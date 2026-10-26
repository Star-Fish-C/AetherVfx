using System.Numerics;
using System.Text;

namespace AetherVfx.Core;

public sealed class EffectCompiler(IResources resources)
{
    public static readonly Dictionary<string, string> Groups = new()
    {
        ["Schd"] = "ScCn", ["TmLn"] = "TlCn", ["Emit"] = "EmCn", ["Ptcl"] = "PrCn",
        ["Efct"] = "EfCn", ["Bind"] = "BdCn", ["Tex"] = "TxCn", ["Modl"] = "MdCn"
    };
    // Only known containers are traversed. Raw vertex/curve data must never be interpreted as blocks.
    private static readonly HashSet<string> Containers = ["Data", "TC1", "TC2", "TC3", "TC4", "TN", "TR", "TD", "TP", "UvSt", "ItPr", "ItEm", "Item", "Trgr", "PrpS", "Prp1", "Prp2", "PrpG"];
    private static readonly Dictionary<string, string> References = new()
    {
        ["TlNo"] = "TmLn", ["BnNo"] = "Bind", ["BdNo"] = "Bind", ["EfNo"] = "Efct", ["EmNo"] = "Emit",
        ["MdNo"] = "Modl", ["TLst"] = "Tex", ["TxNo"] = "Tex"
    };

    public CompileResult CompileForPreview(WorkProject project, Guid? solo = null)
        => Compile(project with { Target = new EquipmentTarget() }, solo);

    public CompileResult Compile(WorkProject project, Guid? solo = null)
    {
        var diagnostics = new List<Diagnostic>();
        var roots = new List<List<Block>>();
        var dependencies = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var layer in project.Layers.Where(l => l.Visible && (solo is null || l.Id == solo)))
        {
            try
            {
                ValidateLayer(layer);
                if (project.Target.PrimaryId != 0 && project.Target.Kind == EquipmentKind.Weapon && layer.Anchor != Anchor.Original)
                    throw new InvalidDataException("武器目标当前仅支持保留原挂点");
                var root = Blocks.Root(resources.Read(layer.Resource ?? ResourceReference.GamePath(layer.Source)));
                Validate(root);
                if (root.Count(b => b.Tag == "Schd") != 1)
                    throw new InvalidDataException("当前只支持单调度器特效，请选择其他特效");
                if (root.Any(b => b.Tag == "Efct"))
                    throw new InvalidDataException("含灯光／屏幕效果节点，暂不支持装备组合，防止导出改变画面");
                foreach (var texture in root.Where(b => b.Tag == "Tex").Concat(root.Where(b => b.Tag == "Emit").SelectMany(b => b.Children().Where(x => x.Tag == "SdNm"))))
                {
                    var path = Encoding.UTF8.GetString(texture.Data).TrimEnd('\0');
                    if (path.Length == 0) continue;
                    CheckGamePath(path);
                    dependencies.TryAdd(path, resources.Read(path));
                }
                ApplyLayer(root, layer);
                Validate(root);
                roots.Add(root);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or OverflowException)
            { diagnostics.Add(new(layer.Id, layer.Name, ex.Message)); }
        }
        if (diagnostics.Any(d => d.Error)) return new(null, diagnostics);
        if (roots.Count == 0) return new(null, [new(null, "工程", "至少需要一个可见特效图层")]);
        try
        {
            var merged = Merge(roots);
            var bytes = Blocks.File(merged);
            Validate(Blocks.Root(bytes));
            return new(new(bytes, dependencies), diagnostics);
        }
        catch (Exception ex) when (ex is InvalidDataException or OverflowException)
        { return new(null, [new(null, "组合", ex.Message)]); }
    }

    public static void CheckGamePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains('\\') || path.Contains(':') || path.Split('/').Any(x => x is ".." or "." or ""))
            throw new InvalidDataException("资源路径必须是游戏内相对路径");
    }
    private static void ValidateLayer(Layer l)
    {
        var reference = l.Resource ?? ResourceReference.GamePath(l.Source);
        reference.Validate();
        if (reference.Kind == ResourceKind.GamePath && !reference.Path.EndsWith(".avfx", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("请选择 AVFX 特效");
        var values = new[] { l.Position.X, l.Position.Y, l.Position.Z, l.RotationDegrees.X, l.RotationDegrees.Y, l.RotationDegrees.Z, l.Scale, l.Color.X, l.Color.Y, l.Color.Z, l.Color.W };
        if (values.Any(x => !float.IsFinite(x)) || l.Scale is < .01f or > 100 || l.Color.X < 0 || l.Color.Y < 0 || l.Color.Z < 0 || l.Color.W is < 0 or > 1)
            throw new InvalidDataException("变换或颜色数值超出范围");
        if (!Enum.IsDefined(l.Anchor) || !Enum.IsDefined(l.Playback)) throw new InvalidDataException("不支持的挂点或播放模式");
        if (l.LoopFrames is < 1 or > 30000 || l.GapFrames < 0 || l.GapFrames > 30000 - l.LoopFrames)
            throw new InvalidDataException("循环总时长须为 1–30000 帧");
        if (l.Anchor == Anchor.Weapon) throw new InvalidDataException("武器挂点需按目标骨架验证，当前请保留原挂点或使用身体挂点");
    }

    private static void ApplyLayer(List<Block> root, Layer layer)
    {
        // Bake tint into each particle's own color curves once; do not compound it down emitter chains.
        if (layer.Color != Vector4.One)
        {
            for (int i = 0; i < root.Count; i++) if (root[i].Tag == "Ptcl")
            {
                var particle = root[i].Children();
                if (particle.Find("Col") is not { } color) throw new InvalidDataException("此粒子没有标准颜色曲线，无法可靠统一调色");
                particle.Set(Tint(color, layer.Color));
                // Additional model/line colors multiply the base color; leave them intact.
                root[i] = Block.Group("Ptcl", particle);
            }
        }
        int? binderIndex = null;
        if (layer.Anchor != Anchor.Original)
        {
            if (root.Where(b => b.Tag == "Bind").Any(b => b.Children().Number("BnVr", 0) != 0))
                throw new InvalidDataException("此特效使用多点／相机绑定，暂不能改为单身体挂点");
            binderIndex = root.Count(b => b.Tag == "Bind");
            root.Add(CreateBinder(layer.Anchor));
        }
        bool needsTransform = layer.Position != Vector3.Zero || layer.RotationDegrees != Vector3.Zero || layer.Scale != 1;
        var emitters = root.Where(b => b.Tag == "Emit").ToList();
        var wrappers = new Dictionary<int, int>();
        for (int i = 0; i < root.Count; i++) if (root[i].Tag == "TmLn")
        {
            var timeline = root[i].Children();
            var items = timeline.Entries("Item", "bEna");
            if (binderIndex.HasValue) timeline.Set(Block.Int("BnNo", binderIndex.Value));
            foreach (var item in items)
            {
                if (binderIndex.HasValue) item.Set(Block.Int("BdNo", binderIndex.Value));
                int source = item.Number("EmNo");
                if (source < 0 || !needsTransform) continue;
                if (source >= emitters.Count) throw new InvalidDataException("时间线引用无效发射器");
                if (!wrappers.TryGetValue(source, out int wrapper))
                {
                    wrapper = emitters.Count + wrappers.Count;
                    wrappers.Add(source, wrapper);
                    root.Add(CreateWrapper(source, layer));
                }
                item.Set(Block.Int("EmNo", wrapper));
            }
            timeline.SetEntries("Item", items);
            if (layer.Playback == Playback.Loop)
            {
                if (timeline.Number("CpCn", 0) != 0) throw new InvalidDataException("含裁剪片段的特效暂不能自动循环");
                if (items.Any(x => x.Number("EdTm") < 0 || x.Number("EdTm") > layer.LoopFrames))
                    throw new InvalidDataException("循环长度必须覆盖原时间线，且不支持含无限持续条目的自动重播");
                timeline.Set(Block.Int("LpSt", 0));
                timeline.Set(Block.Int("LpEd", checked(layer.LoopFrames + layer.GapFrames)));
            }
            root[i] = Block.Group("TmLn", timeline);
        }
        UpdateCounts(root);
    }

    private static Block CreateWrapper(int child, Layer l)
    {
        var item = new List<Block>
        {
            Block.Int("bEnb", 1), Block.Int("TgtB", child), Block.Int("LoDr", 0), Block.Int("CrTm", 1),
            Block.Int("CrCn", 1), Block.Int("CrPr", 100), Block.Int("PICd", 2), Block.Int("PICo", 0),
            Block.Int("ICbS", 1), Block.Int("ICbR", 1), Block.Int("ICbP", 1), Block.Int("ICbB", 0),
            Block.Float("ICSK", 0), Block.Int("IPbV", 0), Block.Int("IPbL", 0), Block.Int("bOvr", 0),
            Block.Int("OvrV", 60), Block.Int("OvrR", 0), Block.Int("PrLk", -1), Block.Int("StFr", 0),
            Block.Int("bStN", 0), Block.Float("BIAX", 0), Block.Float("BIAY", 0), Block.Float("BIAZ", 0),
            Block.Int("GenD", 0), Block.Int("bGD", 0)
        };
        return Block.Group("Emit", new List<Block>
        {
            Block.Int("EVT", 0), Block.Int("SdNo", -1), Block.Int("LpSt", 0), Block.Int("LpEd", 0),
            Block.Int("EfNo", -1), Block.Int("ClCn", 1), Block.Int("PrCn", 0), Block.Int("EmCn", 1),
            Block.Int("RBDT", 0), Block.Int("CCOT", 0), Block.Int("ROT", 0),
            new("bAD", [0]), Block.Int("Life", -1), Curve("CrC", 1), Curve("CrI", 1),
            Axes("Pos", l.Position), Axes("Rot", l.RotationDegrees * (MathF.PI / 180)),
            Axes("Scl", new(l.Scale)), WhiteColor(), Block.Group("ItEm", item)
        });
    }
    public static Block Curve(string tag, float value)
    {
        using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
        w.Write((short)0); w.Write((short)1); w.Write(0f); w.Write(0f); w.Write(value);
        return Block.Group(tag, [Block.Int("KeyC", 1), Block.Int("BvPr", 0), Block.Int("BvPo", 0), Block.Int("RanT", 0), new("Keys", stream.ToArray())]);
    }
    private static Block WhiteColor()
    {
        var rgb = Curve("RGB", 1).Children();
        var data = rgb.Find("Keys")!.Data.ToArray();
        BitConverter.GetBytes(1f).CopyTo(data, 4); BitConverter.GetBytes(1f).CopyTo(data, 8);
        rgb.Set(new("Keys", data)); rgb.RemoveAll(x => x.Tag == "RanT");
        return Block.Group("Col", [Block.Group("RGB", rgb), Curve("A", 1), Curve("Bri", 1), Curve("SclA", 1)]);
    }
    private static Block Axes(string tag, Vector3 v) => Block.Group(tag, [Block.Int("ACT", 0), Block.Int("ACTR", 0), Curve("X", v.X), Curve("Y", v.Y), Curve("Z", v.Z)]);
    private static Block CreateBinder(Anchor anchor)
    {
        int eid = anchor switch { Anchor.Head => 25, Anchor.LeftHand => 33, Anchor.RightHand => 32, Anchor.Body => 30, _ => 0 };
        var prop = new List<Block> { Block.Int("BPT", 0), Block.Int("BPTP", eid == 0 ? 0 : 3), Block.Int("BPID", eid), Block.Int("GenD", 0), Block.Int("CoUF", -1), Axes("Pos", Vector3.Zero) };
        return Block.Group("Bind", [Block.Int("BnVr", 0), Block.Int("bFTO", 1), Block.Int("bTSc", 1), Block.Int("bDSE", 1), Block.Int("RoTp", 0), Block.Int("Life", -1), Block.Group("PrpS", prop)]);
    }
    private static Block Tint(Block color, Vector4 tint)
    {
        var fields = color.Children();
        if (tint.X != 1 || tint.Y != 1 || tint.Z != 1)
        {
            if (fields.Find("RGB") is not { } rgb) throw new InvalidDataException("颜色缺少 RGB 曲线");
            fields.Set(MultiplyCurve(rgb, new(tint.X, tint.Y, tint.Z), true));
            foreach (var (tag, factor) in new[] { ("RanR", tint.X), ("RanG", tint.Y), ("RanB", tint.Z) })
                if (fields.Find(tag) is { } random) fields.Set(MultiplyCurve(random, new(factor), false));
        }
        if (tint.W != 1)
        {
            if (fields.Find("A") is not { } alpha) throw new InvalidDataException("颜色缺少透明度曲线");
            fields.Set(MultiplyCurve(alpha, new(tint.W), false));
            if (fields.Find("RanA") is { } random) fields.Set(MultiplyCurve(random, new(tint.W), false));
        }
        return Block.Group(color.Tag, fields);
    }
    private static Block MultiplyCurve(Block curve, Vector3 factors, bool rgb)
    {
        var fields = curve.Children();
        if (fields.Find("Keys") is not { } keys || keys.Data.Length % 16 != 0) throw new InvalidDataException("不支持的颜色曲线格式");
        var data = keys.Data.ToArray();
        for (int i = 0; i < data.Length; i += 16)
        {
            if (rgb)
            {
                for (int axis = 0; axis < 3; axis++)
                    BitConverter.GetBytes(BitConverter.ToSingle(data, i + 4 + axis * 4) * factors[axis]).CopyTo(data, i + 4 + axis * 4);
            }
            else for (int axis = 0; axis < 3; axis++)
                BitConverter.GetBytes(BitConverter.ToSingle(data, i + 4 + axis * 4) * factors.X).CopyTo(data, i + 4 + axis * 4);
        }
        fields.Set(new("Keys", data)); return Block.Group(curve.Tag, fields);
    }

    public static List<Block> Merge(IReadOnlyList<List<Block>> roots)
    {
        if (roots.Count == 1) return Canonical(roots[0]);
        var first = roots[0];
        var output = first.Where(b => !Groups.ContainsKey(b.Tag) && !Groups.ContainsValue(b.Tag)).ToList();
        var offsets = Groups.Keys.ToDictionary(x => x, _ => 0);
        var schedulerItems = new List<List<Block>>();
        var triggerSources = new List<List<List<Block>>>();
        foreach (var root in roots)
        {
            // Global settings cannot differ across a single AVFX; fail instead of silently taking layer one.
            foreach (var setting in root.Where(b => !Groups.ContainsKey(b.Tag) && !Groups.ContainsValue(b.Tag)))
                if (output.Find(setting.Tag) is not { } baseline || !setting.Data.SequenceEqual(baseline.Data))
                    throw new InvalidDataException($"图层全局设置 {setting.Tag} 不兼容，暂不能组合");
            if (output.Any(b => root.Find(b.Tag) is null)) throw new InvalidDataException("图层全局设置不一致");
            var schedulers = root.Where(b => b.Tag == "Schd").ToList();
            if (schedulers.Count != 1) throw new InvalidDataException("只支持单调度器组合");
            var scheduler = Remap(schedulers[0], offsets).Children();
            var items = scheduler.Entries("Item", "bEna");
            schedulerItems.AddRange(items);
            var combined = scheduler.Entries("Trgr", "bEna");
            int triggerCount = scheduler.Number("TrCn", 0);
            var sourceTriggers = combined.TakeLast(triggerCount).ToList();
            triggerSources.Add(sourceTriggers);
            foreach (var block in root.Where(b => Groups.ContainsKey(b.Tag) && b.Tag != "Schd")) output.Add(Remap(block, offsets));
            foreach (var tag in Groups.Keys) offsets[tag] += root.Count(b => b.Tag == tag);
        }
        var triggers = new List<List<Block>>();
        int maxTriggers = triggerSources.Max(x => x.Count);
        for (int slot = 0; slot < maxTriggers; slot++)
        {
            var active = triggerSources.Where(x => slot < x.Count).Select(x => x[slot]).Where(x => x.Number("TlNo") >= 0 && x.Number("bEna", 0) != 0).ToList();
            if (active.Count == 0) { triggers.Add([Block.Int("bEna", 0), Block.Int("StTm", 0), Block.Int("TlNo", -1)]); continue; }
            var trigger = active[0].ToList();
            if (active.Any(x => x.Number("StTm", 0) != trigger.Number("StTm", 0))) throw new InvalidDataException("事件触发延迟不兼容");
            if (active.Count > 1)
            {
                var timelines = output.Where(b => b.Tag == "TmLn").ToList();
                var combined = JoinTimelines(active.Select(x => timelines[x.Number("TlNo")]).ToList());
                trigger.Set(Block.Int("TlNo", timelines.Count)); output.Add(combined);
            }
            triggers.Add(trigger);
        }
        var sched = new List<Block> { Block.Int("ItCn", schedulerItems.Count), Block.Int("TrCn", triggers.Count) };
        sched.SetEntries("Item", schedulerItems);
        if (triggers.Count > 0)
        {
            var accumulated = schedulerItems.ToList();
            foreach (var trigger in triggers) { accumulated.Add(trigger); sched.Add(Block.Group("Trgr", accumulated.SelectMany(x => x))); }
        }
        output.Insert(0, Block.Group("Schd", sched));
        UpdateCounts(output);
        var ordered = Canonical(output);
        Validate(ordered); return ordered;
    }
    private static List<Block> Canonical(List<Block> root)
    {
        var ordered = root.Where(b => !Groups.ContainsKey(b.Tag) && !Groups.ContainsValue(b.Tag)).ToList();
        foreach (var count in Groups.Values) ordered.Add(root.Find(count) ?? Block.Int(count, 0));
        foreach (var tag in Groups.Keys) ordered.AddRange(root.Where(b => b.Tag == tag));
        return ordered;
    }

    private static Block JoinTimelines(List<Block> timelines)
    {
        var allItems = new List<List<Block>>(); var clips = new List<Block>();
        var first = timelines[0].Children();
        var header = first.Where(b => b.Tag is not "Item" and not "Clip" and not "TICn" and not "CpCn" and not "BnNo").ToList();
        foreach (var timeline in timelines)
        {
            var fields = timeline.Children();
            foreach (var setting in header)
                if (fields.Find(setting.Tag) is not { } other || !setting.Data.SequenceEqual(other.Data)) throw new InvalidDataException("事件触发时间线的循环／全局配置不兼容");
            if (fields.Any(b => b.Tag is not "Item" and not "Clip" and not "TICn" and not "CpCn" and not "BnNo" && header.Find(b.Tag) is null))
                throw new InvalidDataException("事件触发时间线包含不同的全局字段");
            foreach (var item in fields.Entries("Item", "bEna"))
            {
                if (item.Number("BdNo") < 0) item.Set(Block.Int("BdNo", fields.Number("BnNo")));
                if (item.Number("ClNo") >= 0) item.Set(Block.Int("ClNo", item.Number("ClNo") + clips.Count));
                allItems.Add(item);
            }
            clips.AddRange(fields.Where(x => x.Tag == "Clip"));
        }
        header.Add(Block.Int("BnNo", -1)); header.Add(Block.Int("TICn", allItems.Count)); header.Add(Block.Int("CpCn", clips.Count));
        header.SetEntries("Item", allItems); header.AddRange(clips);
        return Block.Group("TmLn", header);
    }

    private static Block Remap(Block block, Dictionary<string, int> offsets, string parent = "")
    {
        if (References.TryGetValue(block.Tag, out var group) || block.Tag == "TgtB")
        {
            group = block.Tag == "TgtB" ? parent == "ItPr" ? "Ptcl" : "Emit" : group;
            var data = block.Data.ToArray();
            if (block.Tag is "TLst" or "MdNo")
            {
                for (int i = 0; i < data.Length; i++) if ((sbyte)data[i] >= 0) data[i] = checked((byte)(data[i] + offsets[group!]));
            }
            else if (block.Integer >= 0) data = BitConverter.GetBytes(checked(block.Integer + offsets[group!]));
            return new(block.Tag, data);
        }
        if (Groups.ContainsKey(block.Tag) && block.Tag is not "Tex" and not "Modl" || Containers.Contains(block.Tag))
            return Block.Group(block.Tag, block.Children().Select(x => Remap(x, offsets, block.Tag)));
        return block;
    }
    private static void UpdateCounts(List<Block> root)
    { foreach (var (group, count) in Groups) root.Set(Block.Int(count, root.Count(b => b.Tag == group))); }

    public static void Validate(List<Block> root)
    {
        var sizes = Groups.Keys.ToDictionary(x => x, x => root.Count(b => b.Tag == x));
        foreach (var (group, count) in Groups)
        {
            if (sizes[group] > 127) throw new InvalidDataException($"{group} 数量超过国服已知容量 127，请减少图层");
            if (root.Number(count, 0) != sizes[group]) throw new InvalidDataException($"{group} 声明数量与内容不一致");
        }
        foreach (var block in root) ValidateNode(block, sizes);
        foreach (var block in root) ValidatePayload(block);
        ValidateEmitterCycles(root);
    }
    private static void ValidatePayload(Block block)
    {
        if (block.Tag is "Keys" or "VDrw" or "VIdx" or "Tex" or "Clip") return;
        if (!Blocks.TryRead(block.Data, out var children)) return;
        if (children.Find("KeyC") is { } count)
        {
            if (count.Integer is < 0 or > 127 || children.Find("Keys") is not { } keys || keys.Data.Length != count.Integer * 16)
                throw new InvalidDataException("动画曲线关键帧数量超出容量或长度不匹配");
        }
        if (block.Tag == "Modl")
        {
            var vertices = children.Find("VDrw")?.Data ?? [];
            var indices = children.Find("VIdx")?.Data ?? [];
            if (vertices.Length % 36 != 0 || indices.Length % 6 != 0 || vertices.Length / 36 > 65535 || indices.Length / 2 > 65535)
                throw new InvalidDataException("内嵌模型缓冲区长度无效或超出容量");
            for (int i = 0; i < indices.Length; i += 2)
                if (BitConverter.ToUInt16(indices, i) >= vertices.Length / 36) throw new InvalidDataException("内嵌模型顶点引用越界");
            return;
        }
        foreach (var child in children) ValidatePayload(child);
    }
    private static void ValidateNode(Block block, Dictionary<string, int> sizes, string parent = "")
    {
        if (References.TryGetValue(block.Tag, out var group) || block.Tag == "TgtB")
        {
            group = block.Tag == "TgtB" ? parent == "ItPr" ? "Ptcl" : "Emit" : group;
            var indices = block.Tag is "TLst" or "MdNo" ? block.Data.Select(x => (int)(sbyte)x) : new[] { block.Integer }.AsEnumerable();
            if (indices.Any(x => x < -1 || x >= sizes[group!])) throw new InvalidDataException($"{parent}/{block.Tag} 引用越界");
            return;
        }
        if (Groups.ContainsKey(block.Tag) && block.Tag is not "Tex" and not "Modl" || Containers.Contains(block.Tag))
        {
            List<Block> fields;
            try { fields = block.Children(); }
            catch (InvalidDataException ex) { throw new InvalidDataException($"{parent}/{block.Tag} ({block.Data.Length} bytes)：{ex.Message}"); }
            if (block.Tag == "Emit") foreach (var (tag, count) in new[] { ("ItPr", "PrCn"), ("ItEm", "EmCn") })
                if (fields.Entries(tag, "bEnb").Count != fields.Number(count, 0)) throw new InvalidDataException("发射器子节点数量不一致");
            if (block.Tag == "TmLn" && fields.Entries("Item", "bEna").Count != fields.Number("TICn", 0)) throw new InvalidDataException("时间线条目数量不一致");
            if (block.Tag == "TmLn" && fields.Count(x => x.Tag == "Clip") != fields.Number("CpCn", 0)) throw new InvalidDataException("时间线裁剪数量不一致");
            if (block.Tag == "Schd")
            {
                int items = fields.Entries("Item", "bEna").Count;
                int triggers = fields.Number("TrCn", 0);
                if (items != fields.Number("ItCn", 0) || triggers is < 0 or > 12 || triggers > 0 && fields.Entries("Trgr", "bEna").Count != items + triggers)
                    throw new InvalidDataException("调度器条目／触发槽数量不一致");
            }
            foreach (var child in fields) ValidateNode(child, sizes, block.Tag);
        }
    }
    private static void ValidateEmitterCycles(List<Block> root)
    {
        var emitters = root.Where(b => b.Tag == "Emit").Select(b => b.Children()).ToList();
        var state = new byte[emitters.Count];
        void Visit(int index)
        {
            if (state[index] == 1) throw new InvalidDataException("发射器存在递归环，拒绝加载");
            if (state[index] == 2) return;
            state[index] = 1;
            foreach (var item in emitters[index].Entries("ItEm", "bEnb")) if (item.Number("TgtB") >= 0) Visit(item.Number("TgtB"));
            state[index] = 2;
        }
        for (int i = 0; i < emitters.Count; i++) Visit(i);
    }
}
