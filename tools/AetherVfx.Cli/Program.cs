using AetherVfx.Core;
using Lumina;
using System.Text.Json;

if (args.Length < 2)
{
    Console.WriteLine("inspect <file.avfx> | verify <file.pmp> | compile <project.json> <sqpack-directory> <output.pmp> | probe <sqpack-directory> <path.avfx> | demo <sqpack-directory> <output-directory>");
    return 1;
}
try
{
    if (args[0] == "catalog-check" && args.Length == 4)
    {
        using var source = new AetherVfx.GameData.SqpackArchiveSource(args[1]);
        var cache = JsonSerializer.Deserialize<IndexCache>(File.ReadAllText(args[2]), ProjectStore.Json)!;
        var entries = source.Enumerate(); source.ValidateVersion();
        if (cache.Fingerprint != source.Fingerprint || cache.Cursor != entries.Count || cache.Failed != 0) throw new InvalidDataException("目录不是当前版本的完整无失败扫描");
        Directory.CreateDirectory(args[3]);
        var chosen = cache.Records.Where(r => r.References.Any(x => x.Kind == ResourceKind.Archive)).Where((_, i) => i % Math.Max(1, cache.Records.Length / 100) == 0).Take(100).ToArray();
        foreach (var record in chosen)
        {
            var reference = record.References.First(x => x.Kind == ResourceKind.Archive);
            var bytes = source.Read(reference); _ = Blocks.Root(bytes);
            if (ResourceCatalog.Hash(bytes) != record.Id) throw new InvalidDataException("目录摘要错误");
        }
        var resources = new ArchiveReadResources(source);
        var target = ProjectStore.Load(Path.Combine("artifacts", "samples", "Equipment.aethervfx.json")).Target;
        string? exported = null;
        foreach (var record in cache.Records.Where(r => r.References.All(x => x.Kind == ResourceKind.Archive)).Take(300))
        {
            var project = new WorkProject { Name = "无路径归档资源验证", Target = target, Layers = [new() { Name = record.DisplayName, Source = "无路径资源", Resource = record.Preferred }] };
            var result = new EffectCompiler(resources).Compile(project);
            if (!result.Success) continue;
            PmpExporter.Export(Path.Combine(args[3], "archive.pmp"), project, new EquipmentBinder(resources).Bind(result.Effect!, target));
            ProjectStore.Save(Path.Combine(args[3], "archive.aethervfx.json"), project); exported = record.Id; break;
        }
        if (exported is null) throw new InvalidDataException("未找到可导出的无路径测试样本");
        var report = new
        {
            cache.Fingerprint, PhysicalEntries = entries.Count, UniqueAvfx = cache.Records.Length, cache.Failed,
            ClassifiedEffects = cache.Records.Count(r => r.Associations.Length > 0), UnclassifiedEffects = cache.Records.Count(r => r.Associations.Length == 0),
            ArchiveReadsVerified = chosen.Length, UnnamedPmpResource = exported,
            Associations = cache.Records.SelectMany(r => r.Associations.Select(a => (r.Id, a.Kind))).Distinct().GroupBy(x => x.Kind).ToDictionary(g => g.Key.ToString(), g => g.Count()),
            NativeRendering = "PendingGameValidation"
        };
        string json = JsonSerializer.Serialize(report, ProjectStore.Json); AtomicFile.Write(Path.Combine(args[3], "catalog-report.json"), System.Text.Encoding.UTF8.GetBytes(json)); Console.WriteLine(json);
    }
    else if (args[0] == "classify" && args.Length >= 4)
    {
        using var source = new AetherVfx.GameData.SqpackArchiveSource(args[1]);
        var catalog = new ResourceCatalog(); IndexCache? old = null;
        if (args.Length > 4 && File.Exists(args[4]))
        {
            old = JsonSerializer.Deserialize<IndexCache>(File.ReadAllText(args[4]), ProjectStore.Json);
            if (old?.Fingerprint != source.Fingerprint) throw new InvalidDataException("索引版本不一致");
            foreach (var record in old.Records) catalog.Upsert(record);
        }
        var associations = new AetherVfx.GameData.AssociationIndexer(source, catalog, args[2]);
        var job = associations.BuildAsync(CancellationToken.None);
        while (!job.IsCompleted) { await Task.Delay(2000); Console.WriteLine(associations.Status + " · " + catalog.Count); }
        await job;
        var cache = new IndexCache { Fingerprint = source.Fingerprint, Cursor = old?.Cursor ?? 0, Failed = old?.Failed ?? 0, Failures = old?.Failures ?? [], Records = catalog.Snapshot() };
        AtomicFile.Write(args[3], JsonSerializer.SerializeToUtf8Bytes(cache, IndexCache.Json));
        Console.WriteLine(JsonSerializer.Serialize(cache.Records.SelectMany(r => r.Associations.Select(a => (r.Id, a.Kind))).Distinct().GroupBy(x => x.Kind).ToDictionary(g => g.Key.ToString(), g => g.Count()), ProjectStore.Json));
    }
    else if (args[0] == "merge-index" && args.Length == 4)
    {
        var main = JsonSerializer.Deserialize<IndexCache>(File.ReadAllText(args[1]), ProjectStore.Json)!;
        var classified = JsonSerializer.Deserialize<IndexCache>(File.ReadAllText(args[2]), ProjectStore.Json)!;
        if (main.Fingerprint != classified.Fingerprint) throw new InvalidDataException("不能合并不同资源包版本的索引");
        var catalog = new ResourceCatalog(); foreach (var record in main.Records.Concat(classified.Records)) catalog.Upsert(record);
        AtomicFile.Write(args[3], JsonSerializer.SerializeToUtf8Bytes(main with { Records = catalog.Snapshot() }, IndexCache.Json));
        Console.WriteLine($"Merged: {catalog.Count} effects; cursor {main.Cursor}");
    }
    else if (args[0] == "scan" && args.Length == 3)
    {
        using var source = new AetherVfx.GameData.SqpackArchiveSource(args[1]);
        var catalog = new ResourceCatalog();
        using var index = new ResourceIndexService(source, catalog, args[2]); index.LoadCache(); index.Start();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; index.Cancel(); };
        while (index.Completion is { IsCompleted: false })
        {
            await Task.Delay(2000); var p = index.Progress;
            Console.WriteLine($"{p.State}: {p.Scanned}/{p.Total}, AVFX {p.Found}, failures {p.Failed}");
        }
        await index.Completion!;
        Console.WriteLine(JsonSerializer.Serialize(index.Progress, ProjectStore.Json));
        return index.Progress.FullCoverage ? 0 : 2;
    }
    else if (args[0] == "scan-probe" && args.Length >= 2)
    {
        using var source = new AetherVfx.GameData.SqpackArchiveSource(args[1]);
        var all = source.Enumerate(); Console.WriteLine($"Physical entries: {all.Count}; fingerprint {source.Fingerprint}");
        int limit = args.Length > 2 ? int.Parse(args[2]) : 10000, found = 0, failures = 0;
        foreach (var entry in all.Take(limit))
            try
            {
                if (!source.IsAvfx(entry)) continue;
                var bytes = source.Read(entry.Location); _ = Blocks.Root(bytes); found++;
                if (found <= 5) Console.WriteLine($"AVFX {entry.Location.Key} {bytes.Length} bytes");
            }
            catch (Exception ex) { failures++; if (failures <= 3) Console.WriteLine(ex.Message); }
        Console.WriteLine($"PARTIAL probe {Math.Min(all.Count, limit)}/{all.Count}; AVFX {found}; failures {failures}");
        return failures == 0 ? 0 : 2;
    }
    else if (args[0] == "sdk" && args.Length >= 3)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(args[1]))!;
        System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (ctx, name) => File.Exists(Path.Combine(directory, name.Name + ".dll")) ? ctx.LoadFromAssemblyPath(Path.Combine(directory, name.Name + ".dll")) : null;
        var assembly = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(args[1]));
        foreach (var type in assembly.GetExportedTypes().Where(t => args.Skip(2).Any(s => t.FullName!.EndsWith(s, StringComparison.Ordinal))))
        {
            Console.WriteLine(type.FullName);
            foreach (var member in type.GetMembers(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly))
                if (member.MemberType is System.Reflection.MemberTypes.Field or System.Reflection.MemberTypes.Property or System.Reflection.MemberTypes.Method or System.Reflection.MemberTypes.Constructor) Console.WriteLine("  " + member);
        }
    }
    else if (args[0] == "inspect")
    {
        var root = Blocks.Root(File.ReadAllBytes(args[1])); EffectCompiler.Validate(root);
        Console.WriteLine(JsonSerializer.Serialize(EffectCompiler.Groups.ToDictionary(x => x.Key, x => root.Number(x.Value)), ProjectStore.Json));
    }
    else if (args[0] == "verify")
    {
        using var zip = System.IO.Compression.ZipFile.OpenRead(args[1]);
        using var meta = JsonDocument.Parse(zip.GetEntry("meta.json")!.Open());
        if (meta.RootElement.GetProperty("FileVersion").GetInt32() != 3) throw new InvalidDataException("不是版本 3 PMP");
        using var mod = JsonDocument.Parse(zip.GetEntry("default_mod.json")!.Open());
        var mapping = mod.RootElement.GetProperty("Files").EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetString()!);
        int avfxCount = 0;
        foreach (var (path, local) in mapping)
        {
            EffectCompiler.CheckGamePath(path);
            if (local.Contains("..") || local.StartsWith('/') || local.Contains(':')) throw new InvalidDataException("不安全的打包路径");
            var entry = zip.GetEntry(local) ?? throw new InvalidDataException("缺少映射文件：" + local);
            if (!path.EndsWith(".avfx")) continue;
            using var memory = new MemoryStream(); using (var stream = entry.Open()) stream.CopyTo(memory);
            var root = Blocks.Root(memory.ToArray()); EffectCompiler.Validate(root);
            foreach (var texture in root.Where(b => b.Tag == "Tex"))
            {
                var dependency = System.Text.Encoding.UTF8.GetString(texture.Data).TrimEnd('\0');
                if (dependency.Length > 0 && !mapping.ContainsKey(dependency)) throw new InvalidDataException("未打包贴图：" + dependency);
            }
            avfxCount++;
        }
        if (avfxCount == 0 || mod.RootElement.GetProperty("Manipulations").GetArrayLength() == 0) throw new InvalidDataException("缺少装备特效或元数据");
        Console.WriteLine($"验证通过：{avfxCount} AVFX，{mapping.Count} 文件映射，装备元数据完整 · {Path.GetFileName(args[1])}");
    }
    else if (args[0] == "compile" && args.Length == 4)
    {
        var project = ProjectStore.Load(args[1]);
        using var game = new GameData(args[2]); var resources = new SqpackResources(game);
        var result = new EffectCompiler(resources).Compile(project);
        foreach (var d in result.Diagnostics) Console.WriteLine($"{d.LayerName}：{d.Message}");
        if (!result.Success) return 2;
        var bound = new EquipmentBinder(resources).Bind(result.Effect!, project.Target);
        PmpExporter.Export(args[3], project, bound); Console.WriteLine("导出：" + Path.GetFullPath(args[3]));
    }
    else if (args[0] == "probe" && args.Length == 3)
    {
        using var game = new GameData(args[1], new LuminaOptions { DefaultExcelLanguage = Lumina.Data.Language.ChineseSimplified }); var resources = new SqpackResources(game);
        var result = new EffectCompiler(resources).Compile(new() { Layers = [new() { Source = args[2] }] });
        foreach (var d in result.Diagnostics) Console.WriteLine(d.Message);
        Console.WriteLine(result.Success ? $"可编译，{result.Effect!.Avfx.Length} bytes" : "不能编译");
        return result.Success ? 0 : 2;
    }
    else if (args[0] == "demo" && args.Length == 3)
    {
        using var game = new GameData(args[1], new LuminaOptions { DefaultExcelLanguage = Lumina.Data.Language.ChineseSimplified }); var resources = new SqpackResources(game);
        Directory.CreateDirectory(args[2]);
        foreach (var kind in Enum.GetValues<EquipmentKind>())
        {
            var errors = new List<string>(); bool exported = false;
            foreach (var item in game.Excel.GetSheet<Lumina.Excel.Sheets.Item>())
            {
                if (item.ModelMain == 0 || !item.EquipSlotCategory.IsValid) continue;
                if (item.Name.ExtractText().StartsWith("过期", StringComparison.Ordinal)) continue;
                var c = item.EquipSlotCategory.Value;
                Slot slot;
                if (kind == EquipmentKind.Weapon && c.MainHand == 1) slot = Slot.MainHand;
                else if (kind == EquipmentKind.Equipment && c.Body == 1) slot = Slot.Body;
                else if (kind == EquipmentKind.Accessory && c.Ears == 1) slot = Slot.Ears;
                else continue;
                var model = item.ModelMain;
                var target = new EquipmentTarget
                {
                    Name = item.Name.ExtractText(), ItemId = item.RowId, Kind = kind, Slot = slot,
                    PrimaryId = (ushort)model, SecondaryId = kind == EquipmentKind.Weapon ? (ushort)(model >> 16) : (ushort)0,
                    Variant = (byte)(model >> (kind == EquipmentKind.Weapon ? 32 : 16)), VfxId = 200
                };
                var project = new WorkProject
                {
                    Name = "AetherVfx 三图层验证 · " + kind, Target = target,
                    Layers =
                    [
                        new() { Name = "脚下光环", Source = "vfx/common/eff/dk10ht_wra0c.avfx", Scale = .6f, Position = new(0, -.5f, 0) },
                        new() { Name = "身体光环", Source = "vfx/common/eff/dk10ht_wra0c.avfx", Scale = 1, Color = new(.5f, .8f, 1, .7f) },
                        new() { Name = "附着装饰", Source = "vfx/common/eff/dk10ht_wra0c.avfx", Scale = .3f, Position = new(0, 1, 0), RotationDegrees = new(0, 90, 0) }
                    ]
                };
                var result = new EffectCompiler(resources).Compile(project);
                if (!result.Success) throw new InvalidDataException(string.Join(';', result.Diagnostics.Select(x => x.Message)));
                try
                {
                    var bound = new EquipmentBinder(resources).Bind(result.Effect!, target);
                    var path = Path.Combine(args[2], kind + ".pmp");
                    PmpExporter.Export(path, project, bound); ProjectStore.Save(Path.Combine(args[2], kind + ".aethervfx.json"), project);
                    Console.WriteLine($"{kind}：{target.Name} (Item {target.ItemId}, {target.PrimaryId}/{target.SecondaryId}, variant {target.Variant}) → {Path.GetFullPath(path)}");
                    exported = true; break;
                }
                catch (IOException ex) { errors.Add(ex.Message); }
            }
            if (!exported) throw new InvalidDataException($"{kind} 无有效样本：{string.Join(';', errors.Take(3))}");
        }
    }
    else throw new ArgumentException("参数不正确");
    return 0;
}
catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 2; }

sealed class SqpackResources(GameData game) : IResources, IDisposable
{
    private readonly Lazy<AetherVfx.GameData.SqpackArchiveSource> archive = new(() => new(game.DataPath.FullName));
    public byte[] Read(string path) => game.GetFile(path)?.Data ?? throw new FileNotFoundException("缺少游戏资源：" + path);
    public byte[] Read(ResourceReference reference) => reference.Kind == ResourceKind.Archive ? archive.Value.Read(reference) : Read(reference.Path);
    public void Dispose() { if (archive.IsValueCreated) archive.Value.Dispose(); }
}
sealed class ArchiveReadResources(AetherVfx.GameData.SqpackArchiveSource source) : IResources
{
    public byte[] Read(string path) => source.ReadPath(path);
    public byte[] Read(ResourceReference reference) => source.Read(reference);
}
