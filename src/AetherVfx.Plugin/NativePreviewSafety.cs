using AetherVfx.Core;

namespace AetherVfx;

internal static class NativePreviewSafety
{
    // Re-enable only after offline ABI/lifetime analysis and a separately agreed runtime validation session.
    // Keep all runtime gates closed. Per-build ABI evidence is recorded in NATIVE-BINARY-20261027.md;
    // a process-independent boolean must not approve another executable after an update.
    public static PreviewCompatibility Compatibility { get; } = new(false, false, false, false, false);
    public static bool Enabled => Compatibility.Supported;
    public const string Message = "原生预览已因客户端崩溃禁用；目录、工程编辑和导出仍可使用";
    public static void RequireEnabled()
    {
        if (!Enabled) throw new InvalidOperationException(Message);
    }
}
