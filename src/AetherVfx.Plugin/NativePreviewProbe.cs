using AetherVfx.Core;
using System.Text.Json;

namespace AetherVfx;

public sealed partial class Plugin
{
    private NativePreviewObserver? nativeObserver;
    private Func<NativePreviewObserver> createNativeObserver = null!;

    private void QueueObserver(bool start)
    {
        message = start ? "已请求启动原生调用观察；不创建预览资源" : "已请求停止观察并保存记录";
        _ = framework.RunOnFrameworkThread(() =>
        {
            if (disposed) return;
            Run(() =>
            {
                if (start)
                {
                    if (nativeObserver is not null) throw new InvalidOperationException("观察器已运行，请先 observe-stop");
                    nativeObserver = createNativeObserver();
                    message = "正在观察游戏原有调用（可能漏掉派生视图释放）；结束后 /avfxprobe observe-stop";
                }
                else StopObserver();
            });
        });
    }
    private void StopObserver()
    {
        if (nativeObserver is not { } observer) return;
        observer.Dispose();
        var directory = Path.Combine(pi.GetPluginConfigDirectory(), "native-probes");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "observation-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + ".json");
        AtomicFile.Write(path, JsonSerializer.SerializeToUtf8Bytes(new
        {
            Time = DateTimeOffset.UtcNow, ClientHash = NativePreviewObserver.BuildHash,
            StopwatchFrequency = System.Diagnostics.Stopwatch.Frequency,
            observer.Dropped, Events = observer.Records,
            Limitation = "Only sampled calls; derived CharaView release 0x9D3B90 is not observed. No ownership, thread exclusivity or visual acceptance inferred."
        }, ProjectStore.Json));
        nativeObserver = null;
        message = "观察已停止，记录：" + path;
    }
    // Each command represents exactly one explicitly requested case. Never queue a next case.
    private void RunNativeProbe(string command)
    {
        command = command.Trim().ToLowerInvariant();
        if (command == "observe-start") { QueueObserver(true); return; }
        if (command == "observe-stop") { QueueObserver(false); return; }
        if (command == "stop")
        {
            StopPreview();
            if (nativeObserver is not null) QueueObserver(false);
            else message = "手动验证已停止";
            return;
        }
        if (command is not ("view" or "original" or "mapped" or "composition"))
        {
            message = "/avfxprobe view | original | mapped | composition | stop | observe-start | observe-stop；每次只执行一项";
            return;
        }
        var directory = Path.Combine(pi.GetPluginConfigDirectory(), "native-probes");
        Directory.CreateDirectory(directory);
        string record = Path.Combine(directory, DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + ".json");
        try
        {
            windowPreview.Access.RequireEnabled();
            StopPreview();
            layout.Mode = PreviewMode.Window; layout.ShowPreview = true; layout.PreviewPopout = true;
            const string path = "vfx/common/eff/dk10ht_wra0c.avfx";
            if (command == "view") windowPreview.OpenView();
            else if (command == "composition")
            {
                StartPreview();
                if (!coordinator.Active || coordinator.Scope != PreviewScope.Composition)
                    throw new InvalidOperationException("组合未启动：" + string.Join("；", diagnostics.Select(x => x.Message)));
                autoPreview = false; // Further cases and edits require another explicit action.
            }
            else
            {
                var effect = RawEffectLoader.Load(resources, ResourceReference.GamePath(path));
                coordinator.Play(PreviewMode.Window, PreviewScope.Asset,
                    new("manual-" + command, "角色光环手动验证", effect, command == "original" ? path : null));
            }
            message = "已提交单项验证；请记录画面，提交成功不代表粒子可见";
        }
        catch (Exception ex) { message = "手动验证未启动／失败：" + ex.Message; }
        finally
        {
            AtomicFile.Write(record, JsonSerializer.SerializeToUtf8Bytes(new
            {
                Time = DateTimeOffset.UtcNow, Case = command, Result = message,
                Compatibility = NativePreviewSafety.Compatibility, ExperimentEnabled = windowPreview.Access.ExperimentEnabled,
                Preview = windowPreview.Snapshot, VisualAcceptance = "PendingGameValidation"
            }, ProjectStore.Json));
        }
    }
}
