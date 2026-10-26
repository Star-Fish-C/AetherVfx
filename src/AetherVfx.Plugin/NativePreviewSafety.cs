namespace AetherVfx;

internal static class NativePreviewSafety
{
    // Re-enable only after offline ABI/lifetime analysis and a separately agreed runtime validation session.
    public static bool Enabled => false;
    public const string Message = "原生预览已因客户端崩溃禁用；目录、工程编辑和导出仍可使用";
    public static void RequireEnabled()
    {
        if (!Enabled) throw new InvalidOperationException(Message);
    }
}
