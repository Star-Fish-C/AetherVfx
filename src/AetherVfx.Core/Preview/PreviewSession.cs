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

// Framework-thread confined. Async producers must dispatch completion back to that thread.
// Leases must represent resources the caller actually owns; no borrowed native pointer is a lease.
public sealed class PreviewSession : IDisposable
{
    private readonly Stack<IDisposable> leases = new();
    private long generation;
    private bool disposed, cleanupFailed;
    public long Generation => generation;
    public PreviewStatus Status { get; private set; } = new(PreviewState.Idle, "预览已停止");
    public bool Active => Status.State is PreviewState.Initializing or PreviewState.Playing;

    public long Begin(PreviewAccess access)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Stop();
        if (cleanupFailed) throw new InvalidOperationException(Status.Message);
        if (!access.CanStart)
        {
            Status = new(PreviewState.Disabled, access.Diagnostic);
            access.RequireEnabled();
        }
        Status = new(PreviewState.Initializing, "正在初始化独立预览");
        return generation;
    }

    public bool IsCurrent(long ticket) => !disposed && Active && ticket == generation;
    public bool Own(long ticket, IDisposable lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (!IsCurrent(ticket))
        {
            try { lease.Dispose(); }
            catch (Exception ex)
            {
                Stop(); cleanupFailed = true;
                Status = new(PreviewState.Failed, "过期资源释放失败，禁止重新播放：" + ex.Message + "；" + Status.Message);
            }
            return false;
        }
        leases.Push(lease);
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
        var failures = new List<string>();
        while (leases.TryPop(out var lease))
        {
            try { lease.Dispose(); }
            catch (Exception ex) { failures.Add(ex.Message); }
        }
        if (failures.Count > 0)
        {
            cleanupFailed = true;
            Status = new(PreviewState.Failed, "资源释放失败，禁止重新播放：" + string.Join("；", failures));
        }
        else if (!cleanupFailed && Status.State != PreviewState.Failed) Status = new(PreviewState.Idle, "预览已停止");
    }
    public void Dispose()
    {
        if (disposed) return;
        Stop(); disposed = true;
    }
}
