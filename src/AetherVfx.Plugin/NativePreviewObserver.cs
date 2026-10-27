using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;

namespace AetherVfx;

// Manual, bounded observation of existing game calls. Never allocates a native view or follows its pointers.
internal sealed class NativePreviewObserver : IDisposable
{
    internal const string BuildHash = "958B78E278AD896493C484EB313FF605AB41EDEFEF74F92B0EE078E69B281BF9";
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Initialize(nint view, nint agent, uint slot, nint callback);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Release(nint view);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint DestroyVfx(nint effect, byte flags);
    internal sealed record Observation(long Sequence, long Call, long Timestamp, int Thread,
        bool FrameworkThread, string Operation, string Phase, long Handle, uint Argument);
    private readonly IFramework framework;
    private readonly ConcurrentQueue<Observation> records = new();
    private Hook<Initialize>? initialize;
    private Hook<Release>? release;
    private Hook<DestroyVfx>? destroy;
    private long sequence, calls, dropped;
    private int count;
    private bool disposed;
    internal long Dropped => Interlocked.Read(ref dropped);
    internal Observation[] Records => records.ToArray();

    internal NativePreviewObserver(ISigScanner scanner, IGameInteropProvider interop, IFramework framework)
    {
        this.framework = framework;
        if (!framework.IsInFrameworkUpdateThread || framework.IsFrameworkUnloading)
            throw new InvalidOperationException("观察器只能在仍运行的 Framework 线程手动启动");
        using var file = File.OpenRead(scanner.Module.FileName);
        if (Convert.ToHexString(SHA256.HashData(file)) != BuildHash)
            throw new NotSupportedException("观察器未验证此客户端文件，未安装任何钩子");
        // Check the loaded module too; an on-disk fingerprint alone cannot identify running code.
        var start = scanner.Module.BaseAddress;
        int pe = Marshal.ReadInt32(start, 0x3c);
        if (pe < 0x40 || pe > scanner.Module.ModuleMemorySize - 0x100
            || Marshal.ReadInt32(start, pe) != 0x4550
            || Marshal.ReadInt32(start, pe + 8) != 0x6ac3dcc1
            || scanner.Module.ModuleMemorySize != 0x3811000)
            throw new NotSupportedException("运行模块头与离线证据不一致");
        nint Checked(int rva, string hex)
        {
            byte[] expected = Convert.FromHexString(hex), actual = new byte[expected.Length];
            if (rva < 0 || rva > scanner.Module.ModuleMemorySize - expected.Length)
                throw new NotSupportedException("观察目标超出模块范围");
            Marshal.Copy(start + rva, actual, 0, actual.Length);
            if (!actual.AsSpan().SequenceEqual(expected))
                throw new NotSupportedException($"观察目标 {rva:X} 已变化或已被其他钩子修改");
            return start + rva;
        }
        var initAddress = Checked(0x9cd2d0, "48895c24084889742410574883ec2048");
        var releaseAddress = Checked(0x9cd3e0, "48895c2408574883ec208b510c33ff48");
        var destroyAddress = Checked(0x40c5a0, "48895c2410574883ec20488d05af09d5");
        try
        {
            initialize = interop.HookFromAddress<Initialize>(initAddress, OnInitialize);
            release = interop.HookFromAddress<Release>(releaseAddress, OnRelease);
            destroy = interop.HookFromAddress<DestroyVfx>(destroyAddress, OnDestroy);
            initialize.Enable(); release.Enable(); destroy.Enable();
        }
        catch (Exception initializationError)
        {
            try { Dispose(); }
            catch (Exception cleanupError) { throw new AggregateException(initializationError, cleanupError); }
            throw;
        }
    }
    private void Record(long call, string operation, string phase, nint handle, uint argument)
    {
        try
        {
            if (Interlocked.Increment(ref count) > 4096) { Interlocked.Increment(ref dropped); return; }
            records.Enqueue(new(Interlocked.Increment(ref sequence), call,
                System.Diagnostics.Stopwatch.GetTimestamp(), Environment.CurrentManagedThreadId,
                framework.IsInFrameworkUpdateThread, operation, phase, handle.ToInt64(), argument));
        }
        catch { Interlocked.Increment(ref dropped); } // Never propagate observation errors across the native boundary.
    }
    private void OnInitialize(nint view, nint agent, uint slot, nint callback)
    {
        long call = Interlocked.Increment(ref calls);
        Record(call, "CharaView.Initialize", "enter", view, slot);
        initialize!.Original(view, agent, slot, callback);
        Record(call, "CharaView.Initialize", "return", view, slot);
    }
    private void OnRelease(nint view)
    {
        long call = Interlocked.Increment(ref calls);
        Record(call, "CharaView.Release", "enter", view, 0);
        release!.Original(view);
        Record(call, "CharaView.Release", "return", view, 0);
    }
    private nint OnDestroy(nint effect, byte flags)
    {
        long call = Interlocked.Increment(ref calls);
        Record(call, "ActorVfx.Destroy", "enter", effect, flags);
        nint result = destroy!.Original(effect, flags);
        Record(call, "ActorVfx.Destroy", "return", effect, flags);
        return result;
    }
    public void Dispose()
    {
        if (disposed) return;
        List<Exception> errors = [];
        void Attempt(IDisposable? hook)
        {
            try { hook?.Dispose(); }
            catch (Exception error) { errors.Add(error); }
        }
        Attempt(destroy); Attempt(release); Attempt(initialize);
        if (errors.Count > 0) throw new AggregateException("观察器钩子清理失败", errors);
        disposed = true;
    }
}
