using AetherVfx.Core;
using Dalamud.Plugin.Services;
using System.Text.Json;

namespace AetherVfx;

public sealed partial class Plugin
{
    private ITextureReadbackProvider readback = null!;
    private readonly List<object> probeEvents = [];
    private readonly Queue<NativeProbeCase> probeCases = new();
    private NativeProbeCase? probeCase;
    private string probeDirectory = "", probeId = "";
    private DateTime probeStarted, probeReady;
    private int probeFrame;
    private Task? probeReadback;
    private sealed record NativeProbeCase(string Name, string Path, bool OriginalPath, bool HideCarrier, bool Self = false);

    private void StartNativeProbe()
    {
        if (!NativePreviewSafety.Enabled) { message = NativePreviewSafety.Message; return; }
        if (probeCase is not null || probeReadback?.IsCompleted == false) throw new InvalidOperationException("原生探针仍在运行，请等待结束");
        if (objects.LocalPlayer is null) throw new InvalidOperationException("请先登录角色");
        coordinator.Stop(); autoPreview = false;
        probeDirectory = Path.Combine(pi.GetPluginConfigDirectory(), "native-probes", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(probeDirectory); probeEvents.Clear(); probeCases.Clear();
        const string aura = "vfx/common/eff/dk10ht_wra0c.avfx";
        const string ground = "vfx/common/eff/pop_tlep1t0h.avfx";
        probeCases.Enqueue(new("self-original-control", aura, true, false, true));
        probeCases.Enqueue(new("self-mapped-control", aura, false, false, true));
        probeCases.Enqueue(new("aura-original-visible", aura, true, false));
        probeCases.Enqueue(new("aura-original-hidden", aura, true, true));
        probeCases.Enqueue(new("aura-mapped-hidden", aura, false, true));
        probeCases.Enqueue(new("ground-original-hidden", ground, true, true));
        AddAssociatedSample(AssociationKind.Monster, "monster");
        AddAssociatedSample(AssociationKind.Region, "region");
        layout.Mode = PreviewMode.Window; layout.ShowPreview = true; layout.PreviewPopout = true;
        probeEvents.Add(new { Event = "start", Time = DateTimeOffset.Now, Dalamud = typeof(IFramework).Assembly.GetName().Version?.ToString(), ClientStructs = typeof(FFXIVClientStructs.FFXIV.Client.UI.Misc.CharaView).Assembly.GetName().Version?.ToString(), Fingerprint = archive?.Fingerprint, Cases = probeCases.ToArray(), VisualAcceptance = "NotVerified" });
        log.Information("AetherVfx 原生探针开始，输出目录：{Directory}", probeDirectory);
        NextProbeCase();
        void AddAssociatedSample(AssociationKind kind, string name)
        {
            foreach (var record in resourceCatalog.Snapshot().Where(r => r.Associations.Any(a => a.Kind == kind)).OrderBy(r => r.Id).Take(100))
            {
                var reference = record.References.FirstOrDefault(r => r.Kind == ResourceKind.GamePath);
                if (reference is null) continue;
                try
                {
                    var effect = RawEffectLoader.Load(resources, reference);
                    if (Blocks.Root(effect.Avfx).Any(b => b.Tag == "Efct")) continue;
                    probeCases.Enqueue(new(name + "-original-hidden", reference.Path, true, true));
                    return;
                }
                catch (IOException) { }
                catch (InvalidDataException) { }
            }
            probeEvents.Add(new { Event = "missing-sample", Kind = kind.ToString(), Reason = "未找到有可靠关联且无屏幕效果节点的已知路径样本" });
        }
    }
    private void NextProbeCase()
    {
        coordinator.Stop();
        if (!probeCases.TryDequeue(out var next))
        {
            probeCase = null; SaveProbeEvents();
            message = "原生探针结束，已保存渲染目标供检查；尚未自动判定粒子可见：" + probeDirectory;
            log.Information("AetherVfx {Message}", message); return;
        }
        probeCase = next; probeFrame = 0; probeReady = DateTime.MaxValue; probeStarted = DateTime.UtcNow;
        probeId = "native-probe-" + next.Name;
        try
        {
            var effect = RawEffectLoader.Load(resources, ResourceReference.GamePath(next.Path));
            windowPreview.EmptyScene = next.HideCarrier;
            coordinator.Play(next.Self ? PreviewMode.Self : PreviewMode.Window, PreviewScope.Asset, new(probeId, next.Name, effect, next.OriginalPath ? next.Path : null));
            probeEvents.Add(new { Event = "case-start", Case = next, Hash = ResourceCatalog.Hash(effect.Avfx), Dependencies = effect.Dependencies.Keys.ToArray() });
        }
        catch (Exception ex)
        {
            probeEvents.Add(new { Event = "case-failed", Case = next.Name, Error = ex.ToString() });
            log.Warning(ex, "AetherVfx 原生探针未通过，停止整组验证：{Sample}", next.Name);
            probeCases.Clear(); probeCase = null;
            message = "独立原生验证未通过：" + ex.Message;
            coordinator.Stop();
        }
        SaveProbeEvents();
    }
    private void TickNativeProbe()
    {
        if (probeCase is not { } current || probeReadback?.IsCompleted == false) return;
        if (coordinator.ResourceId != probeId)
        {
            probeEvents.Add(new { Event = "cancelled", Reason = "预览被切换、关闭，或场景生命周期终止", Scene = windowPreview.SceneDiagnostic, Status = windowPreview.Status, Error = windowPreview.EffectCreationError });
            probeCase = null; probeCases.Clear(); SaveProbeEvents(); return;
        }
        if (current.Self)
        {
            if (DateTime.UtcNow - probeStarted < TimeSpan.FromSeconds(2)) return;
            probeEvents.Add(new { Event = "self-control", Case = current.Name, Diagnostic = preview.Diagnostic, Status = preview.Status });
            NextProbeCase(); return;
        }
        if (DateTime.UtcNow - probeStarted > TimeSpan.FromSeconds(30))
        {
            probeEvents.Add(new { Event = "timeout", Case = current.Name, Scene = windowPreview.SceneDiagnostic, Resource = windowPreview.Diagnostic });
            NextProbeCase(); return;
        }
        if (!windowPreview.RenderTargetReady) return;
        if (probeReady == DateTime.MaxValue) probeReady = DateTime.UtcNow;
        if (DateTime.UtcNow - probeReady < TimeSpan.FromSeconds(.5 + probeFrame)) return;
        if (probeFrame >= 3) { NextProbeCase(); return; }
        string path = Path.Combine(probeDirectory, current.Name + "-" + probeFrame++ + ".png");
        var wrap = windowPreview.RetainRenderTarget();
        string scene = windowPreview.SceneDiagnostic, resource = windowPreview.Diagnostic + "；创建错误=" + windowPreview.EffectCreationError;
        probeReadback = SaveNativeTargetAsync(wrap, path, current.Name, scene, resource);
    }
    private async Task SaveNativeTargetAsync(NativeTargetReadback wrap, string path, string name, string scene, string resource)
    {
        try
        {
            // WIC PNG encoder. Read back this offscreen target only, never the main viewport.
            await readback.SaveToFileAsync(wrap, new Guid("1b7cfaf4-713f-473c-bbcd-6137425faeaf"), path, null, true);
            lock (probeEvents) probeEvents.Add(new { Event = "frame", Case = name, File = path, Scene = scene, Resource = resource });
            log.Information("AetherVfx 原生离屏帧：{Path}；{Scene}；{Resource}", path, scene, resource);
        }
        catch (Exception ex)
        {
            lock (probeEvents) probeEvents.Add(new { Event = "readback-failed", Case = name, Error = ex.ToString() });
            log.Error(ex, "AetherVfx 原生渲染目标回读失败");
        }
        finally { wrap.Dispose(); }
    }
    private void SaveProbeEvents()
    {
        lock (probeEvents) AtomicFile.Write(Path.Combine(probeDirectory, "events.json"), JsonSerializer.SerializeToUtf8Bytes(probeEvents, ProjectStore.Json));
    }
}
