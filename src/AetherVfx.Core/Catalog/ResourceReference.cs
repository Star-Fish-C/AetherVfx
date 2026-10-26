namespace AetherVfx.Core;

public enum ResourceKind { GamePath, Archive }

public sealed record ArchiveLocation(string Repository, byte Category, int Expansion, int Chunk, byte Dat, long Offset)
{
    [System.Text.Json.Serialization.JsonIgnore] public string Key => $"{Repository}/{Category:X2}/{Expansion}/{Chunk}/{Dat}/{Offset:X}";
}

public sealed record ResourceReference
{
    public ResourceKind Kind { get; init; }
    public string Path { get; init; } = "";
    public ArchiveLocation? Archive { get; init; }
    public string PackageVersion { get; init; } = "";
    public string ContentHash { get; init; } = "";
    [System.Text.Json.Serialization.JsonIgnore] public string Key => Kind == ResourceKind.GamePath ? Path.ToLowerInvariant() : $"sqpack:{PackageVersion}/{Archive?.Key}";
    public static ResourceReference GamePath(string path) => new() { Path = path };
    public void Validate()
    {
        if (Kind == ResourceKind.GamePath) { EffectCompiler.CheckGamePath(Path); return; }
        if (Kind != ResourceKind.Archive || Archive is null || string.IsNullOrWhiteSpace(Archive.Repository) || Archive.Repository.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_') || Archive.Offset < 0 || PackageVersion.Length != 64 || ContentHash.Length != 64 || !PackageVersion.All(Uri.IsHexDigit) || !ContentHash.All(Uri.IsHexDigit))
            throw new InvalidDataException("归档资源标识无效");
    }
}
