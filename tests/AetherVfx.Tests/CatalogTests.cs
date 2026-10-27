using AetherVfx.Core;
using System.Text.Json;

internal static class CatalogTests
{
    public static int Run(byte[] fixture, IResources original)
    {
        int count = 0;
        void Test(string name, Action action) { action(); count++; Console.WriteLine("PASS " + name); }
        void Assert(bool value, string message) { if (!value) throw new Exception(message); }
        var location = new ArchiveLocation("ffxiv", 8, 0, 0, 0, 128);
        var reference = new ResourceReference { Kind = ResourceKind.Archive, Archive = location, PackageVersion = new('a', 64), ContentHash = ResourceCatalog.Hash(fixture) };
        Test("版本 1 工程迁移为版本 2，保留位置及路径", () =>
        {
            var project = ProjectStore.Parse("{\"Version\":1,\"Layers\":[{\"Name\":\"旧图层\",\"Source\":\"vfx/test.avfx\",\"Position\":{\"X\":1,\"Y\":2,\"Z\":3}}]}");
            Assert(project.Version == 2 && project.Layers[0].Resource?.Path == "vfx/test.avfx" && project.Layers[0].Position.Y == 2, "迁移丢失状态");
        });
        Test("归档资源工程往返，并在无路径时参与编译", () =>
        {
            var project = new WorkProject { Layers = [new() { Name = "未命名", Source = "无路径资源", Resource = reference }] };
            project = ProjectStore.Parse(ProjectStore.Serialize(project));
            Assert(project.Layers[0].Resource == reference, "归档标识丢失");
            var result = new EffectCompiler(new ArchiveResources(original, fixture)).Compile(project);
            Assert(result.Success, string.Join(';', result.Diagnostics.Select(x => x.Message)));
        });
        Test("同内容多路径去重，地区与怪物关联可联合搜索", () =>
        {
            var catalog = new ResourceCatalog(); string hash = reference.ContentHash;
            catalog.Upsert(new() { Id = hash, References = [reference] });
            catalog.Upsert(new() { Id = hash, Name = "蓝色法阵", References = [ResourceReference.GamePath("vfx/test.avfx")], Associations = [new(AssociationKind.Region, "1", "黑衣森林", AssociationEvidence.DirectReference, "bg/vfx.lgb")] });
            catalog.Upsert(new() { Id = hash, References = [reference], Associations = [new(AssociationKind.Monster, "2", "水元素", AssociationEvidence.IndirectReference, "snapshot")] });
            var record = catalog.Snapshot().Single();
            Assert(catalog.Count == 1 && record.References.Length == 2 && record.Associations.Length == 2 && record.Preferred.Kind == ResourceKind.GamePath, "目录去重丢失来源");
            Assert(ResourceCatalog.Matches(record, "黑衣 水元素") && !ResourceCatalog.Matches(record, "火元素"), "联合检索错误");
            Assert(ResourceCatalog.Matches(record, "我的别名 黑衣", "我的别名"), "别名不能参与联合搜索");
        });
        Test("完整扫描收录无名称资源，失败条目阻止完整覆盖标记", () =>
        {
            string path = Temp();
            try
            {
                var source = new FakeArchive(fixture, 10, 5); var catalog = new ResourceCatalog();
                using var service = new ResourceIndexService(source, catalog, path);
                service.Start(); service.Completion!.GetAwaiter().GetResult();
                Assert(service.Progress.State == ScanState.Completed && service.Progress.Scanned == 10 && service.Progress.Failed == 1 && !service.Progress.FullCoverage, "不完整扫描被标为完整");
                Assert(catalog.Count == 1 && catalog.Snapshot()[0].References.Length == 4 && catalog.Snapshot()[0].Name.Length == 0, "未命名／重复条目处理错误");
            }
            finally { File.Delete(path); }
        });
        Test("缓存恢复无需重读，版本变化拒绝旧偏移", () =>
        {
            string path = Temp();
            try
            {
                var source = new FakeArchive(fixture, 20); using (var service = new ResourceIndexService(source, new(), path)) { service.Start(); service.Completion!.GetAwaiter().GetResult(); Assert(service.Progress.FullCoverage, "完整扫描未完成"); }
                var resumed = new FakeArchive(fixture, 20); var catalog = new ResourceCatalog();
                using (var service = new ResourceIndexService(resumed, catalog, path)) { Assert(service.LoadCache(), "缓存未载入"); service.Start(); service.Completion!.GetAwaiter().GetResult(); Assert(resumed.Reads == 0 && service.Progress.FullCoverage && catalog.Count == 1, "未正确恢复游标"); }
                using var stale = new ResourceIndexService(new FakeArchive(fixture, 20) { Fingerprint = new('b', 64) }, new(), path);
                Assert(!stale.LoadCache(), "接受了不同资源包版本的缓存");
            }
            finally { File.Delete(path); }
        });
        Test("暂停、取消及继续扫描保留检查点", () =>
        {
            string path = Temp();
            try
            {
                var source = new FakeArchive(fixture, 500) { Delay = 2 }; using var service = new ResourceIndexService(source, new(), path);
                service.Start(); service.Pause(); Thread.Sleep(30); int reads = source.Reads; Thread.Sleep(30);
                Assert(source.Reads <= reads + 1 && service.Progress.State == ScanState.Paused, "暂停仍继续读取");
                service.Cancel(); service.Completion!.GetAwaiter().GetResult(); Assert(service.Progress.State == ScanState.Cancelled, "取消状态错误");
                source.Delay = 0; service.Start(); service.Completion!.GetAwaiter().GetResult(); Assert(service.Progress.FullCoverage, "取消后不能继续");
            }
            finally { File.Delete(path); }
        });
        Test("资源包在扫描中变动，不能声明扫描完成", () =>
        {
            string path = Temp();
            try
            {
                using var service = new ResourceIndexService(new FakeArchive(fixture, 3) { ChangeAtEnd = true }, new(), path);
                service.Start(); service.Completion!.GetAwaiter().GetResult(); Assert(service.Progress.State == ScanState.Faulted && !service.Progress.FullCoverage, "接受了扫描期间的资源变化");
            }
            finally { File.Delete(path); }
        });
        Test("预览连续切换 100 次仅保留一个实例，且不修改工程", () =>
        {
            var self = new FakePreview(); var window = new FakePreview();
            var coordinator = new PreviewCoordinator(new Dictionary<PreviewMode, IPreviewSurface> { [PreviewMode.Self] = self, [PreviewMode.Window] = window });
            var project = new WorkProject(); string before = ProjectStore.Serialize(project);
            var payload = new PreviewPayload("raw", "素材", null!);
            for (int i = 0; i < 100; i++)
            {
                coordinator.Play(i % 2 == 0 ? PreviewMode.Self : PreviewMode.Window, PreviewScope.Asset, payload with { Id = i.ToString() });
                Assert((self.Active ? 1 : 0) + (window.Active ? 1 : 0) == 1, "多个预览同时存活");
            }
            coordinator.Stop(); Assert(!self.Active && !window.Active && ProjectStore.Serialize(project) == before, "停止／工程隔离失败");
        });
        Test("预览启动失败释放已创建实例并保留错误", () =>
        {
            var surface = new FakePreview { Fail = true }; var coordinator = new PreviewCoordinator(new Dictionary<PreviewMode, IPreviewSurface> { [PreviewMode.Self] = surface });
            try { coordinator.Play(PreviewMode.Self, PreviewScope.Asset, new("broken", "素材", null!)); throw new Exception("应抛错"); }
            catch (InvalidOperationException) { Assert(!surface.Active && coordinator.Mode is null, "失败后仍有预览实例"); }
        });
        Test("组合预览忽略导出装备限制，且不修改已选装备与图层", () =>
        {
            var project = new WorkProject
            {
                Layers = [new() { Source = "vfx/test.avfx", Anchor = Anchor.Body }],
                Target = new() { Kind = EquipmentKind.Weapon, PrimaryId = 1 }
            };
            string before = ProjectStore.Serialize(project);
            var compiler = new EffectCompiler(original);
            Assert(compiler.CompileForPreview(project).Success, "预览仍受导出目标约束");
            Assert(!compiler.Compile(project).Success, "导出应继续验证武器挂点限制");
            Assert(ProjectStore.Serialize(project) == before, "预览更改了工程");
        });
        Test("直接预览保留编译产物及依赖，无装备或 IMC 资源", () =>
        {
            var effect = new EffectCompiler(original).CompileForPreview(new() { Layers = [new() { Source = "vfx/test.avfx" }] }).Effect!;
            var mapped = PreviewResources.FromEffect(effect);
            Assert(mapped.VfxPath.StartsWith("vfx/aethervfx/eff/") && mapped.Files[mapped.VfxPath].SequenceEqual(effect.Avfx), "预览未使用编译产物");
            Assert(mapped.Files.Count == effect.Dependencies.Count + 1 && effect.Dependencies.All(x => mapped.Files[x.Key].SequenceEqual(x.Value)), "依赖丢失");
            Assert(!mapped.Files.Keys.Any(x => x.StartsWith("chara/") || x.EndsWith(".imc")), "预览生成装备资源");
            var changed = new EffectCompiler(original).CompileForPreview(new() { Layers = [new() { Source = "vfx/test.avfx", Scale = 2 }] }).Effect!;
            Assert(PreviewResources.FromEffect(changed).VfxPath != mapped.VfxPath, "编辑后仍使用旧缓存路径");
        });
        Test("原生特效自然结束后停止不重复释放，指针可再次跟踪", () =>
        {
            var registry = new NativeVfxRegistry();
            registry.Add(123);
            Assert(registry.Remove(123) && !registry.Contains(123) && !registry.Remove(123), "自然结束后指针仍存活");
            registry.Add(123); registry.Add(456);
            Assert(registry.Snapshot().Length == 2 && registry.Remove(123) && registry.Remove(456) && registry.Snapshot().Length == 0, "新实例跟踪或清理失败");
        });
        return count;
    }
    private static string Temp() => Path.Combine(Path.GetTempPath(), "aethervfx-index-" + Guid.NewGuid() + ".json");
    private sealed class ArchiveResources(IResources original, byte[] bytes) : IResources
    {
        public byte[] Read(string path) => original.Read(path);
        public byte[] Read(ResourceReference reference) => reference.Kind == ResourceKind.Archive ? bytes : original.Read(reference.Path);
    }
    private sealed class FakeArchive(byte[] bytes, int total, int failure = -1) : IArchiveSource
    {
        public string Fingerprint { get; init; } = new('a', 64);
        public int Reads, Delay;
        public bool ChangeAtEnd;
        private int validations;
        public void ValidateVersion() { if (ChangeAtEnd && ++validations == 2) throw new InvalidDataException("版本发生变化"); }
        public IReadOnlyList<ArchiveScanEntry> Enumerate() => Enumerable.Range(0, total).Select(i => new ArchiveScanEntry(new("ffxiv", 8, 0, 0, 0, i), i == failure ? "测试读取失败" : null)).ToArray();
        public bool IsAvfx(ArchiveScanEntry entry) { if (Delay > 0) Thread.Sleep(Delay); return entry.Location.Offset % 3 == 0; }
        public byte[] Read(ArchiveLocation location) { Interlocked.Increment(ref Reads); return bytes; }
    }
    private sealed class FakePreview : IPreviewSurface
    {
        public bool Active { get; private set; }
        public PreviewStatus Snapshot => new(Active ? PreviewState.Playing : PreviewState.Idle, "模拟表面");
        public bool Fail;
        public void Play(PreviewPayload payload) { Active = true; if (Fail) throw new InvalidOperationException("模拟启动失败"); }
        public void Stop() => Active = false;
    }
}
