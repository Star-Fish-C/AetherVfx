namespace AetherVfx.Core;

public enum PreviewMode { Self, Window }
public enum PreviewScope { Asset, Composition }
// Preview is intentionally independent of equipment targets and IMC manipulations.
public sealed record PreviewPayload(string Id, string Name, CompiledEffect Effect, string? NativeGamePath = null);
public interface IPreviewSurface
{
    bool Active { get; }
    void Play(PreviewPayload payload);
    void Stop();
}

public sealed record PreviewResources(string VfxPath, IReadOnlyDictionary<string, byte[]> Files)
{
    public static PreviewResources FromEffect(CompiledEffect effect)
    {
        _ = Blocks.Root(effect.Avfx);
        string path = "vfx/aethervfx/eff/" + ResourceCatalog.Hash(effect.Avfx) + ".avfx";
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (dependency, bytes) in effect.Dependencies)
        { EffectCompiler.CheckGamePath(dependency); files.Add(dependency, bytes); }
        files[path] = effect.Avfx;
        return new(path, files);
    }
}

// Engine removal notifications invalidate native pointers before a subsequent Stop can reuse them.
public sealed class NativeVfxRegistry
{
    private readonly object sync = new();
    private readonly HashSet<nint> live = [];
    public void Add(nint pointer)
    {
        if (pointer == 0) throw new ArgumentException("VFX 创建返回空指针", nameof(pointer));
        lock (sync) if (!live.Add(pointer)) throw new InvalidOperationException("VFX 指针已被跟踪");
    }
    public bool Contains(nint pointer) { lock (sync) return live.Contains(pointer); }
    public bool Remove(nint pointer) { lock (sync) return live.Remove(pointer); }
    public nint[] Snapshot() { lock (sync) return live.ToArray(); }
}

public sealed class PreviewCoordinator(IReadOnlyDictionary<PreviewMode, IPreviewSurface> surfaces)
{
    public PreviewMode? Mode { get; private set; }
    public PreviewScope? Scope { get; private set; }
    public string? ResourceId { get; private set; }
    public bool Active => Mode is { } mode && surfaces[mode].Active;
    public void Play(PreviewMode mode, PreviewScope scope, PreviewPayload payload)
    {
        Stop();
        var surface = surfaces[mode];
        try { surface.Play(payload); Mode = mode; Scope = scope; ResourceId = payload.Id; }
        catch { surface.Stop(); throw; }
    }
    public void Stop()
    {
        if (Mode is { } mode) surfaces[mode].Stop();
        Mode = null; Scope = null; ResourceId = null;
    }
}

public static class RawEffectLoader
{
    public static CompiledEffect Load(IResources resources, ResourceReference reference)
    {
        var bytes = resources.Read(reference);
        var root = Blocks.Root(bytes);
        // Browsing is independent of editable-subset restrictions in EffectCompiler.
        var dependencies = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var block in root.Where(x => x.Tag == "Tex").Concat(root.Where(x => x.Tag == "Emit").SelectMany(x => x.Children().Where(b => b.Tag == "SdNm"))))
        {
            string path = System.Text.Encoding.UTF8.GetString(block.Data).TrimEnd('\0');
            if (path.Length == 0) continue;
            EffectCompiler.CheckGamePath(path); dependencies.TryAdd(path, resources.Read(path));
        }
        return new(bytes, dependencies);
    }
}
