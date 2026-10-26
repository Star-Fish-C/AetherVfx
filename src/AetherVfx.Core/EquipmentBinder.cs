using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AetherVfx.Core;

public sealed record BoundEffect(CompiledEffect Effect, EquipmentTarget Target, object[] Manipulations, byte OriginalVfxId);

public sealed class EquipmentBinder(IResources resources)
{
    public BoundEffect Bind(CompiledEffect effect, EquipmentTarget target)
    {
        if (target.PrimaryId == 0 || target.VfxId == 0) throw new InvalidDataException("请选择有效装备；特效 ID 不能为 0");
        bool slotValid = target.Kind switch
        {
            EquipmentKind.Weapon => target.Slot is Slot.MainHand or Slot.OffHand,
            EquipmentKind.Accessory => target.Slot is Slot.Ears or Slot.Neck or Slot.Wrists or Slot.RFinger or Slot.LFinger,
            EquipmentKind.Equipment => target.Slot is Slot.Head or Slot.Body or Slot.Hands or Slot.Legs or Slot.Feet,
            _ => false
        };
        if (!slotValid) throw new InvalidDataException("装备类型和部位不匹配");
        var imc = resources.Read(target.ImcPath);
        if (imc.Length < 4) throw new InvalidDataException("装备 IMC 文件被截断");
        int variants = BinaryPrimitives.ReadUInt16LittleEndian(imc);
        int mask = BinaryPrimitives.ReadUInt16LittleEndian(imc.AsSpan(2));
        int slot = target.Slot switch
        {
            Slot.Head or Slot.Ears or Slot.MainHand or Slot.OffHand => 0,
            Slot.Body or Slot.Neck => 1, Slot.Hands or Slot.Wrists => 2,
            Slot.Legs or Slot.RFinger or Slot.LFinger => 3, Slot.Feet => 4,
            _ => throw new InvalidDataException("不支持的装备部位")
        };
        if (target.Kind == EquipmentKind.Weapon) slot = 0;
        if ((mask & (1 << slot)) == 0 || target.Variant > variants) throw new InvalidDataException("目标装备 IMC 不包含此部位／变体");
        int parts = BitOperations(mask);
        int ordinal = BitOperations(mask & ((1 << slot) - 1));
        int offset = checked(4 + (target.Variant * parts + ordinal) * 6);
        if (offset + 6 > imc.Length) throw new InvalidDataException("装备 IMC 条目被截断");
        var entry = imc.AsSpan(offset, 6);
        byte original = entry[4];
        if (target.KeepOriginal && original != 0)
        {
            var originalTarget = target with { VfxId = original };
            var preserved = new EffectCompiler(resources).Compile(new() { Layers = [new() { Name = "原装备特效", Source = originalTarget.VfxPath }] });
            if (!preserved.Success) throw new InvalidDataException("无法保留原特效：" + string.Join("；", preserved.Diagnostics.Select(x => x.Message)));
            var merged = EffectCompiler.Merge([Blocks.Root(preserved.Effect!.Avfx), Blocks.Root(effect.Avfx)]);
            var dependencies = preserved.Effect.Dependencies.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
            foreach (var (path, data) in effect.Dependencies) dependencies[path] = data;
            effect = new(Blocks.File(merged), dependencies);
        }
        ushort attributes = BinaryPrimitives.ReadUInt16LittleEndian(entry[2..]);
        object[] manipulations = [new
        {
            Type = "Imc",
            Manipulation = new
            {
                Entry = new { MaterialId = entry[0], DecalId = entry[1], AttributeMask = attributes & 0x3FF, SoundId = attributes >> 10, VfxId = target.VfxId, MaterialAnimationId = entry[5] },
                target.PrimaryId, target.SecondaryId, target.Variant,
                ObjectType = target.Kind.ToString(), EquipSlot = target.Slot.ToString(), BodySlot = "Body"
            }
        }];
        return new(effect, target with { }, manipulations, original);
    }
    private static int BitOperations(int value) => System.Numerics.BitOperations.PopCount((uint)value);
}

public static class PmpExporter
{
    public static string CompressedManipulations(object[] manipulations)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, true))
        {
            gzip.WriteByte(0);
            gzip.Write(JsonSerializer.SerializeToUtf8Bytes(manipulations));
        }
        return Convert.ToBase64String(output.ToArray());
    }
    public static Dictionary<string, byte[]> Files(BoundEffect bound)
    {
        var result = bound.Effect.Dependencies.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
        result[bound.Target.VfxPath] = bound.Effect.Avfx;
        return result;
    }
    public static void Export(string path, WorkProject project, BoundEffect bound)
    {
        EffectCompiler.Validate(Blocks.Root(bound.Effect.Avfx));
        var files = Files(bound);
        var mappings = new Dictionary<string, string>();
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            foreach (var (gamePath, data) in files.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                EffectCompiler.CheckGamePath(gamePath);
                var local = "files/" + Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant() + Path.GetExtension(gamePath);
                if (!mappings.Values.Contains(local)) Write(zip, local, data);
                mappings.Add(gamePath, local);
            }
            // Version 3 remains supported by the installed CN Penumbra; avoid newer v4-only containers.
            WriteJson(zip, "meta.json", new { FileVersion = 3, project.Name, project.Author, Description = "AetherVfx 游戏内特效工作台", Version = "1.0.0", Website = "", ModTags = new[] { "VFX" } });
            WriteJson(zip, "default_mod.json", new { Files = mappings, FileSwaps = new Dictionary<string, string>(), bound.Manipulations });
            WriteJson(zip, "aethervfx-project.json", project);
        }
        AtomicFile.Write(path, output.ToArray());
    }
    private static void WriteJson(ZipArchive zip, string name, object value) => Write(zip, name, JsonSerializer.SerializeToUtf8Bytes(value, ProjectStore.Json));
    private static void Write(ZipArchive zip, string name, byte[] bytes)
    {
        using var stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open(); stream.Write(bytes);
    }
}
