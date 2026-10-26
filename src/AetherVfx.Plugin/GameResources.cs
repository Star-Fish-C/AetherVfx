using AetherVfx.Core;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Penumbra.Api.IpcSubscribers;

namespace AetherVfx;

internal sealed class GameResources(IDataManager data, IDalamudPluginInterface pi, Func<AetherVfx.GameData.SqpackArchiveSource?> archive) : IResources
{
    private readonly ResolvePlayerPath resolve = new(pi);
    public byte[] Read(ResourceReference reference) => reference.Kind == ResourceKind.GamePath ? Read(reference.Path)
        : (archive() ?? throw new InvalidDataException("归档资源索引正在初始化")).Read(reference);
    public byte[] Read(string gamePath)
    {
        EffectCompiler.CheckGamePath(gamePath);
        string resolved;
        try { resolved = resolve.Invoke(gamePath); }
        catch (Exception ex) { throw new IOException("需要启用 Penumbra 才能读取当前角色资源", ex); }
        if (Path.IsPathFullyQualified(resolved))
            return File.ReadAllBytes(resolved);
        return data.GetFile(string.IsNullOrEmpty(resolved) ? gamePath : resolved)?.Data
            ?? throw new FileNotFoundException($"缺少资源：{gamePath}");
    }
}
