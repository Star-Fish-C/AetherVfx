using AetherVfx.Core;
using Lumina;
using Lumina.Data;
using Lumina.Data.Structs;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace AetherVfx.GameData;

public sealed class SqpackArchiveSource : IArchiveSource, IDisposable
{
    public Lumina.GameData Game { get; }
    private readonly Dictionary<(string, byte, int, int), Category> categories = [];
    private readonly object readLock = new();
    private readonly Dictionary<string, (long Size, DateTime Modified)> manifest = new(StringComparer.OrdinalIgnoreCase);
    public string Fingerprint { get; }
    private IReadOnlyList<ArchiveScanEntry>? entries;
    public SqpackArchiveSource(string sqpack)
    {
        Game = new(sqpack, new LuminaOptions { CacheFileResources = false, LoadMultithreaded = true, DefaultExcelLanguage = Language.ChineseSimplified });
        var manifest = new StringBuilder();
        foreach (var (name, repo) in Game.Repositories.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            manifest.AppendLine(name + "|" + repo.Version);
            foreach (var file in repo.RootDir.EnumerateFiles("*", SearchOption.TopDirectoryOnly).Where(f => f.Extension is ".index" or ".index2" || f.Name.Contains(".dat", StringComparison.Ordinal)).OrderBy(f => f.Name, StringComparer.Ordinal))
            {
                manifest.AppendLine($"{file.Name}|{file.Length}|{file.LastWriteTimeUtc.Ticks}");
                this.manifest[file.FullName] = (file.Length, file.LastWriteTimeUtc);
            }
            foreach (var category in repo.Categories.Values.SelectMany(x => x))
                categories[(name, category.CategoryId, category.Expansion, category.Chunk)] = category;
        }
        Fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifest.ToString()))).ToLowerInvariant();
    }
    public IReadOnlyList<ArchiveScanEntry> Enumerate()
    {
        if (entries is not null) return entries;
        var result = new Dictionary<ArchiveLocation, ArchiveScanEntry>();
        var synonyms = new List<ArchiveScanEntry>();
        foreach (var (key, category) in categories)
        {
            foreach (var entry in category.IndexHashTableEntries?.Values.AsEnumerable() ?? [])
            {
                var location = new ArchiveLocation(key.Item1, key.Item2, key.Item3, key.Item4, entry.DataFileId, entry.Offset);
                if (!entry.IsSynonym) result.TryAdd(location, new(location));
                else synonyms.Add(new(location, $"尚未解析的同哈希冲突条目 {entry.hash:X16}"));
            }
            foreach (var entry in category.Index2HashTableEntries?.Values.AsEnumerable() ?? [])
            {
                var location = new ArchiveLocation(key.Item1, key.Item2, key.Item3, key.Item4, entry.DataFileId, entry.Offset);
                if (!entry.IsSynonym) result.TryAdd(location, new(location));
            }
        }
        entries = result.Values.Concat(synonyms).OrderBy(x => x.Location.Repository, StringComparer.Ordinal)
            .ThenBy(x => x.Location.Category).ThenBy(x => x.Location.Expansion).ThenBy(x => x.Location.Chunk)
            .ThenBy(x => x.Location.Dat).ThenBy(x => x.Location.Offset).ThenBy(x => x.Diagnostic, StringComparer.Ordinal).ToArray();
        return entries;
    }
    private Category CategoryFor(ArchiveLocation location) => categories.TryGetValue((location.Repository, location.Category, location.Expansion, location.Chunk), out var category)
        ? category : throw new FileNotFoundException("归档分片已不存在，请重新扫描");
    public bool IsAvfx(ArchiveScanEntry entry)
    {
        var loc = entry.Location;
        var dat = CategoryFor(loc).GetDat(loc.Dat) ?? throw new FileNotFoundException("缺少 DAT 文件");
        // Read a prefix only. Large standard files such as SCD must not be fully decompressed.
        using var stream = new FileStream(dat.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.RandomAccess);
        using var reader = new BinaryReader(stream);
        stream.Position = loc.Offset;
        uint headerSize = reader.ReadUInt32(); var type = (FileType)reader.ReadUInt32(); uint rawSize = reader.ReadUInt32();
        if (type != FileType.Standard || rawSize < 8) return false;
        if (headerSize < 24 || headerSize > 1048576 || loc.Offset + headerSize + 16 > stream.Length) throw new InvalidDataException("标准资源块头无效");
        stream.Position = loc.Offset + 20; uint blocks = reader.ReadUInt32();
        if (blocks == 0) return false;
        stream.Position = loc.Offset + 24; uint relative = reader.ReadUInt32();
        stream.Position = checked(loc.Offset + headerSize + relative);
        uint blockHeader = reader.ReadUInt32(); reader.ReadUInt32(); uint compressed = reader.ReadUInt32(); uint uncompressed = reader.ReadUInt32();
        if (blockHeader < 16 || blockHeader > 1024 || uncompressed < 4) throw new InvalidDataException("资源首块无效");
        stream.Position = checked(loc.Offset + headerSize + relative + blockHeader);
        Span<byte> magic = stackalloc byte[4];
        if (compressed == 32000) stream.ReadExactly(magic);
        else { using var deflate = new DeflateStream(stream, CompressionMode.Decompress, true); deflate.ReadExactly(magic); }
        return magic.SequenceEqual("XFVA"u8);
    }
    public byte[] Read(ArchiveLocation location)
    {
        lock (readLock)
        {
            var category = CategoryFor(location); var dat = category.GetDat(location.Dat) ?? throw new FileNotFoundException("缺少 DAT 分片");
            ValidateFile(dat.FullName);
            return category.GetFile<FileResource>(location.Dat, location.Offset)?.Data ?? throw new FileNotFoundException("无法读取归档资源");
        }
    }
    public void ValidateVersion() { foreach (var path in manifest.Keys) ValidateFile(path); }
    private void ValidateFile(string path)
    {
        var file = new FileInfo(path);
        if (!manifest.TryGetValue(path, out var expected) || !file.Exists || file.Length != expected.Size || file.LastWriteTimeUtc != expected.Modified)
            throw new InvalidDataException("游戏资源包在索引后发生变化，请重新启动资源索引后再读取");
    }
    public byte[] Read(ResourceReference reference)
    {
        reference.Validate();
        if (reference.Kind == ResourceKind.GamePath) return ReadPath(reference.Path);
        if (reference.PackageVersion != Fingerprint) throw new InvalidDataException("游戏资源版本已改变，不能使用旧偏移；请重新扫描并选择资源");
        var bytes = Read(reference.Archive!);
        if (ResourceCatalog.Hash(bytes) != reference.ContentHash) throw new InvalidDataException("归档资源内容不匹配，请重新扫描");
        return bytes;
    }
    public byte[] ReadPath(string path)
    {
        EffectCompiler.CheckGamePath(path);
        lock (readLock) return Game.GetFile(path)?.Data ?? throw new FileNotFoundException("缺少资源：" + path);
    }
    public bool FileExists(string path) { lock (readLock) return Game.FileExists(path); }
    public void Dispose() => Game.Dispose();
}
