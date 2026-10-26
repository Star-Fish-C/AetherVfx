using AetherVfx.Core;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Penumbra.Api.Enums;
using Penumbra.Api.IpcSubscribers;

namespace AetherVfx;

// Maps a private AVFX resource, then creates an actor VFX directly. No equipment or redraw calls.
internal sealed class PreviewManager(IDalamudPluginInterface pi, IObjectTable objects, IClientState client,
    ICondition condition, NativeVfxEngine engine, IPluginLog log, string sessionName = "self") : IDisposable, IPreviewSurface
{
    private readonly CreateTemporaryCollection create = new(pi);
    private readonly DeleteTemporaryCollection delete = new(pi);
    private readonly AssignTemporaryCollection assign = new(pi);
    private readonly AddTemporaryMod add = new(pi);
    private readonly RemoveTemporaryMod remove = new(pi);
    private readonly ResolveGameObjectPath resolve = new(pi);
    private readonly GetCollectionForObject collectionForObject = new(pi);
    private readonly string markerPath = Path.Combine(pi.GetPluginConfigDirectory(), "preview-session-" + sessionName + ".json");
    private readonly string tag = "AetherVfx.Preview." + sessionName;
    private const int Priority = 1000;
    private Guid collection;
    private bool ownsCollection, isSelf;
    private nint actor, effect;
    private uint territory;
    private bool gpose;
    private DateTime nextDiagnostic;
    public bool CenterOnActor { get; set; } = true;
    public string Diagnostic { get; private set; } = "尚未播放";
    public bool Active => effect != 0 || collection != Guid.Empty;
    public string Status { get; private set; } = "预览已停止";

    public void Recover()
    {
        string path = markerPath;
        if (!File.Exists(path) && sessionName == "self") path = Path.Combine(pi.GetPluginConfigDirectory(), "preview-session.json");
        if (!File.Exists(path)) return;
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        Guid saved = json.RootElement.GetProperty("Collection").GetGuid();
        bool owned = !json.RootElement.TryGetProperty("OwnsCollection", out var flag) || flag.GetBoolean();
        if (saved == Guid.Empty) return;
        if (owned) Check(delete.Invoke(saved)); else Check(remove.Invoke(tag, saved, Priority));
        File.Delete(path); Status = "已清理上次预览的资源映射";
    }
    public void Play(PreviewPayload payload)
    {
        var player = objects.LocalPlayer ?? throw new InvalidOperationException("请先登录角色");
        if (payload.NativeGamePath is { } path)
        {
            Stop(); EffectCompiler.CheckGamePath(path);
            if (condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas] || condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas51])
                throw new InvalidOperationException("换区期间不能预览");
            actor = player.Address; isSelf = true; territory = client.TerritoryType; gpose = client.IsGPosing;
            effect = engine.Play(path, actor);
            if (CenterOnActor) Center(player.Position, player.Rotation);
            Status = "原始游戏路径对照播放 · 不使用临时文件映射";
            Diagnostic = "等待资源状态；请与常规素材预览对照";
            nextDiagnostic = DateTime.UtcNow.AddSeconds(1);
            log.Information("AetherVfx 原始路径对照：{Path}，角色坐标={Position}", path, player.Position);
            return;
        }
        ShowFor(payload.Effect, player.ObjectIndex, player.Address, true);
    }
    public void ShowFor(CompiledEffect compiled, int objectIndex, nint address, bool self, string? nativePath = null)
    {
        Stop();
        if (condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas] || condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas51])
            throw new InvalidOperationException("换区期间不能预览");
        try
        {
            if (nativePath is not null)
            {
                EffectCompiler.CheckGamePath(nativePath);
                actor = address; isSelf = self; territory = client.TerritoryType; gpose = client.IsGPosing;
                effect = engine.Play(nativePath, address);
                nextDiagnostic = DateTime.UtcNow.AddSeconds(1);
                Diagnostic = "原始路径对照；等待资源状态";
                Status = "正在进行原始路径渲染验证";
                return;
            }
            var resources = PreviewResources.FromEffect(compiled);
            var current = collectionForObject.Invoke(objectIndex);
            collection = current.Item3.Id;
            // Keep the current appearance/mod collection; only an empty collection needs an isolated fallback.
            ownsCollection = collection == Guid.Empty;
            if (ownsCollection) Check(create.Invoke(tag, "VFX 预览资源", out collection));
            AtomicFile.Write(markerPath, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new PreviewSession(collection, ownsCollection)));
            var directory = Path.Combine(pi.GetPluginConfigDirectory(), "preview", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var files = new Dictionary<string, string>();
            foreach (var (gamePath, bytes) in resources.Files)
            {
                var local = Path.Combine(directory, ResourceCatalog.Hash(bytes) + Path.GetExtension(gamePath));
                if (!File.Exists(local)) File.WriteAllBytes(local, bytes);
                files.Add(gamePath, local);
            }
            Check(add.Invoke(tag, collection, files, PmpExporter.CompressedManipulations([]), Priority));
            if (ownsCollection) Check(assign.Invoke(collection, objectIndex, true));
            string resolved = resolve.Invoke(resources.VfxPath, objectIndex);
            if (!Path.IsPathFullyQualified(resolved) || !string.Equals(Path.GetFullPath(resolved), Path.GetFullPath(files[resources.VfxPath]), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("预览资源重定向未生效，未创建特效。请检查 Penumbra 是否启用及角色集合状态");
            actor = address; isSelf = self; territory = client.TerritoryType; gpose = client.IsGPosing;
            effect = engine.Play(resources.VfxPath, address);
            if (self && objects.LocalPlayer is { } player && CenterOnActor) Center(player.Position, player.Rotation);
            nextDiagnostic = DateTime.UtcNow.AddSeconds(1);
            Diagnostic = "等待 AVFX 资源加载；已创建对象不等于画面可见";
            Status = "已提交 VFX 播放 · 单次特效结束后可重新播放";
            log.Information("AetherVfx 直接预览资源已解析：{Path} -> {Resolved}, collection={Collection}", resources.VfxPath, resolved, collection);
        }
        catch { Stop(); throw; }
    }
    public void Tick()
    {
        if (!Active) return;
        if (isSelf && objects.LocalPlayer?.Address != actor || client.TerritoryType != territory || client.IsGPosing != gpose || condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas] || condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas51])
        { Stop(); Status = "角色／场景已变化，预览已清理"; return; }
        if (effect != 0 && !engine.IsAlive(effect))
        {
            effect = 0; Stop(); Status = "本次特效对象已结束；若始终无画面，请查看播放诊断";
            log.Information("AetherVfx 特效对象结束；最后诊断：{Diagnostic}", Diagnostic); return;
        }
        if (isSelf && CenterOnActor && objects.LocalPlayer is { } player) Center(player.Position, player.Rotation);
        if (DateTime.UtcNow >= nextDiagnostic)
        {
            Diagnostic = engine.Describe(effect);
            nextDiagnostic = DateTime.MaxValue;
            log.Information("AetherVfx 播放诊断：{Diagnostic}", Diagnostic);
        }
    }
    public void Center(System.Numerics.Vector3 position, float rotation)
    { if (CenterOnActor) engine.Center(effect, position, rotation); }
    public void Stop()
    {
        nint oldEffect = effect; effect = 0;
        engine.Stop(oldEffect);
        if (collection == Guid.Empty) return;
        if (ownsCollection) Check(delete.Invoke(collection)); else Check(remove.Invoke(tag, collection, Priority));
        collection = Guid.Empty; ownsCollection = false; actor = 0;
        if (File.Exists(markerPath)) File.Delete(markerPath);
        Status = "预览已停止，资源映射已清理";
    }
    private static void Check(PenumbraApiEc result)
    {
        if (result is not PenumbraApiEc.Success and not PenumbraApiEc.NothingChanged)
            throw new InvalidOperationException($"Penumbra 操作失败：{result}");
    }
    public void Dispose() => Stop();
    private sealed record PreviewSession(Guid Collection, bool OwnsCollection);
}
