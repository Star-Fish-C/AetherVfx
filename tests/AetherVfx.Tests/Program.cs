using AetherVfx.Core;
using System.Numerics;
using System.IO.Compression;
using System.Text.Json;

int count = 0;
void Test(string name, Action action) { action(); count++; Console.WriteLine("PASS " + name); }
void Assert(bool value, string message) { if (!value) throw new Exception(message); }
void Throws(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("应拒绝无效输入"); }
var source = Fixture();
var resources = new MemoryResources(); resources.Files["vfx/test.avfx"] = source;
resources.Files["vfx/test.atex"] = [1, 2, 3];
var compiler = new EffectCompiler(resources);
Test("崩溃保护默认禁止全部原生预览入口", () =>
{
    Assert(!AetherVfx.NativePreviewSafety.Enabled, "原生预览意外恢复");
    bool rejected = false;
    try { AetherVfx.NativePreviewSafety.RequireEnabled(); }
    catch (InvalidOperationException) { rejected = true; }
    Assert(rejected, "崩溃保护未阻止原生操作");
});
Test("预览槽采用客户端返回 ID，兼容国服索引 7 对应槽 247", () =>
{
    Assert(AetherVfx.PreviewViewSlot.Validate(7, 7, 247, 249) == 247, "仍假设角色槽为 47");
    Assert(AetherVfx.PreviewViewSlot.Validate(7, 7, 47, 49) == 47, "旧布局兼容失败");
});
Test("错误视图索引和越界角色槽在原生访问前拒绝", () =>
{
    foreach (var args in new (uint Requested, uint Actual, uint Id, int Capacity)[] { (47, 47, 287, 249), (7, 6, 247, 249), (7, 7, 249, 249) })
    {
        bool rejected = false;
        try { AetherVfx.PreviewViewSlot.Validate(args.Requested, args.Actual, args.Id, args.Capacity); }
        catch (InvalidOperationException) { rejected = true; }
        Assert(rejected, "接受了无效槽位");
    }
});
WorkProject Project(int layers = 1) => new() { Layers = Enumerable.Range(0, layers).Select(i => new Layer { Name = $"层{i}", Source = "vfx/test.avfx" }).ToList() };

Test("DLL 从内存加载时使用宿主安装路径查找资源", () =>
{
    string assemblyPath = typeof(WorkProject).Assembly.Location;
    var memoryLoaded = System.Reflection.Assembly.Load(File.ReadAllBytes(assemblyPath));
    Assert(string.IsNullOrEmpty(memoryLoaded.Location), "未复现内存加载环境");
    string assets = AetherVfx.PluginAssetPaths.AssetsDirectory(new FileInfo(assemblyPath));
    Assert(assets == Path.Combine(Path.GetDirectoryName(assemblyPath)!, "assets"), "资源路径未使用宿主安装目录");
});
Test("中文及含空格的开发插件安装目录不依赖当前工作目录", () =>
{
    var installed = new FileInfo(Path.Combine(Path.GetTempPath(), "插件 开发目录", "AetherVfx.dll"));
    Assert(AetherVfx.PluginAssetPaths.AssetsDirectory(installed) == Path.Combine(installed.Directory!.FullName, "assets"), "安装目录解析错误");
});

Test("真实标签字节顺序、未知字段及对齐往返", () =>
{
    var bytes = Blocks.Write([Block.Int("Ver", 5), new("Tex", [1, 2, 3])]);
    Assert(bytes[0] == (byte)'r' && bytes[1] == (byte)'e' && bytes[2] == (byte)'V' && bytes[3] == 0, "标签必须左侧补零再倒序");
    Assert(bytes.SequenceEqual(Blocks.Write(Blocks.Read(bytes))), "往返改变原始文件");
    Throws(() => Blocks.Root(bytes)); Throws(() => Blocks.Read(bytes[..^1]));
});
Test("工程向量、枚举、中文名称、版本校验与撤销重做", () =>
{
    var p = Project(); p.Layers[0].Position = new(1, 2, 3);
    var restored = ProjectStore.Parse(ProjectStore.Serialize(p));
    Assert(restored.Layers[0].Position == new Vector3(1, 2, 3), "向量丢失");
    var h = new ProjectHistory(); h.Record(p); p.Layers[0].Scale = 3; p = h.Undo(p);
    Assert(p.Layers[0].Scale == 1 && h.Redo(p).Layers[0].Scale == 3, "撤销重做丢失状态");
    Throws(() => ProjectStore.Parse("{\"Version\":999}"));
});
Test("三个图层合并，纹理／模型／发射器引用逐层重映射", () =>
{
    var result = compiler.Compile(Project(3)); Assert(result.Success, string.Join(';', result.Diagnostics.Select(x => x.Message)));
    var root = Blocks.Root(result.Effect!.Avfx); EffectCompiler.Validate(root);
    Assert(root.Number("PrCn") == 3 && root.Number("TxCn") == 3, "合并计数错误");
    var particles = root.Where(b => b.Tag == "Ptcl").ToList();
    Assert(particles[2].Children().Find("TC1")!.Children().Number("TxNo") == 2, "纹理未重映射");
    Assert((sbyte)particles[2].Children().Find("Data")!.Children().Find("MdNo")!.Data[0] == 2, "模型未重映射");
    Assert(result.Effect.Dependencies.Count == 1, "依赖未去重");
});
Test("整体变换添加父发射器，原动画逐字节保留", () =>
{
    var p = Project(); p.Layers[0].Scale = 2; p.Layers[0].Position = new(1, 2, 3); p.Layers[0].RotationDegrees = new(0, 90, 0);
    var result = compiler.Compile(p); Assert(result.Success, "变换编译失败");
    var root = Blocks.Root(result.Effect!.Avfx);
    Assert(root.First(x => x.Tag == "Ptcl").Data.SequenceEqual(Blocks.Root(source).First(x => x.Tag == "Ptcl").Data), "改坏原粒子动画");
    var wrapper = root.Last(x => x.Tag == "Emit").Children();
    float value = BitConverter.ToSingle(wrapper.Find("Scl")!.Children().Find("X")!.Children().Find("Keys")!.Data, 12);
    Assert(value == 2, "缩放未烘焙");
});
Test("颜色和透明度乘算保留曲线时间与原亮度", () =>
{
    var p = Project(); p.Layers[0].Color = new(.5f, 1, .25f, .4f);
    var result = compiler.Compile(p); Assert(result.Success, "调色失败");
    var color = Blocks.Root(result.Effect!.Avfx).First(x => x.Tag == "Ptcl").Children().Find("Col")!.Children();
    var rgb = color.Find("RGB")!.Children().Find("Keys")!.Data;
    Assert(BitConverter.ToSingle(rgb, 4) == .5f && BitConverter.ToSingle(rgb, 12) == .25f, "RGB 乘算错误");
    Assert(BitConverter.ToSingle(color.Find("A")!.Children().Find("Keys")!.Data, 12) == .4f, "透明度错误");
});
Test("循环与间隔写进 AVFX，不依赖定时刷新", () =>
{
    var p = Project(); p.Layers[0].Playback = Playback.Loop; p.Layers[0].LoopFrames = 120; p.Layers[0].GapFrames = 30;
    var result = compiler.Compile(p); Assert(result.Success, "循环失败");
    Assert(Blocks.Root(result.Effect!.Avfx).First(x => x.Tag == "TmLn").Children().Number("LpEd") == 150, "循环未写入");
});
Test("缺失依赖、非数、无效挂点、越界引用阻止编译", () =>
{
    var p = Project(); p.Layers[0].Scale = float.NaN; Assert(!compiler.Compile(p).Success, "允许非数");
    p = Project(); p.Layers[0].Anchor = Anchor.Weapon; Assert(!compiler.Compile(p).Success, "允许未知武器挂点");
    resources.Files.Remove("vfx/test.atex"); Assert(!compiler.Compile(Project()).Success, "缺失贴图仍成功"); resources.Files["vfx/test.atex"] = [1, 2, 3];
    var root = Blocks.Root(source); var particle = root.First(x => x.Tag == "Ptcl").Children();
    particle.Set(Block.Group("TC1", [Block.Int("TxNo", 99)])); root[root.FindIndex(x => x.Tag == "Ptcl")] = Block.Group("Ptcl", particle);
    Throws(() => EffectCompiler.Validate(root));
});
Test("隐藏、单独预览与国服节点容量限制", () =>
{
    var p = Project(3); p.Layers[1].Visible = false;
    Assert(Blocks.Root(compiler.Compile(p).Effect!.Avfx).Number("PrCn") == 2, "隐藏层仍在产物中");
    Assert(Blocks.Root(compiler.Compile(p, p.Layers[0].Id).Effect!.Avfx).Number("PrCn") == 1, "单独显示失败");
    Assert(!compiler.Compile(Project(128)).Success, "允许容量溢出");
});
Test("防具／武器／饰品 IMC 只改特效 ID，保留材质与声音", () =>
{
    foreach (var kind in Enum.GetValues<EquipmentKind>())
    {
        var t = Target(kind); resources.Files[t.ImcPath] = Imc();
        var bound = new EquipmentBinder(resources).Bind(compiler.Compile(Project()).Effect!, t);
        string json = JsonSerializer.Serialize(bound.Manipulations);
        Assert(json.Contains("\"MaterialId\":7") && json.Contains("\"SoundId\":3") && json.Contains("\"VfxId\":2"), "元数据字段不正确");
        Assert(t.Kind != EquipmentKind.Accessory || t.VfxPath.Contains("accessory/a"), "饰品路径错误");
    }
});
Test("PMP 包含可独立加载的映射、元数据、依赖及工程", () =>
{
    var p = Project(3); p.Target = Target(EquipmentKind.Equipment); resources.Files[p.Target.ImcPath] = Imc();
    var bound = new EquipmentBinder(resources).Bind(compiler.Compile(p).Effect!, p.Target);
    var path = Path.Combine(Path.GetTempPath(), "AetherVfx-test-" + Guid.NewGuid() + ".pmp");
    try
    {
        PmpExporter.Export(path, p, bound);
        using var zip = ZipFile.OpenRead(path);
        Assert(zip.GetEntry("meta.json") is not null && zip.GetEntry("aethervfx-project.json") is not null, "缺少元数据");
        using var stream = zip.GetEntry("default_mod.json")!.Open(); using var doc = JsonDocument.Parse(stream);
        foreach (var f in doc.RootElement.GetProperty("Files").EnumerateObject()) Assert(zip.GetEntry(f.Value.GetString()!) is not null, "映射缺少目标文件");
        using var input = new MemoryStream(Convert.FromBase64String(PmpExporter.CompressedManipulations(bound.Manipulations)));
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        Assert(gzip.ReadByte() == 0, "IPC 元数据版本错误");
    }
    finally { File.Delete(path); }
});
Test("事件触发槽合并后保留每层的事件时间线", () =>
{
    var root = Blocks.Root(source);
    var scheduler = root.First(x => x.Tag == "Schd").Children();
    scheduler.Set(Block.Int("TrCn", 1));
    scheduler.Add(Block.Group("Trgr", scheduler.Entries("Item", "bEna").SelectMany(x => x).Concat(new[] { Block.Int("bEna", 1), Block.Int("StTm", 0), Block.Int("TlNo", 0) })));
    root[root.FindIndex(x => x.Tag == "Schd")] = Block.Group("Schd", scheduler);
    var merged = EffectCompiler.Merge([root, root]);
    Assert(merged.Number("TlCn") == 3, "没有创建组合事件时间线");
    var trigger = merged.First(x => x.Tag == "Schd").Children().Entries("Trgr", "bEna").Last();
    Assert(trigger.Number("TlNo") == 2, "触发槽没有引用新时间线");
    Assert(merged.Last(x => x.Tag == "TmLn").Children().Number("TICn") == 2, "事件时间线丢失某层");
});
Test("递归发射器、损坏曲线和不同全局配置拒绝合并", () =>
{
    var root = Blocks.Root(source);
    var emitter = root.First(x => x.Tag == "Emit").Children();
    emitter.Set(Block.Int("EmCn", 1)); emitter.Add(Block.Group("ItEm", [Block.Int("bEnb", 1), Block.Int("TgtB", 0)]));
    root[root.FindIndex(x => x.Tag == "Emit")] = Block.Group("Emit", emitter);
    Throws(() => EffectCompiler.Validate(root));
    root = Blocks.Root(source); root.Add(Block.Group("Extra", [Block.Int("KeyC", 500), new("Keys", [])]));
    Throws(() => EffectCompiler.Validate(root));
    var other = Blocks.Root(source); other.Set(Block.Int("Ver", 123));
    Throws(() => EffectCompiler.Merge([Blocks.Root(source), other]));
});
Test("保留原装备特效时同时合并节点和依赖", () =>
{
    var t = Target(EquipmentKind.Equipment) with { KeepOriginal = true };
    var imc = Imc(); imc[14] = 1; resources.Files[t.ImcPath] = imc;
    resources.Files[(t with { VfxId = 1 }).VfxPath] = source;
    var bound = new EquipmentBinder(resources).Bind(compiler.Compile(Project()).Effect!, t);
    Assert(bound.OriginalVfxId == 1 && Blocks.Root(bound.Effect.Avfx).Number("PrCn") == 2, "原特效丢失");
});
Test("头部挂点写入绑定节点，所有主时间线引用该节点", () =>
{
    var p = Project(); p.Layers[0].Anchor = Anchor.Head;
    var result = compiler.Compile(p); Assert(result.Success, "挂点编译失败");
    var root = Blocks.Root(result.Effect!.Avfx);
    Assert(root.First(x => x.Tag == "Bind").Children().Find("PrpS")!.Children().Number("BPID") == 25, "头部绑定 ID 错误");
    Assert(root.First(x => x.Tag == "TmLn").Children().Number("BnNo") == 0, "时间线挂点未引用");
});
count += CatalogTests.Run(source, resources);
count += PreviewSessionTests.Run();
if (args.Length > 0)
{
    foreach (var file in Directory.EnumerateFiles(args[0], "*.avfx"))
        Test("真实样本结构往返：" + Path.GetFileName(file), () =>
        {
            var bytes = File.ReadAllBytes(file); var root = Blocks.Root(bytes);
            Assert(bytes.SequenceEqual(Blocks.File(root)), "原始结构往返不一致"); EffectCompiler.Validate(root);
        });
}
Console.WriteLine($"{count} tests passed");

static EquipmentTarget Target(EquipmentKind kind) => new() { Name = "测试装备", PrimaryId = 1, SecondaryId = kind == EquipmentKind.Weapon ? (ushort)1 : (ushort)0, Variant = 1, VfxId = 2, Kind = kind, Slot = kind == EquipmentKind.Weapon ? Slot.MainHand : kind == EquipmentKind.Accessory ? Slot.Ears : Slot.Head };
static byte[] Imc()
{
    using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
    w.Write((ushort)1); w.Write((ushort)1);
    for (int i = 0; i < 2; i++) { w.Write((byte)7); w.Write((byte)0); w.Write((ushort)((3 << 10) | 5)); w.Write((byte)0); w.Write((byte)1); }
    return stream.ToArray();
}
static byte[] Fixture()
{
    var rgb = EffectCompiler.Curve("RGB", 1).Children();
    var keys = rgb.Find("Keys")!.Data.ToArray(); BitConverter.GetBytes(1f).CopyTo(keys, 4); BitConverter.GetBytes(1f).CopyTo(keys, 8); rgb.Set(new("Keys", keys));
    var root = new List<Block>
    {
        Block.Int("Ver", 0x20110913),
        Block.Group("Schd", [Block.Int("ItCn", 1), Block.Int("TrCn", 0), Block.Group("Item", [Block.Int("bEna", 1), Block.Int("StTm", 0), Block.Int("TlNo", 0)])]),
        Block.Group("TmLn", [Block.Int("BnNo", -1), Block.Int("TICn", 1), Block.Int("CpCn", 0), Block.Group("Item", [Block.Int("bEna", 1), Block.Int("StTm", 0), Block.Int("EdTm", 60), Block.Int("EmNo", 0), Block.Int("BdNo", -1), Block.Int("EfNo", -1)])]),
        Block.Group("Emit", [Block.Int("EVT", 0), Block.Int("PrCn", 1), Block.Int("EmCn", 0), Block.Int("EfNo", -1), Block.Group("ItPr", [Block.Int("bEnb", 1), Block.Int("TgtB", 0)])]),
        Block.Group("Ptcl", [Block.Int("PrVT", 0), Block.Group("Data", [new("MdNo", [0])]), Block.Group("TC1", [Block.Int("TxNo", 0), new("TLst", [0])]), Block.Group("Col", [Block.Group("RGB", rgb), EffectCompiler.Curve("A", 1)])]),
        new("Tex", System.Text.Encoding.UTF8.GetBytes("vfx/test.atex\0")), Block.Group("Modl", [])
    };
    foreach (var (group, count) in EffectCompiler.Groups) root.Add(Block.Int(count, root.Count(x => x.Tag == group)));
    return Blocks.File(root);
}
sealed class MemoryResources : IResources
{
    public Dictionary<string, byte[]> Files { get; } = [];
    public byte[] Read(string path) => Files.TryGetValue(path, out var bytes) ? bytes : throw new FileNotFoundException(path);
}
