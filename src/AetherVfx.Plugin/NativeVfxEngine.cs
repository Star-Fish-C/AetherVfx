using AetherVfx.Core;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using System.Runtime.InteropServices;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;

namespace AetherVfx;

internal sealed class NativeVfxEngine(ISigScanner scanner, IGameInteropProvider interop, IPluginLog log) : IDisposable
{
    // Actor create/remove interop follows the MIT-licensed Dalamud-VFXEditor implementation.
    private const string CreateSignature = "40 53 55 56 57 48 81 EC ?? ?? ?? ?? 0F 29 B4 24 ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 0F B6 AC 24 ?? ?? ?? ?? 0F 28 F3 49 8B F8";
    private const string RemoveVtableSignature = "0F 11 48 10 48 8D 05";
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint CreateDelegate([MarshalAs(UnmanagedType.LPUTF8Str)] string path, nint caster, nint target, float duration, byte a5, ushort a6, byte a7);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint RemoveDelegate(nint vfx, byte immediate);
    private CreateDelegate? create;
    private Hook<RemoveDelegate>? remove;
    private readonly NativeVfxRegistry live = new();
    private bool disposed;

    private void EnsureReady()
    {
        NativePreviewSafety.RequireEnabled();
        ObjectDisposedException.ThrowIf(disposed, this);
        if (remove is not null) return;
        nint createAddress = scanner.ScanText(CreateSignature);
        nint relative = scanner.ScanText(RemoveVtableSignature) + 7;
        RequireModuleAddress(relative, 4);
        nint table = relative + 4 + Marshal.ReadInt32(relative);
        RequireModuleAddress(table, IntPtr.Size);
        nint removeAddress = Marshal.ReadIntPtr(table);
        RequireModuleAddress(createAddress, 1);
        RequireModuleAddress(removeAddress, 1);
        if (createAddress == 0 || removeAddress == 0) throw new InvalidOperationException("当前客户端的 VFX 播放接口未匹配，预览未启动");
        var candidate = interop.HookFromAddress<RemoveDelegate>(removeAddress, Removed);
        try
        {
            create = Marshal.GetDelegateForFunctionPointer<CreateDelegate>(createAddress);
            candidate.Enable(); remove = candidate;
            log.Information("AetherVfx 原生 VFX 播放接口已初始化");
        }
        catch { candidate.Dispose(); create = null; throw; }
    }
    private void RequireModuleAddress(nint address, int length)
    {
        long offset = address.ToInt64() - scanner.Module.BaseAddress.ToInt64();
        if (offset < 0 || offset > scanner.Module.ModuleMemorySize - length)
            throw new InvalidOperationException("当前客户端的 VFX 接口地址校验失败，预览未启动");
    }
    public nint Play(string gamePath, nint actor)
    {
        if (actor == 0) throw new InvalidOperationException("预览角色尚未就绪");
        EnsureReady();
        nint pointer = create!(gamePath, actor, actor, -1, 0, 0, 0);
        if (pointer == 0) throw new InvalidOperationException($"游戏未能创建 VFX：path={gamePath}, actor={actor:X}");
        live.Add(pointer);
        log.Information("AetherVfx 已创建原生特效 {Path}, actor={Actor:X}, vfx={Vfx:X}", gamePath, actor, pointer);
        return pointer;
    }
    public bool IsAlive(nint pointer) => pointer != 0 && live.Contains(pointer);
    public unsafe void Center(nint pointer, Vector3 position, float rotation)
    {
        if (!IsAlive(pointer)) return;
        var vfx = (VfxObject*)pointer;
        vfx->Position = position;
        vfx->Rotation = FFXIVClientStructs.FFXIV.Common.Math.Quaternion.CreateFromYawPitchRoll(rotation, 0, 0);
        vfx->UpdateTransforms(true);
    }
    public string Describe(nint pointer)
    {
        if (!IsAlive(pointer)) return "特效对象已结束";
        // Opaque handle only. Never walk VfxResourceInstance -> resource -> handle from this pointer.
        return $"已跟踪 VFX 句柄 0x{pointer:X}；未读取内部资源指针，未验证粒子可见性";
    }
    private nint Removed(nint pointer, byte immediate)
    {
        live.Remove(pointer);
        return remove!.Original(pointer, immediate);
    }
    public void Stop(nint pointer)
    {
        if (pointer == 0 || !live.Remove(pointer)) return;
        remove!.Original(pointer, 1);
    }
    public void Dispose()
    {
        if (disposed) return;
        foreach (var pointer in live.Snapshot()) Stop(pointer);
        disposed = true; remove?.Dispose(); remove = null; create = null;
    }
}
