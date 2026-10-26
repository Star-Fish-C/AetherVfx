using System.Text.Json;
using AetherVfx.Core;

namespace AetherVfx.GameData;

public static class MonsterSnapshots
{
    public static async Task<string> UpdateAsync(string configDirectory, CancellationToken token)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AetherVfx/0.2");
        const string repo = "https://api.github.com/repos/0ceal0t/Dalamud-VFXEditor/commits/main";
        using var commit = JsonDocument.Parse(await client.GetStringAsync(repo, token));
        string sha = commit.RootElement.GetProperty("sha").GetString() ?? throw new InvalidDataException("映射更新缺少提交版本");
        if (sha.Length != 40 || !sha.All(Uri.IsHexDigit)) throw new InvalidDataException("映射提交标识无效");
        string root = $"https://raw.githubusercontent.com/0ceal0t/Dalamud-VFXEditor/{sha}/VFXEditor/Files/";
        var bnpc = await client.GetByteArrayAsync(root + "bnpc.json", token);
        var files = await client.GetByteArrayAsync(root + "npc_files.json", token);
        var mapping = JsonSerializer.Deserialize<Dictionary<string, uint>>(bnpc) ?? throw new InvalidDataException("名称映射为空");
        using var parsed = JsonDocument.Parse(files);
        if (mapping.Count == 0 || parsed.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("映射数据格式错误");
        string directory = Path.Combine(configDirectory, "snapshots", sha);
        Directory.CreateDirectory(directory);
        AtomicFile.Write(Path.Combine(directory, "bnpc.json"), bnpc);
        AtomicFile.Write(Path.Combine(directory, "npc_files.json"), files);
        AtomicFile.Write(Path.Combine(directory, "snapshot-manifest.json"), JsonSerializer.SerializeToUtf8Bytes(new { Source = repo, Commit = sha, GameVersion = "not-declared-by-upstream" }));
        // A manifest pointer is committed only after both complete, validated files have been written.
        AtomicFile.Write(Path.Combine(configDirectory, "snapshot-selection.json"), JsonSerializer.SerializeToUtf8Bytes(sha));
        return directory;
    }
    public static string Select(string configDirectory, string bundled)
    {
        string pointer = Path.Combine(configDirectory, "snapshot-selection.json");
        if (!File.Exists(pointer)) return bundled;
        string? sha = JsonSerializer.Deserialize<string>(File.ReadAllText(pointer));
        if (sha is null || sha.Length != 40 || !sha.All(Uri.IsHexDigit)) throw new InvalidDataException("保存的怪物映射版本无效");
        string folder = Path.Combine(configDirectory, "snapshots", sha);
        return File.Exists(Path.Combine(folder, "bnpc.json")) && File.Exists(Path.Combine(folder, "npc_files.json")) ? folder : bundled;
    }
}
