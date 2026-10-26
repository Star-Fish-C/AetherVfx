using System.Buffers.Binary;
using System.Text;

namespace AetherVfx.Core;

// AVFX block format documented by Dalamud-VFXEditor (MIT); unknown payloads are preserved.
public sealed record Block(string Tag, byte[] Data)
{
    public static Block Int(string tag, int value) => new(tag, BitConverter.GetBytes(value));
    public static Block Float(string tag, float value) => new(tag, BitConverter.GetBytes(value));
    public static Block Group(string tag, IEnumerable<Block> children) => new(tag, Blocks.Write(children));
    public int Integer => Data.Length == 1 ? Data[0] : Data.Length == 4 ? BinaryPrimitives.ReadInt32LittleEndian(Data) : throw new InvalidDataException($"{Tag} 不是整数");
    public List<Block> Children() => Blocks.Read(Data);
}

public static class Blocks
{
    public static List<Block> Read(ReadOnlySpan<byte> bytes)
    {
        var result = new List<Block>();
        for (int p = 0; p < bytes.Length;)
        {
            if (bytes.Length - p < 8) throw new InvalidDataException("AVFX 块头被截断");
            var tag = Encoding.ASCII.GetString(bytes.Slice(p, 4).ToArray().Reverse().ToArray()).Trim('\0');
            if (tag.Length == 0 || tag.Any(c => c < 32 || c > 126)) throw new InvalidDataException("无效 AVFX 块标签");
            var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(p + 4)..]);
            if (size > int.MaxValue || size > bytes.Length - p - 8) throw new InvalidDataException($"{tag} 块长度超出文件");
            result.Add(new(tag, bytes.Slice(p + 8, (int)size).ToArray()));
            p = checked(p + 8 + (((int)size + 3) & ~3));
            if (p > bytes.Length) throw new InvalidDataException("AVFX 对齐被截断");
        }
        return result;
    }
    public static byte[] Write(IEnumerable<Block> blocks)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        foreach (var b in blocks)
        {
            if (b.Tag.Length is < 1 or > 4) throw new InvalidDataException("标签长度错误");
            writer.Write(Encoding.ASCII.GetBytes(b.Tag.PadLeft(4, '\0')).Reverse().ToArray());
            writer.Write(b.Data.Length); writer.Write(b.Data);
            writer.Write(new byte[(-b.Data.Length) & 3]);
        }
        return stream.ToArray();
    }
    public static List<Block> Root(byte[] data)
    {
        var root = Read(data);
        if (root.Count != 1 || root[0].Tag != "AVFX") throw new InvalidDataException("不是 AVFX 文件");
        return root[0].Children();
    }
    public static byte[] File(IEnumerable<Block> root) => Write([Block.Group("AVFX", root)]);
    public static Block? Find(this IEnumerable<Block> blocks, string tag) => blocks.LastOrDefault(x => x.Tag == tag);
    public static int Number(this IEnumerable<Block> blocks, string tag, int fallback = -1) => blocks.Find(tag)?.Integer ?? fallback;
    public static void Set(this List<Block> blocks, Block value)
    {
        var index = blocks.FindIndex(x => x.Tag == value.Tag);
        if (index < 0) blocks.Add(value); else blocks[index] = value;
    }
    public static List<List<Block>> Entries(this IEnumerable<Block> blocks, string tag, string start)
    {
        var last = blocks.Find(tag); if (last is null || last.Data.Length == 0) return [];
        var result = new List<List<Block>>();
        foreach (var b in last.Children())
        {
            if (b.Tag == start) result.Add([]);
            if (result.Count == 0) throw new InvalidDataException($"{tag} 缺少条目起始字段");
            result[^1].Add(b);
        }
        return result;
    }
    public static void SetEntries(this List<Block> blocks, string tag, IEnumerable<List<Block>> entries)
    {
        blocks.RemoveAll(b => b.Tag == tag);
        var accumulated = new List<Block>();
        // Native/editor format uses cumulative prefixes; readers consume the last container.
        foreach (var entry in entries) { accumulated.AddRange(entry); blocks.Add(Block.Group(tag, accumulated)); }
    }
    public static bool TryRead(byte[] data, out List<Block> blocks)
    {
        try { blocks = Read(data); return true; }
        catch (InvalidDataException) { blocks = []; return false; }
    }
}
