using AetherVfx.Core;
using Lumina.Excel.Sheets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AetherVfx.GameData;

public sealed class AssociationIndexer(SqpackArchiveSource source, ResourceCatalog catalog, string snapshotDirectory)
{
    private readonly Dictionary<string, string> hashes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string[]> graph = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, List<SourceAssociation>> dungeons = [];
    private readonly Dictionary<uint, List<SourceAssociation>> territorySources = [], monsterLocations = [];
    private readonly object sync = new();
    public string Status { get { lock (sync) return status; } }
    private string status = "来源索引尚未建立";
    public int MissingReferences { get; private set; }
    public int FailedSources { get; private set; }
    public async Task BuildAsync(CancellationToken token) => await Task.Run(() => Build(token), token);
    private void SetStatus(string value) { lock (sync) status = value; }
    private void Build(CancellationToken token)
    {
        try
        {
            hashes.Clear(); graph.Clear(); dungeons.Clear(); territorySources.Clear(); monsterLocations.Clear(); MissingReferences = FailedSources = 0;
            SetStatus("正在关联技能／状态");
            foreach (var action in source.Game.Excel.GetSheet<Lumina.Excel.Sheets.Action>())
            {
                token.ThrowIfCancellationRequested(); var name = action.Name.ExtractText(); if (name.Length == 0) continue;
                var association = new SourceAssociation(AssociationKind.Skill, action.RowId.ToString(), name, AssociationEvidence.DirectReference, "Action/" + action.RowId);
                AddCommon(action.VFX.ValueNullable?.VFX.ValueNullable?.Location.ExtractText(), association);
                AddCommon(action.AnimationStart.ValueNullable?.VFX.ValueNullable?.Location.ExtractText(), association);
                foreach (var key in new[] { action.AnimationEnd.ValueNullable?.Key.ExtractText(), action.ActionTimelineHit.ValueNullable?.Key.ExtractText() })
                    if (!string.IsNullOrEmpty(key)) Walk($"chara/action/{key}.tmb", [association], token);
            }
            foreach (var item in source.Game.Excel.GetSheet<Status>())
            {
                token.ThrowIfCancellationRequested(); var name = item.Name.ExtractText(); if (name.Length == 0) continue;
                var association = new SourceAssociation(AssociationKind.Status, item.RowId.ToString(), name, AssociationEvidence.DirectReference, "Status/" + item.RowId);
                AddCommon(item.HitEffect.ValueNullable?.Location.ValueNullable?.Location.ExtractText(), association);
                var loops = item.VFX.ValueNullable?.VFX;
                if (loops is not null) foreach (var vfx in loops) AddCommon(vfx.ValueNullable?.Location.ExtractText(), association);
            }
            SetStatus("正在关联地区／副本");
            foreach (var content in source.Game.Excel.GetSheet<ContentFinderCondition>())
            {
                var name = content.Name.ExtractText(); if (name.Length == 0 || content.TerritoryType.RowId == 0) continue;
                if (!dungeons.TryGetValue(content.TerritoryType.RowId, out var associations)) dungeons[content.TerritoryType.RowId] = associations = [];
                associations.Add(new(AssociationKind.Dungeon, content.RowId.ToString(), name, AssociationEvidence.IndirectReference, "ContentFinderCondition/" + content.RowId));
            }
            foreach (var territory in source.Game.Excel.GetSheet<TerritoryType>())
            {
                token.ThrowIfCancellationRequested();
                var bg = territory.Bg.ExtractText(); if (string.IsNullOrWhiteSpace(bg) || !bg.Contains('/')) continue;
                string name = territory.PlaceName.ValueNullable?.Name.ExtractText() ?? "";
                if (name.Length == 0) name = "地区 " + territory.RowId;
                var associations = new List<SourceAssociation> { new(AssociationKind.Region, territory.RowId.ToString(), name, AssociationEvidence.DirectReference, "TerritoryType/" + territory.RowId) };
                if (dungeons.TryGetValue(territory.RowId, out var instance)) associations.AddRange(instance);
                territorySources[territory.RowId] = associations;
                string directory = "bg/" + bg[..(bg.LastIndexOf('/') + 1)];
                foreach (var file in new[] { "vfx.lgb", "bg.lgb", "planmap.lgb" }) Walk(directory + file, associations, token);
            }
            foreach (var level in source.Game.Excel.GetSheet<Level>())
            {
                token.ThrowIfCancellationRequested();
                if (!level.Object.Is<BNpcBase>() || !territorySources.TryGetValue(level.Territory.RowId, out var contexts)) continue;
                if (!monsterLocations.TryGetValue(level.Object.RowId, out var targets)) monsterLocations[level.Object.RowId] = targets = [];
                targets.AddRange(contexts.Select(a => a with { Evidence = AssociationEvidence.IndirectReference, EvidencePath = $"Level/{level.RowId} → BNpcBase/{level.Object.RowId} → " + a.EvidencePath }));
            }
            SetStatus("正在关联怪物快照"); BuildMonsters(token);
            SetStatus("正在关联装备"); BuildEquipment(token);
            SetStatus($"来源索引完成；缺失引用 {MissingReferences}，失败来源 {FailedSources}（未知归属保留在未分类）");
        }
        catch (OperationCanceledException) { SetStatus("来源索引已取消，已完成关联保留"); }
        catch (Exception ex) { SetStatus("来源索引失败：" + ex.Message); throw; }
    }
    private void BuildMonsters(CancellationToken token)
    {
        var mappingsPath = Path.Combine(snapshotDirectory, "bnpc.json");
        var filesPath = Path.Combine(snapshotDirectory, "npc_files.json");
        if (!File.Exists(mappingsPath) || !File.Exists(filesPath)) throw new FileNotFoundException("缺少安装包内的怪物基础快照");
        var baseToName = JsonSerializer.Deserialize<Dictionary<string, uint>>(File.ReadAllText(mappingsPath))!;
        using var fileMap = JsonDocument.Parse(File.ReadAllText(filesPath));
        var names = source.Game.Excel.GetSheet<BNpcName>(); var bases = source.Game.Excel.GetSheet<BNpcBase>();
        var seen = new HashSet<(uint Name, string Model)>();
        string snapshotVersion = "unknown";
        string manifest = Path.Combine(snapshotDirectory, "snapshot-manifest.json");
        if (File.Exists(manifest)) { using var document = JsonDocument.Parse(File.ReadAllText(manifest)); snapshotVersion = document.RootElement.GetProperty("Commit").GetString() ?? "unknown"; }
        foreach (var (baseKey, nameId) in baseToName)
        {
            token.ThrowIfCancellationRequested();
            if (!uint.TryParse(baseKey, out var baseId) || !bases.TryGetRow(baseId, out var bnpc) || !names.TryGetRow(nameId, out var nameRow) || !bnpc.ModelChara.IsValid) continue;
            var model = bnpc.ModelChara.Value;
            if (model.Type is not 2 and not 3) continue;
            var name = nameRow.Singular.ExtractText(); if (name.Length == 0) continue;
            string modelId = (model.Type == 2 ? "d" : "m") + model.Model.ToString("D4");
            if (!seen.Add((nameId, modelId)) && !monsterLocations.ContainsKey(baseId)) continue;
            if (!fileMap.RootElement.TryGetProperty(modelId, out var files)) continue;
            var association = new SourceAssociation(AssociationKind.Monster, nameId.ToString(), name, AssociationEvidence.IndirectReference, $"snapshot/{snapshotVersion}/bnpc.json:{baseKey};BNpcName/{nameId};ModelChara/{bnpc.ModelChara.RowId};{modelId}");
            var associations = new List<SourceAssociation> { association };
            if (monsterLocations.TryGetValue(baseId, out var placements)) associations.AddRange(placements.Distinct());
            if (files.TryGetProperty("vfx", out var vfxs)) foreach (var value in vfxs.EnumerateArray()) Add(value.GetString()!, associations);
            if (files.TryGetProperty("tmb", out var timelines)) foreach (var value in timelines.EnumerateArray()) Walk(value.GetString()!, associations, token);
        }
    }
    private void BuildEquipment(CancellationToken token)
    {
        var imcs = new Dictionary<string, byte[]?>();
        foreach (var item in source.Game.Excel.GetSheet<Item>())
        {
            token.ThrowIfCancellationRequested();
            if (item.ModelMain == 0 || !item.EquipSlotCategory.IsValid) continue;
            var c = item.EquipSlotCategory.Value; string name = item.Name.ExtractText(); if (name.Length == 0) continue;
            bool weapon = c.MainHand == 1 || c.OffHand == 1;
            bool accessory = c.Ears == 1 || c.Neck == 1 || c.Wrists == 1 || c.FingerR == 1 || c.FingerL == 1;
            string prefix = weapon ? "weapon" : accessory ? "accessory" : "equipment";
            var model = item.ModelMain; int id = (ushort)model, body = (ushort)(model >> 16);
            string root = weapon ? $"chara/weapon/w{id:D4}/obj/body/b{body:D4}" : $"chara/{prefix}/{(accessory ? 'a' : 'e')}{id:D4}";
            string imc = weapon ? $"{root}/b{body:D4}.imc" : $"{root}/{(accessory ? 'a' : 'e')}{id:D4}.imc";
            if (!imcs.TryGetValue(imc, out var bytes))
            {
                try { imcs[imc] = bytes = source.ReadPath(imc); }
                catch (IOException) { imcs[imc] = null; }
            }
            if (bytes is null) continue;
            var association = new SourceAssociation(AssociationKind.Equipment, item.RowId.ToString(), name, AssociationEvidence.IndirectReference, imc);
            // Record model-level IMC references conservatively; not a claim that every item variant uses every VFX.
            for (int p = 8; p < bytes.Length; p += 6) if (bytes[p] != 0)
                Add($"{root}/vfx/eff/v{(weapon ? 'w' : accessory ? 'a' : 'e')}{bytes[p]:D4}.avfx", [association]);
        }
    }
    private void AddCommon(string? key, SourceAssociation association)
    { if (!string.IsNullOrEmpty(key)) Add($"vfx/common/eff/{key}.avfx", [association]); }
    private void Add(string path, IEnumerable<SourceAssociation> associations)
    {
        try
        {
            if (!hashes.TryGetValue(path, out var hash))
            {
                if (!source.FileExists(path)) { MissingReferences++; return; }
                byte[] data = source.ReadPath(path); _ = Blocks.Root(data); hashes[path] = hash = ResourceCatalog.Hash(data);
            }
            var refs = associations.Distinct().ToArray();
            catalog.Upsert(new() { Id = hash, Name = refs.FirstOrDefault()?.Name ?? "", References = [ResourceReference.GamePath(path)], Associations = refs });
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException) { FailedSources++; }
    }
    private void Walk(string start, IEnumerable<SourceAssociation> associations, CancellationToken token)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var queue = new Queue<string>(); queue.Enqueue(start);
        int count = 0;
        while (queue.TryDequeue(out var path))
        {
            token.ThrowIfCancellationRequested(); if (!visited.Add(path)) continue;
            if (++count > 10000) { FailedSources++; break; }
            if (path.EndsWith(".avfx", StringComparison.OrdinalIgnoreCase)) { Add(path, associations.Select(a => a with { EvidencePath = a.EvidencePath + " → " + start + " → " + path })); continue; }
            if (!graph.TryGetValue(path, out var links))
            {
                try
                {
                    if (!source.FileExists(path)) { graph[path] = []; continue; }
                    string text = Encoding.ASCII.GetString(source.ReadPath(path));
                    links = Regex.Matches(text, @"(?:vfx|chara|bg)/[a-zA-Z0-9_./\-]+\.(?:avfx|sgb|lgb|tmb)")
                        .Select(m => m.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    graph[path] = links;
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException) { FailedSources++; graph[path] = []; continue; }
            }
            foreach (var next in links) queue.Enqueue(next);
        }
    }
}
