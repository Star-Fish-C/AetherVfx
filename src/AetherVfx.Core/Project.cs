using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AetherVfx.Core;

public enum Anchor { Original, Origin, Feet, Head, LeftHand, RightHand, Weapon, Body }
public enum Playback { Original, Loop }
public enum EquipmentKind { Equipment, Weapon, Accessory }
public enum Slot { MainHand, OffHand, Head, Body, Hands, Legs, Feet, Ears, Neck, Wrists, RFinger, LFinger }

public sealed record Layer
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = "新特效";
    public string Source { get; set; } = "";
    public ResourceReference? Resource { get; set; }
    public bool Visible { get; set; } = true;
    public Anchor Anchor { get; set; }
    public Vector3 Position { get; set; }
    public Vector3 RotationDegrees { get; set; }
    public float Scale { get; set; } = 1;
    public Vector4 Color { get; set; } = Vector4.One;
    public Playback Playback { get; set; }
    public int LoopFrames { get; set; } = 120;
    public int GapFrames { get; set; }
}

public sealed record EquipmentTarget
{
    public uint ItemId { get; set; }
    public string Name { get; set; } = "";
    public EquipmentKind Kind { get; set; }
    public Slot Slot { get; set; } = Slot.Body;
    public ushort PrimaryId { get; set; }
    public ushort SecondaryId { get; set; }
    public byte Variant { get; set; }
    public byte VfxId { get; set; } = 1;
    public bool KeepOriginal { get; set; }
    public string VfxPath => Kind == EquipmentKind.Weapon
        ? $"chara/weapon/w{PrimaryId:D4}/obj/body/b{SecondaryId:D4}/vfx/eff/vw{VfxId:D4}.avfx"
        : Kind == EquipmentKind.Accessory ? $"chara/accessory/a{PrimaryId:D4}/vfx/eff/va{VfxId:D4}.avfx"
        : $"chara/equipment/e{PrimaryId:D4}/vfx/eff/ve{VfxId:D4}.avfx";
    public string ImcPath => Kind == EquipmentKind.Weapon
        ? $"chara/weapon/w{PrimaryId:D4}/obj/body/b{SecondaryId:D4}/b{SecondaryId:D4}.imc"
        : Kind == EquipmentKind.Accessory ? $"chara/accessory/a{PrimaryId:D4}/a{PrimaryId:D4}.imc"
        : $"chara/equipment/e{PrimaryId:D4}/e{PrimaryId:D4}.imc";
}

public sealed record WorkProject
{
    public int Version { get; set; } = 2;
    public string Name { get; set; } = "我的特效组合";
    public string Author { get; set; } = "";
    public List<Layer> Layers { get; set; } = [];
    public EquipmentTarget Target { get; set; } = new();
}

public static class ProjectStore
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, IncludeFields = true,
        Converters = { new JsonStringEnumConverter() }
    };
    public static string Serialize(WorkProject project) => JsonSerializer.Serialize(project, Json);
    public static WorkProject Parse(string json)
    {
        var project = JsonSerializer.Deserialize<WorkProject>(json, Json) ?? throw new InvalidDataException("工程为空");
        if (project.Version is not 1 and not 2) throw new InvalidDataException($"不支持工程版本 {project.Version}");
        if (project.Layers is null || project.Target is null) throw new InvalidDataException("工程缺少图层或目标装备");
        if (project.Layers.Select(x => x.Id).Distinct().Count() != project.Layers.Count)
            throw new InvalidDataException("工程包含重复图层 ID");
        foreach (var layer in project.Layers) layer.Resource ??= ResourceReference.GamePath(layer.Source);
        project.Version = 2;
        return project;
    }
    public static void Save(string path, WorkProject project) => AtomicFile.Write(path, System.Text.Encoding.UTF8.GetBytes(Serialize(project)));
    public static WorkProject Load(string path) => Parse(File.ReadAllText(path, System.Text.Encoding.UTF8));
}

public static class AtomicFile
{
    public static void Write(string path, byte[] data)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temp, data); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

public sealed class ProjectHistory
{
    private readonly List<string> undo = [], redo = [];
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;
    public void Record(WorkProject project)
    {
        var state = ProjectStore.Serialize(project);
        if (undo.LastOrDefault() == state) return;
        undo.Add(state); if (undo.Count > 100) undo.RemoveAt(0); redo.Clear();
    }
    public WorkProject Undo(WorkProject project) => Move(undo, redo, project);
    public WorkProject Redo(WorkProject project) => Move(redo, undo, project);
    private static WorkProject Move(List<string> from, List<string> to, WorkProject current)
    {
        if (from.Count == 0) return current;
        var result = from[^1]; from.RemoveAt(from.Count - 1); to.Add(ProjectStore.Serialize(current));
        return ProjectStore.Parse(result);
    }
}

public interface IResources
{
    byte[] Read(string gamePath);
    byte[] Read(ResourceReference reference) => reference.Kind == ResourceKind.GamePath
        ? Read(reference.Path) : throw new InvalidDataException("当前资源提供器不能读取归档条目");
}
public sealed record Diagnostic(Guid? LayerId, string LayerName, string Message, bool Error = true);
public sealed record CompiledEffect(byte[] Avfx, IReadOnlyDictionary<string, byte[]> Dependencies);
public sealed record CompileResult(CompiledEffect? Effect, IReadOnlyList<Diagnostic> Diagnostics)
{
    public bool Success => Effect is not null && !Diagnostics.Any(d => d.Error);
}
