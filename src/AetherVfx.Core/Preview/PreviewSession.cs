namespace AetherVfx.Core;

public enum PreviewState { Disabled, Idle, Initializing, Playing, Failed }
public sealed record PreviewStatus(PreviewState State, string Message, bool ViewReady = false, bool VfxCreated = false)
{
    // Creating a handle or a texture is never evidence of visible particles.
    public string VisualAcceptance => "PendingGameValidation";
}

public sealed record PreviewCompatibility(bool ActorAbi, bool ActorLifetime, bool ViewOwnership, bool ViewRelease, bool FrameworkThread)
{
    public bool Supported => ActorAbi && ActorLifetime && ViewOwnership && ViewRelease && FrameworkThread;
    public string Diagnostic => Supported ? "离线兼容检查通过；粒子画面仍待游戏内验收" :
        "原生预览停用：" + string.Join("、", new[]
        {
            ActorAbi ? null : "ActorVFX ABI 未确认", ActorLifetime ? null : "VFX 生命周期未确认",
            ViewOwnership ? null : "视图独占申请协议未确认", ViewRelease ? null : "视图释放协议未确认",
            FrameworkThread ? null : "原生调用线程时序未确认"
        }.Where(x => x is not null));
}

// Per-plugin instance, deliberately not serialized. An opt-in cannot bypass compatibility.
public sealed class PreviewAccess(PreviewCompatibility compatibility)
{
    public bool ExperimentEnabled { get; set; }
    public bool CanStart => ExperimentEnabled && compatibility.Supported;
    public string Diagnostic => !compatibility.Supported ? compatibility.Diagnostic :
        ExperimentEnabled ? compatibility.Diagnostic : "请手动启用本次会话的实验预览";
    public void RequireEnabled()
    {
        if (!CanStart) throw new InvalidOperationException(Diagnostic);
    }
}

// Opt in only when a failed Dispose leaves ownership intact and retrying it is safe.
public interface IRetryablePreviewLease : IDisposable { }

// Framework-thread confined. Async producers must dispatch completion back to that thread.
// Leases must represent resources the caller actually owns; no borrowed native pointer is a lease.
public sealed class PreviewSession : IDisposable
{
    private static readonly TimeSpan ViewInitializationTimeout = TimeSpan.FromSeconds(30);
    private readonly TimeProvider clock;
    private readonly LinkedList<IDisposable> leases = new();
    private readonly HashSet<IDisposable> ownedLeases = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<IDisposable> failedLeases = new(ReferenceEqualityComparer.Instance);
    private long generation;
    private long initializationStarted;
    private bool disposed, cleanupFailed, releasing;
    public long Generation => generation;
    public PreviewStatus Status { get; private set; } = new(PreviewState.Idle, "预览已停止");
    public bool Active => Status.State is PreviewState.Initializing or PreviewState.Playing;
    public bool CleanupPending => cleanupFailed;

    public PreviewSession(TimeProvider? clock = null) => this.clock = clock ?? TimeProvider.System;

    public long Begin(PreviewAccess access)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (releasing) throw new InvalidOperationException("资源清理进行中，不能启动预览");
        Stop();
        if (cleanupFailed) throw new InvalidOperationException(Status.Message);
        if (!access.CanStart)
        {
            Status = new(PreviewState.Disabled, access.Diagnostic);
            access.RequireEnabled();
        }
        initializationStarted = clock.GetTimestamp();
        Status = new(PreviewState.Initializing, "正在初始化独立预览");
        return generation;
    }

    // Called on the same controlled thread as Begin/Stop, never from a timer callback.
    public void Tick()
    {
        if (Status.State == PreviewState.Initializing && !Status.ViewReady &&
            clock.GetElapsedTime(initializationStarted, clock.GetTimestamp()) >= ViewInitializationTimeout)
            Fail(generation, "视图初始化超时");
    }

    public bool IsCurrent(long ticket) => !disposed && Active && ticket == generation;
    public bool Own(long ticket, IDisposable lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (ownedLeases.Contains(lease)) throw new InvalidOperationException("不能重复接管同一个资源租约");
        if (!IsCurrent(ticket))
        {
            try { lease.Dispose(); }
            catch (Exception ex)
            {
                // A late resource can still depend on live resources; retain it and the whole
                // current stack rather than tearing down dependencies underneath it.
                generation++;
                leases.AddLast(lease);
                ownedLeases.Add(lease);
                failedLeases.Add(lease);
                cleanupFailed = true;
                Status = new(PreviewState.Failed, "过期资源释放失败，已保留资源及依赖并禁止重新播放：" + ex.Message);
            }
            return false;
        }
        leases.AddLast(lease);
        ownedLeases.Add(lease);
        return true;
    }
    public bool MarkViewReady(long ticket)
    {
        if (!IsCurrent(ticket)) return false;
        Status = Status with { ViewReady = true, Message = "角色视图就绪；粒子画面待验收" };
        return true;
    }
    public bool MarkVfxCreated(long ticket)
    {
        if (!IsCurrent(ticket)) return false;
        if (!Status.ViewReady) throw new InvalidOperationException("角色视图尚未就绪");
        Status = Status with { State = PreviewState.Playing, VfxCreated = true, Message = "VFX 已创建；粒子画面待验收" };
        return true;
    }
    public void Fail(long ticket, string message)
    {
        if (!IsCurrent(ticket)) return;
        Stop();
        Status = new(PreviewState.Failed, cleanupFailed ? message + "；" + Status.Message : message);
    }
    public void Disable(string message)
    {
        Stop();
        if (!cleanupFailed) Status = new(PreviewState.Disabled, message);
    }
    public void Stop()
    {
        generation++;
        if (cleanupFailed || releasing) return; // Never retry an arbitrary IDisposable implicitly.
        ReleaseInOrder(false);
    }
    public void RetryFailedCleanup()
    {
        if (releasing) throw new InvalidOperationException("资源清理进行中，不能重试");
        if (!cleanupFailed) return;
        if (leases.Last?.Value is not IRetryablePreviewLease)
            throw new InvalidOperationException("失败资源未声明安全重试，保留该资源及其依赖");
        cleanupFailed = false;
        ReleaseInOrder(true);
    }
    private void ReleaseInOrder(bool retryFirst)
    {
        releasing = true;
        try
        {
            while (leases.Last is { } node)
            {
                var lease = node.Value;
                if (failedLeases.Contains(lease))
                {
                    if (!retryFirst || lease is not IRetryablePreviewLease)
                    {
                        cleanupFailed = true;
                        Status = new(PreviewState.Failed, "资源清理仍待处理，已保留该资源及依赖");
                        return;
                    }
                    failedLeases.Remove(lease);
                    retryFirst = false;
                }
                try { lease.Dispose(); leases.Remove(node); ownedLeases.Remove(lease); }
                catch (Exception ex)
                {
                    // Keep the failed lease and all earlier leases it may depend on.
                    failedLeases.Add(lease);
                    cleanupFailed = true;
                    Status = new(PreviewState.Failed, "资源释放失败，已保留资源及依赖并禁止重新播放：" + ex.Message);
                    return;
                }
                // A synchronous callback may have delivered a late lease that failed cleanup.
                if (cleanupFailed) return;
            }
            Status = new(PreviewState.Idle, "预览已停止");
        }
        finally { releasing = false; }
    }
    public void Dispose()
    {
        if (releasing) throw new InvalidOperationException("资源清理进行中，不能卸载预览会话");
        if (disposed && !cleanupFailed) return;
        if (!disposed) Stop();
        if (cleanupFailed) throw new InvalidOperationException(Status.Message);
        disposed = true;
    }
}
