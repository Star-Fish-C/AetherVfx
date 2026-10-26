using AetherVfx.Core;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using System.Text.RegularExpressions;

namespace AetherVfx;

internal sealed record CatalogEntry(string Name, string Category, string Path);

internal sealed class GameCatalog(IDataManager data)
{
    public List<EquipmentTarget> Equipment { get; } = [];
    public List<CatalogEntry> Effects { get; } = [];
    public string Status { get; private set; } = "尚未建立索引";
    private readonly HashSet<string> seen = [];
    public void Build()
    {
        Equipment.Clear(); Effects.Clear(); seen.Clear();
        foreach (var item in data.GetExcelSheet<Item>())
        {
            if (item.ModelMain == 0 || !item.EquipSlotCategory.IsValid) continue;
            var c = item.EquipSlotCategory.Value;
            Slot? slot = c.MainHand == 1 ? Slot.MainHand : c.OffHand == 1 ? Slot.OffHand : c.Head == 1 ? Slot.Head : c.Body == 1 ? Slot.Body : c.Gloves == 1 ? Slot.Hands : c.Legs == 1 ? Slot.Legs : c.Feet == 1 ? Slot.Feet : c.Ears == 1 ? Slot.Ears : c.Neck == 1 ? Slot.Neck : c.Wrists == 1 ? Slot.Wrists : c.FingerR == 1 ? Slot.RFinger : c.FingerL == 1 ? Slot.LFinger : null;
            if (slot is null) continue;
            bool weapon = slot is Slot.MainHand or Slot.OffHand;
            bool accessory = slot is Slot.Ears or Slot.Neck or Slot.Wrists or Slot.RFinger or Slot.LFinger;
            ulong model = item.ModelMain;
            var target = new EquipmentTarget
            {
                ItemId = item.RowId, Name = item.Name.ExtractText(), Slot = slot.Value,
                Kind = weapon ? EquipmentKind.Weapon : accessory ? EquipmentKind.Accessory : EquipmentKind.Equipment,
                PrimaryId = (ushort)model, SecondaryId = weapon ? (ushort)(model >> 16) : (ushort)0,
                Variant = (byte)((model >> (weapon ? 32 : 16)) & 0xFF)
            };
            if (target.PrimaryId == 0 || string.IsNullOrEmpty(target.Name)) continue;
            Equipment.Add(target);
        }
        Status = $"已索引 {Effects.Count} 个特效、{Equipment.Count} 件装备；技能时间线可按需展开";
    }
    public IReadOnlyList<CatalogEntry> ExpandEquipment(EquipmentTarget target)
    {
        var result = new List<CatalogEntry>();
        var bytes = data.GetFile(target.ImcPath)?.Data ?? throw new InvalidDataException("找不到装备 IMC");
        if (bytes.Length < 4) return result;
        for (int i = 8; i < bytes.Length; i += 6)
        {
            byte vfx = bytes[i];
            if (vfx != 0)
            {
                var path = (target with { VfxId = vfx }).VfxPath;
                if (data.FileExists(path)) result.Add(new(target.Name, "装备", path));
            }
        }
        return result.DistinctBy(x => x.Path).ToArray();
    }
    public void ExpandAction(string search)
    {
        if (search.Trim().Length == 0) throw new InvalidDataException("先输入技能名称");
        foreach (var action in data.GetExcelSheet<Lumina.Excel.Sheets.Action>().Where(x => x.Name.ExtractText().Contains(search, StringComparison.OrdinalIgnoreCase)).Take(40))
        {
            foreach (var timeline in new[] { action.AnimationEnd.ValueNullable?.Key.ExtractText(), action.ActionTimelineHit.ValueNullable?.Key.ExtractText() })
            {
                if (string.IsNullOrEmpty(timeline)) continue;
                var bytes = data.GetFile($"chara/action/{timeline}.tmb")?.Data;
                if (bytes is null) continue;
                foreach (Match match in Regex.Matches(System.Text.Encoding.ASCII.GetString(bytes), @"[a-zA-Z0-9_/]+\.avfx"))
                    Add(action.Name.ExtractText(), "技能", match.Value);
            }
        }
    }
    private void AddCommon(string name, string category, string? key)
    { if (!string.IsNullOrWhiteSpace(key)) Add(name, category, $"vfx/common/eff/{key}.avfx"); }
    private void Add(string name, string category, string path)
    {
        if (seen.Add(name + "|" + path) && data.FileExists(path)) Effects.Add(new(name, category, path));
    }
    public List<string> SharedEquipment(EquipmentTarget target) => Equipment.Where(x => x.Kind == target.Kind && x.PrimaryId == target.PrimaryId && x.SecondaryId == target.SecondaryId).Select(x => $"{x.Name}（{x.Slot}，变体 {x.Variant}）").Distinct().ToList();
}
