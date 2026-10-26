namespace AetherVfx;

internal static class PluginAssetPaths
{
    // Dalamud can load assembly bytes into memory, leaving Assembly.Location empty.
    // Only the host-provided installation path is authoritative for adjacent assets.
    public static string AssetsDirectory(FileInfo installationAssembly)
    {
        ArgumentNullException.ThrowIfNull(installationAssembly);
        var directory = installationAssembly.Directory
            ?? throw new ArgumentException("Dalamud 提供的插件安装路径没有父目录", nameof(installationAssembly));
        return Path.Combine(directory.FullName, "assets");
    }
}
