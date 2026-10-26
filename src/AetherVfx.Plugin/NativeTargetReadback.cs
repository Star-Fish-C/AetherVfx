using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;
using System.Runtime.InteropServices;

namespace AetherVfx;

// Retains the actual offscreen SRV for acceptance readback; never captures the world viewport.
internal sealed class NativeTargetReadback : IDalamudTextureWrap
{
    private nint pointer;
    public NativeTargetReadback(nint shaderResourceView, int width, int height)
    {
        if (shaderResourceView == 0 || width <= 0 || height <= 0) throw new ArgumentException("预览渲染目标尚未就绪");
        Marshal.AddRef(shaderResourceView);
        pointer = shaderResourceView; Width = width; Height = height;
    }
    public ImTextureID Handle => new(pointer);
    public int Width { get; }
    public int Height { get; }
    public void Dispose()
    {
        nint old = Interlocked.Exchange(ref pointer, 0);
        if (old != 0) Marshal.Release(old);
    }
}
