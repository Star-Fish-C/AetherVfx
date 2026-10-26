using System.Security.Cryptography;

namespace AetherVfx.Core;

public enum AssociationKind { Region, Monster, Dungeon, Skill, Status, Equipment, User }
public enum AssociationEvidence { DirectReference, IndirectReference, UserTag }
public enum ResourceCapability { Unknown, Available, Unavailable, PendingGameValidation }
public sealed record SourceAssociation(AssociationKind Kind, string Id, string Name, AssociationEvidence Evidence, string EvidencePath);

public sealed record EffectRecord
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public ResourceReference[] References { get; init; } = [];
    public SourceAssociation[] Associations { get; init; } = [];
    public ResourceCapability Preview { get; init; } = ResourceCapability.PendingGameValidation;
    public ResourceCapability Editing { get; init; } = ResourceCapability.Unknown;
    public string Diagnostic { get; init; } = "";
    public string EditingDiagnostic { get; init; } = "";
    [System.Text.Json.Serialization.JsonIgnore] public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "未命名 · " + Id[..Math.Min(12, Id.Length)] : Name;
    [System.Text.Json.Serialization.JsonIgnore] public ResourceReference Preferred => References.FirstOrDefault(x => x.Kind == ResourceKind.GamePath) ?? References.First();
}

public sealed class ResourceCatalog
{
    private readonly object sync = new();
    private readonly Dictionary<string, EffectRecord> records = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<(AssociationKind, string, AssociationEvidence)>> associationKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> referenceKeys = new(StringComparer.OrdinalIgnoreCase);
    private long revision;
    public long Revision => Interlocked.Read(ref revision);
    public int Count { get { lock (sync) return records.Count; } }
    public EffectRecord[] Snapshot() { lock (sync) return records.Values.ToArray(); }
    public EffectRecord? Find(string id) { lock (sync) return records.GetValueOrDefault(id); }
    public void Clear() { lock (sync) { records.Clear(); associationKeys.Clear(); referenceKeys.Clear(); Interlocked.Increment(ref revision); } }
    public void ClearArchiveEntries()
    {
        lock (sync)
        {
            foreach (var (id, record) in records.ToArray())
            {
                var paths = record.References.Where(r => r.Kind == ResourceKind.GamePath).ToArray();
                if (paths.Length == 0) { records.Remove(id); associationKeys.Remove(id); referenceKeys.Remove(id); }
                else { records[id] = record with { References = paths }; referenceKeys[id] = paths.Select(x => x.Key).ToHashSet(); }
            }
            Interlocked.Increment(ref revision);
        }
    }
    public void Upsert(EffectRecord record)
    {
        if (record.Id.Length == 0 || record.References.Length == 0) throw new InvalidDataException("目录条目缺少资源标识");
        lock (sync)
        {
            if (records.TryGetValue(record.Id, out var old))
            {
                var newReferences = record.References.Where(r => referenceKeys[record.Id].Add(r.Key)).ToArray();
                var newAssociations = record.Associations.Where(a => associationKeys[record.Id].Add((a.Kind, a.Id, a.Evidence))).ToArray();
                string name = string.IsNullOrWhiteSpace(old.Name) ? record.Name : old.Name;
                if (newReferences.Length == 0 && newAssociations.Length == 0 && old.Name == name) return;
                record = old with
                {
                    Name = name,
                    References = old.References.Concat(newReferences).ToArray(),
                    Associations = old.Associations.Concat(newAssociations).ToArray()
                };
            }
            else
            {
                record = record with { References = record.References.DistinctBy(r => r.Key).ToArray(), Associations = record.Associations.DistinctBy(a => (a.Kind, a.Id, a.Evidence)).ToArray() };
                referenceKeys[record.Id] = record.References.Select(r => r.Key).ToHashSet();
                associationKeys[record.Id] = record.Associations.Select(a => (a.Kind, a.Id, a.Evidence)).ToHashSet();
            }
            records[record.Id] = record; Interlocked.Increment(ref revision);
        }
    }
    public void SetCapability(string id, ResourceCapability preview, string message)
    {
        lock (sync) if (records.TryGetValue(id, out var record))
        { records[id] = record with { Preview = preview, Diagnostic = message }; Interlocked.Increment(ref revision); }
    }
    public void SetEditingCapability(string id, bool supported, string message)
    {
        lock (sync) if (records.TryGetValue(id, out var record))
        { records[id] = record with { Editing = supported ? ResourceCapability.Available : ResourceCapability.Unavailable, EditingDiagnostic = message }; Interlocked.Increment(ref revision); }
    }
    public static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    public static bool Matches(EffectRecord record, string search, string? alias = null)
    {
        var terms = search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return terms.All(term => record.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase)
            || alias?.Contains(term, StringComparison.OrdinalIgnoreCase) == true
            || record.References.Any(r => r.Key.Contains(term, StringComparison.OrdinalIgnoreCase))
            || record.Associations.Any(a => a.Name.Contains(term, StringComparison.OrdinalIgnoreCase)));
    }
}

public sealed record ArchiveScanEntry(ArchiveLocation Location, string? Diagnostic = null);
public interface IArchiveSource
{
    string Fingerprint { get; }
    IReadOnlyList<ArchiveScanEntry> Enumerate();
    void ValidateVersion() { }
    bool IsAvfx(ArchiveScanEntry entry);
    byte[] Read(ArchiveLocation location);
}

public enum ScanState { Idle, Running, Paused, Cancelled, Completed, Faulted }
public sealed record ScanProgress(ScanState State, int Scanned, int Total, int Found, int Failed, string Message)
{
    public bool FullCoverage => State == ScanState.Completed && Scanned == Total && Failed == 0;
}
public sealed record IndexCache
{
    public static readonly System.Text.Json.JsonSerializerOptions Json = new(ProjectStore.Json) { WriteIndented = false };
    public int Version { get; init; } = 1;
    public string Fingerprint { get; init; } = "";
    public int Cursor { get; init; }
    public int Failed { get; init; }
    public string[] Failures { get; init; } = [];
    public EffectRecord[] Records { get; init; } = [];
}
