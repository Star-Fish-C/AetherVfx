using AetherVfx.Core;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using System.Numerics;

namespace AetherVfx;

// No native allocator is installed until ownership, release and ABI have been verified.
// The previous implementation initialized shared slots 5-7 before proving ownership.
internal sealed class WindowPreviewAdapter : IPreviewSurface, IDisposable
{
    private readonly PreviewSession session = new();
    private readonly PreviewManager legacyRecovery;
    public PreviewAccess Access { get; } = new(NativePreviewSafety.Compatibility);
    // A failed cleanup still owns resources; retain the coordinator's surface identity.
    public bool Active => session.Active || session.CleanupPending;
    public PreviewStatus Snapshot => session.Status;
    public string Status => Snapshot.Message;
    public string Diagnostic => Access.Diagnostic + "；视觉验收=" + Snapshot.VisualAcceptance;

    public WindowPreviewAdapter(IDalamudPluginInterface pi, IObjectTable objects, IClientState client,
        ICondition condition, NativeVfxEngine engine, IPluginLog log)
    {
        legacyRecovery = new(pi, objects, client, condition, engine, log, "window");
        session.Disable(Access.Diagnostic);
    }
    public void Recover() => legacyRecovery.Recover();
    public void Play(PreviewPayload payload) => StartUnavailable();
    public void OpenView() => StartUnavailable();
    private void StartUnavailable()
    {
        long ticket = session.Begin(Access);
        // Even a future compatibility change alone must not install an unverified native backend.
        const string reason = "独立视图后端尚未接入已验证的申请／释放协议";
        session.Fail(ticket, reason);
        throw new NotSupportedException(reason);
    }
    public void SetExperimentEnabled(bool enabled)
    {
        Access.ExperimentEnabled = enabled;
        session.Disable(Access.Diagnostic);
    }
    public void Draw(Vector2 size)
    {
        ImGui.TextWrapped(Status);
        ImGui.TextWrapped("首版目标：角色与特效同屏。当前兼容性门槛未通过，未申请原生视图。");
        ImGui.Dummy(new(Math.Max(1, size.X), Math.Max(1, size.Y - 65)));
    }
    public void Stop()
    {
        session.Stop();
        if (session.CleanupPending) throw new InvalidOperationException(session.Status.Message);
        if (!Access.CanStart) session.Disable(Access.Diagnostic);
    }
    public void Dispose() { session.Dispose(); legacyRecovery.Dispose(); }
}
