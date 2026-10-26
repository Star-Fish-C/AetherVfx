using System.Text.Json;

namespace AetherVfx.Core;

public sealed class ResourceIndexService(IArchiveSource source, ResourceCatalog catalog, string cachePath) : IDisposable
{
    private readonly ManualResetEventSlim resume = new(true);
    private readonly object sync = new();
    private readonly object saveLock = new();
    private CancellationTokenSource? cancellation;
    private Task? task;
    private ScanProgress progress = new(ScanState.Idle, 0, 0, 0, 0, "尚未扫描");
    private int cursor, failed;
    private readonly List<string> failures = [];
    private bool disposed;
    public ScanProgress Progress { get { lock (sync) return progress; } }
    public Task? Completion { get { lock (sync) return task; } }
    public string[] Failures { get { lock (sync) return failures.ToArray(); } }
    public bool LoadCache()
    {
        if (!File.Exists(cachePath)) return false;
        var cache = JsonSerializer.Deserialize<IndexCache>(File.ReadAllText(cachePath), ProjectStore.Json) ?? throw new InvalidDataException("索引缓存为空");
        if (cache.Version != 1 || cache.Fingerprint != source.Fingerprint)
        { SetProgress(new(ScanState.Idle, 0, 0, catalog.Count, 0, "游戏资源包已改变，旧归档偏移已失效，请重新扫描")); return false; }
        foreach (var record in cache.Records) catalog.Upsert(record);
        cursor = cache.Cursor; failed = cache.Failed; failures.AddRange(cache.Failures);
        SetProgress(new(ScanState.Idle, cursor, 0, catalog.Count, failed, "已读取缓存，可继续扫描验证覆盖率"));
        return true;
    }
    public void Start(bool rebuild = false)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (task is { IsCompleted: false }) { resume.Set(); progress = progress with { State = ScanState.Running }; return; }
            if (rebuild) { cursor = 0; failed = 0; failures.Clear(); catalog.ClearArchiveEntries(); }
            cancellation?.Dispose(); cancellation = new(); resume.Set();
            progress = progress with { State = ScanState.Running, Message = "正在枚举本机归档条目" };
            task = Task.Run(() => Scan(cancellation.Token));
        }
    }
    public void Pause() { resume.Reset(); lock (sync) if (progress.State == ScanState.Running) progress = progress with { State = ScanState.Paused }; }
    public void Cancel() { cancellation?.Cancel(); resume.Set(); }
    private void SetProgress(ScanProgress value) { lock (sync) progress = value; }
    private void Scan(CancellationToken token)
    {
        try
        {
            var entries = source.Enumerate();
            source.ValidateVersion();
            if (cursor < 0 || cursor > entries.Count) throw new InvalidDataException("索引游标超出当前资源清单");
            for (; cursor < entries.Count; cursor++)
            {
                token.ThrowIfCancellationRequested(); resume.Wait(token);
                var entry = entries[cursor];
                try
                {
                    if (entry.Diagnostic is not null) throw new InvalidDataException(entry.Diagnostic);
                    if (source.IsAvfx(entry))
                    {
                        var bytes = source.Read(entry.Location);
                        _ = Blocks.Root(bytes);
                        var hash = ResourceCatalog.Hash(bytes);
                        catalog.Upsert(new()
                        {
                            Id = hash,
                            References = [new() { Kind = ResourceKind.Archive, Archive = entry.Location, PackageVersion = source.Fingerprint, ContentHash = hash }]
                        });
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
                {
                    failed++;
                    lock (sync) if (failures.Count < 200) failures.Add(entry.Location.Key + "：" + ex.Message);
                }
                if (cursor % 128 == 0)
                    SetProgress(new(resume.IsSet ? ScanState.Running : ScanState.Paused, cursor + 1, entries.Count, catalog.Count, failed, "后台扫描中"));
                if ((cursor + 1) % 65536 == 0) Save(cursor + 1);
            }
            source.ValidateVersion();
            SetProgress(new(ScanState.Completed, cursor, entries.Count, catalog.Count, failed, failed == 0 ? "归档扫描完成" : "扫描结束，但有失败条目；覆盖不完整"));
        }
        catch (OperationCanceledException) { SetProgress(Progress with { State = ScanState.Cancelled, Scanned = cursor, Message = "已取消；下次可从检查点继续" }); }
        catch (Exception ex) { SetProgress(Progress with { State = ScanState.Faulted, Scanned = cursor, Message = ex.Message }); }
        finally
        {
            try { Save(cursor); }
            catch (Exception ex) { SetProgress(Progress with { State = ScanState.Faulted, Message = "无法保存索引：" + ex.Message }); }
        }
    }
    private void Save(int next)
    {
        lock (saveLock)
        {
        var cache = new IndexCache { Fingerprint = source.Fingerprint, Cursor = next, Failed = failed, Failures = Failures, Records = catalog.Snapshot() };
        AtomicFile.Write(cachePath, JsonSerializer.SerializeToUtf8Bytes(cache, IndexCache.Json));
        }
    }
    public void SaveCheckpoint() => Save(cursor);
    public void Dispose()
    {
        lock (sync) { if (disposed) return; disposed = true; }
        Cancel();
        // Do not block the framework/UI thread on scanning or checkpoint serialization.
        var running = Completion;
        if (running is null || running.IsCompleted) { cancellation?.Dispose(); resume.Dispose(); }
        else _ = running.ContinueWith(_ => { cancellation?.Dispose(); resume.Dispose(); }, TaskScheduler.Default);
    }
}
