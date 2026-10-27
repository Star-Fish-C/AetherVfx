using AetherVfx.Core;
using AetherVfx.GameData;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using System.Numerics;
using System.Text.Json;

namespace AetherVfx;

public sealed partial class Plugin
{
    private readonly ResourceCatalog resourceCatalog = new();
    private readonly CancellationTokenSource browserCancellation = new();
    private readonly object browserLifetime = new();
    private GameResources resources = null!;
    private WindowPreviewAdapter windowPreview = null!;
    private PreviewCoordinator coordinator = null!;
    private SqpackArchiveSource? archive;
    private ResourceIndexService? resourceIndex;
    private AssociationIndexer? associationIndexer;
    private Task? browserInitialization, associationTask;
    private BrowserLayout layout = new();
    private string layoutPath = "", selectedAsset = "", sourceFilter = "", sourceName = "全部来源", aliasInput = "", userTagInput = "";
    private string browserStatus = "正在初始化资源目录";
    private AssociationKind userTagKind = AssociationKind.User;
    private bool exportOpen, settingsOpen, indexOpen;
    private long libraryRevision, queriedLibrary = -1, queriedCatalog = -1;
    private string lastQuery = "", lastCategory = "", lastFilter = "";
    private DateTime nextRefresh;
    private EffectRecord[] rows = [], snapshot = [];
    private (string Id, string Name, int Count)[] facets = [];
    private EffectRecord? Asset => resourceCatalog.Find(selectedAsset);

    private void InitializeBrowser(IDataManager data, string config)
    {
        layoutPath = Path.Combine(config, "layout.json");
        Run(() => { if (File.Exists(layoutPath)) layout = JsonSerializer.Deserialize<BrowserLayout>(File.ReadAllText(layoutPath), ProjectStore.Json) ?? new(); });
        // This development milestone always opens on the independent surface; never auto-play on load.
        layout.Mode = PreviewMode.Window;
        string sqpack = data.GameData.DataPath.FullName;
        string snapshots = PluginAssetPaths.AssetsDirectory(pi.AssemblyLocation);
        browserInitialization = Task.Run(async () =>
        {
            SqpackArchiveSource? created = null;
            try
            {
                created = new(sqpack);
                browserCancellation.Token.ThrowIfCancellationRequested();
                string cachePath = Path.Combine(config, "resource-index.json");
                string bundledCache = Path.Combine(snapshots, "catalog-cache.json.gz");
                if (!File.Exists(cachePath) && File.Exists(bundledCache))
                {
                    using var input = File.OpenRead(bundledCache);
                    using var gzip = new System.IO.Compression.GZipStream(input, System.IO.Compression.CompressionMode.Decompress);
                    using var bytes = new MemoryStream(); gzip.CopyTo(bytes); AtomicFile.Write(cachePath, bytes.ToArray());
                }
                var index = new ResourceIndexService(created, resourceCatalog, cachePath);
                try { index.LoadCache(); } catch (Exception ex) { log.Warning(ex, "目录缓存无法读取，将重新扫描"); }
                lock (browserLifetime)
                {
                    browserCancellation.Token.ThrowIfCancellationRequested();
                    archive = created; resourceIndex = index;
                    associationIndexer = new(created, resourceCatalog, MonsterSnapshots.Select(config, snapshots));
                    index.Start();
                }
                browserStatus = "资源目录已就绪；归档扫描与来源关联均在后台执行";
                associationTask = associationIndexer.BuildAsync(browserCancellation.Token);
                await associationTask;
                index.SaveCheckpoint();
            }
            catch (OperationCanceledException) { if (archive is null) created?.Dispose(); }
            catch (Exception ex) { browserStatus = "来源初始化诊断：" + ex.Message; log.Error(ex, "浏览器来源初始化失败"); if (archive is null) created?.Dispose(); }
        });
    }
    private void SaveLayout() => AtomicFile.Write(layoutPath, JsonSerializer.SerializeToUtf8Bytes(layout, ProjectStore.Json));
    private void DisposeBrowser()
    {
        lock (browserLifetime) { browserCancellation.Cancel(); resourceIndex?.Dispose(); }
        Run(SaveLayout);
        var pending = new[] { browserInitialization ?? Task.CompletedTask, resourceIndex?.Completion ?? Task.CompletedTask, associationTask ?? Task.CompletedTask };
        _ = Task.WhenAll(pending).ContinueWith(_ => { archive?.Dispose(); browserCancellation.Dispose(); }, TaskScheduler.Default);
    }
    private void DrawBrowserContent()
    {
        DrawMenu();
        if (!NativePreviewSafety.Enabled) ImGui.TextWrapped(NativePreviewSafety.Message);
        foreach (var tab in new[] { "全部", "地区", "怪物", "副本", "技能", "状态", "装备", "收藏", "最近", "未分类" })
        {
            if (tab != "全部") ImGui.SameLine();
            bool isSelected = category == tab;
            if (isSelected) ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(.2f, .4f, .55f, 1));
            if (ImGui.Button(tab)) { category = tab; sourceFilter = ""; sourceName = "全部来源"; }
            if (isSelected) ImGui.PopStyleColor();
        }
        ImGui.SetNextItemWidth(Math.Max(180, ImGui.GetContentRegionAvail().X - 360));
        ImGui.InputTextWithHint("##联合搜索", "搜索名称、别名、路径、地区、怪物或副本…", ref search, 256);
        ImGui.SameLine();
        if (ImGui.BeginCombo("##预览方式", layout.Mode == PreviewMode.Window ? "独立窗口（兼容检查未通过）" : "自身预览（已停用）"))
        {
            foreach (var mode in Enum.GetValues<PreviewMode>())
                if (ImGui.Selectable(mode == PreviewMode.Self ? "游戏画面中的自身（已停用）" : "窗口内角色与特效（待验收）", layout.Mode == mode))
                    Run(() => { StopPreview(); layout.Mode = mode; SaveLayout(); });
            ImGui.EndCombo();
        }
        if (ImGui.Button("打开独立预览面板")) Run(OpenIndependentPreview);
        ImGui.SameLine(); ImGui.TextDisabled("角色与特效同屏 · 原生功能待验证");
        RefreshRows();
        float height = Math.Max(280, ImGui.GetContentRegionAvail().Y - 112);
        float totalWidth = ImGui.GetContentRegionAvail().X;
        float tree = layout.ShowTree ? Math.Clamp(layout.TreeWidth, 140, Math.Max(140, totalWidth - 660)) : 0;
        float list = layout.ShowList ? Math.Clamp(layout.ListWidth, 230, Math.Max(230, totalWidth - tree - 330)) : 0;
        if (layout.ShowTree)
        {
            ImGui.BeginChild("分类树", new(tree, height), true); DrawFacets(); ImGui.EndChild(); ImGui.SameLine(0, 0);
            Splitter("分类分隔", true, height, ref layout.TreeWidth, 140, totalWidth * .4f); ImGui.SameLine(0, 0);
        }
        if (layout.ShowList)
        {
            ImGui.BeginChild("素材列表", new(list, height), true); DrawResourceRows(); ImGui.EndChild(); ImGui.SameLine(0, 0);
            Splitter("列表分隔", true, height, ref layout.ListWidth, 230, totalWidth * .6f); ImGui.SameLine(0, 0);
        }
        ImGui.BeginChild("常驻工作台", new(0, height), false);
        if (layout.ShowPreview && !layout.PreviewPopout)
        {
            float previewHeight = layout.ShowEditor ? Math.Clamp(layout.PreviewHeight, 140, Math.Max(140, height - 200)) : height;
            ImGui.BeginChild("内嵌预览", new(0, previewHeight), true); DrawPreviewPanel(); ImGui.EndChild();
            if (layout.ShowEditor) Splitter("预览分隔", false, ImGui.GetContentRegionAvail().X, ref layout.PreviewHeight, 140, Math.Max(140, height - 200));
        }
        if (layout.ShowEditor) DrawLayers();
        ImGui.EndChild();
        DrawIndexStatus();
        if (message.Length > 0) ImGui.TextWrapped(message);
        foreach (var d in diagnostics.Take(2)) ImGui.TextWrapped($"{d.LayerName}：{d.Message}");
    }
    private static void Splitter(string id, bool vertical, float length, ref float value, float min, float max)
    {
        ImGui.InvisibleButton(id, vertical ? new(6, length) : new(length, 6));
        var a = ImGui.GetItemRectMin(); var b = ImGui.GetItemRectMax();
        ImGui.GetWindowDrawList().AddRectFilled(a, b, ImGui.IsItemHovered() || ImGui.IsItemActive() ? 0xFF897557 : 0xFF393532);
        if (ImGui.IsItemActive()) value = Math.Clamp(value + (vertical ? ImGui.GetIO().MouseDelta.X : ImGui.GetIO().MouseDelta.Y), min, max);
    }
    private void DrawMenu()
    {
        if (!ImGui.BeginMenuBar()) return;
        if (ImGui.BeginMenu("工程"))
        {
            if (ImGui.MenuItem("保存工程")) Run(() => ProjectStore.Save(projectPath, project));
            if (ImGui.MenuItem("读取工程")) Run(() => { coordinator.Stop(); Change(() => { project = ProjectStore.Load(projectPath); selected = project.Layers.FirstOrDefault()?.Id; }); });
            if (ImGui.MenuItem("装备绑定与 PMP 导出")) { exportOpen = true; if (!indexed) Run(() => { catalog.Build(); indexed = true; }); }
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("编辑"))
        {
            if (ImGui.MenuItem("撤销", "", false, history.CanUndo)) { project = history.Undo(project); Dirty(); }
            if (ImGui.MenuItem("重做", "", false, history.CanRedo)) { project = history.Redo(project); Dirty(); }
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("预览"))
        {
            if (ImGui.MenuItem("打开独立预览面板")) Run(OpenIndependentPreview);
            if (ImGui.MenuItem("播放选中素材", "", false, Asset is not null)) Run(PlayAsset);
            if (ImGui.MenuItem("播放当前组合")) Run(StartPreview);
            if (ImGui.MenuItem("停止并恢复")) Run(StopPreview);
            ImGui.BeginDisabled(!coordinator.Active);
            ImGui.MenuItem("调整组合时自动更新", "", ref autoPreview);
            ImGui.EndDisabled();
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("索引"))
        {
            if (ImGui.MenuItem("开始／继续归档扫描", "", false, resourceIndex is not null)) resourceIndex!.Start();
            if (ImGui.MenuItem("暂停归档扫描")) resourceIndex?.Pause();
            if (ImGui.MenuItem("取消扫描并保留检查点")) resourceIndex?.Cancel();
            if (ImGui.MenuItem("重建归档索引", "", false, resourceIndex?.Completion?.IsCompleted != false)) Run(() => resourceIndex?.Start(true));
            if (ImGui.MenuItem("查看索引诊断")) indexOpen = true;
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu("窗口"))
        {
            ImGui.MenuItem("分类树", "", ref layout.ShowTree);
            ImGui.MenuItem("素材列表", "", ref layout.ShowList);
            if (ImGui.MenuItem("预览面板", "", ref layout.ShowPreview) && !layout.ShowPreview && layout.Mode == PreviewMode.Window) Run(StopPreview);
            ImGui.MenuItem("组合编辑区", "", ref layout.ShowEditor);
            ImGui.MenuItem("预览弹出窗口", "", ref layout.PreviewPopout);
            if (ImGui.MenuItem("保存布局")) Run(SaveLayout);
            if (ImGui.MenuItem("重置布局")) { layout = new(); Run(SaveLayout); }
            ImGui.EndMenu();
        }
        if (ImGui.MenuItem("设置")) settingsOpen = true;
        ImGui.EndMenuBar();
    }
    private AssociationKind? FacetKind => category switch
    {
        "地区" => AssociationKind.Region, "怪物" => AssociationKind.Monster, "副本" => AssociationKind.Dungeon,
        "技能" => AssociationKind.Skill, "状态" => AssociationKind.Status, "装备" => AssociationKind.Equipment, _ => null
    };
    private void RefreshRows()
    {
        long revision = resourceCatalog.Revision;
        bool queryChanged = search != lastQuery || category != lastCategory || sourceFilter != lastFilter || libraryRevision != queriedLibrary;
        if (!queryChanged && (revision == queriedCatalog || DateTime.UtcNow < nextRefresh)) return;
        snapshot = resourceCatalog.Snapshot();
        foreach (var record in snapshot)
        {
            var tags = library.UserAssociations.GetValueOrDefault(record.Id, []).Concat(library.UserTags.GetValueOrDefault(record.Id, []).Select(tag => new SourceAssociation(AssociationKind.User, tag, tag, AssociationEvidence.UserTag, "user"))).Except(record.Associations).ToArray();
            if (tags.Length > 0) resourceCatalog.Upsert(record with { Associations = tags });
        }
        if (resourceCatalog.Revision != revision) { snapshot = resourceCatalog.Snapshot(); revision = resourceCatalog.Revision; }
        var kind = FacetKind;
        facets = kind is null ? [] : snapshot.SelectMany(r => r.Associations.Where(a => a.Kind == kind).DistinctBy(a => a.Id)).GroupBy(a => a.Id)
            .Select(g => (g.Key, g.First().Name, g.Count())).OrderBy(x => x.Name, StringComparer.CurrentCulture).ToArray();
        IEnumerable<EffectRecord> found = snapshot;
        if (kind is not null) found = found.Where(r => r.Associations.Any(a => a.Kind == kind && (sourceFilter.Length == 0 || a.Id == sourceFilter)));
        if (category == "未分类") found = found.Where(r => r.Associations.Length == 0);
        if (category == "收藏") found = found.Where(IsFavorite);
        if (category == "最近") found = found.Where(r => library.Recent.Contains(r.Id) || r.References.Any(v => library.Recent.Contains(v.Key)))
            .OrderBy(r => { int i = library.Recent.IndexOf(r.Id); return i < 0 ? int.MaxValue : i; });
        else found = found.OrderBy(r => DisplayName(r), StringComparer.CurrentCulture);
        rows = found.Where(r => ResourceCatalog.Matches(r, search, DisplayName(r))).ToArray();
        queriedCatalog = revision; queriedLibrary = libraryRevision; lastQuery = search; lastCategory = category; lastFilter = sourceFilter; nextRefresh = DateTime.UtcNow.AddMilliseconds(500);
    }
    private bool IsFavorite(EffectRecord r) => library.Favorites.Contains(r.Id) || r.References.Any(x => library.Favorites.Contains(x.Key));
    private string DisplayName(EffectRecord r) => library.Labels.GetValueOrDefault(r.Id, r.References.Select(x => library.Labels.GetValueOrDefault(x.Key)).FirstOrDefault(x => x is not null) ?? r.DisplayName);
    private unsafe void DrawFacets()
    {
        ImGui.TextUnformatted(category);
        if (ImGui.Selectable("全部来源", sourceFilter.Length == 0)) { sourceFilter = ""; sourceName = "全部来源"; }
        var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper());
        try
        {
            clipper.Begin(facets.Length);
            while (clipper.Step()) for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                var f = facets[i];
                if (ImGui.Selectable($"{f.Name} ({f.Count})##{f.Id}", sourceFilter == f.Id)) { sourceFilter = f.Id; sourceName = f.Name; }
            }
        }
        finally { clipper.Destroy(); }
        if (FacetKind is null) ImGui.TextWrapped("按顶部栏目浏览；无法确认归属的特效仍在“全部”和“未分类”中。");
    }
    private unsafe void DrawResourceRows()
    {
        ImGui.TextUnformatted($"{sourceName} · {rows.Length:N0} 项");
        if (!ImGui.BeginTable("资源目录", 3, ImGuiTableFlags.Resizable | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV)) return;
        ImGui.TableSetupColumn("名称", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("来源", ImGuiTableColumnFlags.WidthFixed, 70);
        ImGui.TableSetupColumn("预览", ImGuiTableColumnFlags.WidthFixed, 55);
        ImGui.TableHeadersRow();
        var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper());
        try
        {
            clipper.Begin(rows.Length);
            while (clipper.Step()) for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                var record = rows[i]; ImGui.PushID(record.Id);
                ImGui.TableNextRow(); ImGui.TableNextColumn();
                if (ImGui.SmallButton(IsFavorite(record) ? "★" : "☆"))
                {
                    if (IsFavorite(record)) { library.Favorites.Remove(record.Id); foreach (var reference in record.References) library.Favorites.Remove(reference.Key); }
                    else library.Favorites.Add(record.Id);
                    Run(SaveLibrary);
                }
                ImGui.SameLine();
                if (ImGui.Selectable(DisplayName(record), selectedAsset == record.Id, ImGuiSelectableFlags.AllowDoubleClick))
                {
                    selectedAsset = record.Id; aliasInput = DisplayName(record);
                    if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) AddEffect(record);
                    else Run(PlayAsset);
                }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(record.Preferred.Key + "\n" + string.Join("、", record.Associations.Select(x => x.Name).Distinct().Take(6)) + "\n预览：" + CapabilityName(record.Preview) + "；编辑：" + CapabilityName(record.Editing));
                ImGui.TableNextColumn(); ImGui.TextUnformatted(string.Join("／", record.Associations.Select(x => KindName(x.Kind)).Distinct().Take(2)) is { Length: > 0 } kinds ? kinds : "未分类");
                ImGui.TableNextColumn(); ImGui.TextUnformatted(record.Preview == ResourceCapability.Unavailable ? "不可用" : record.Preview == ResourceCapability.Available ? "已确认" : "待验收");
                ImGui.PopID();
            }
        }
        finally { clipper.Destroy(); ImGui.EndTable(); }
    }
    private void AddEffect(EffectRecord record)
    {
        Change(() =>
        {
            var reference = record.Preferred;
            var layer = new Layer { Name = DisplayName(record), Resource = reference, Source = reference.Kind == ResourceKind.GamePath ? reference.Path : "归档 " + record.Id[..12] };
            project.Layers.Add(layer); selected = layer.Id;
        });
        message = "已加入组合：" + DisplayName(record);
    }
    private void PlayAsset()
    {
        if (!NativePreviewSafety.Enabled) { message = NativePreviewSafety.Message; return; }
        if (layout.Mode == PreviewMode.Window && !windowPreview.Access.CanStart) { message = windowPreview.Access.Diagnostic; return; }
        var record = Asset ?? throw new InvalidOperationException("先选择一个特效");
        autoPreview = false; coordinator.Stop();
        try
        {
            var payload = PreviewPayload.Asset(record.Id, DisplayName(record), record.Preferred, resources);
            coordinator.Play(layout.Mode, PreviewScope.Asset, payload);
            resourceCatalog.SetCapability(record.Id, ResourceCapability.PendingGameValidation, "已提交原生预览，画面与上下文仍需实际确认");
            library.Recent.Remove(record.Id); library.Recent.Insert(0, record.Id);
            if (library.Recent.Count > 100) library.Recent.RemoveRange(100, library.Recent.Count - 100); SaveLibrary();
        }
        catch (Exception ex)
        { resourceCatalog.SetCapability(record.Id, ResourceCapability.Unavailable, ex.Message); throw; }
    }
    private void DrawPreviewPanel()
    {
        ImGui.TextUnformatted(coordinator.Scope == PreviewScope.Composition ? "当前：组合 · " + project.Name : "当前：素材预览");
        bool canPlay = NativePreviewSafety.Enabled && (layout.Mode != PreviewMode.Window || windowPreview.Access.CanStart);
        ImGui.BeginDisabled(!canPlay);
        if (ImGui.Button("播放素材")) Run(PlayAsset);
        ImGui.SameLine(); if (ImGui.Button("组合预览")) Run(StartPreview);
        ImGui.EndDisabled();
        ImGui.SameLine(); if (ImGui.Button("停止")) Run(StopPreview);
        ImGui.SameLine(); ImGui.BeginDisabled(Asset is null);
        if (ImGui.Button("加入组合")) AddEffect(Asset!); ImGui.EndDisabled();
        if (layout.Mode == PreviewMode.Window)
        {
            bool experiment = windowPreview.Access.ExperimentEnabled;
            if (ImGui.Checkbox("本次会话启用实验预览（仍须通过兼容检查）", ref experiment))
                Run(() => { StopPreview(); windowPreview.SetExperimentEnabled(experiment); });
            var state = windowPreview.Snapshot;
            ImGui.TextWrapped($"状态：{state.State} · 视图就绪：{state.ViewReady} · VFX 已创建：{state.VfxCreated} · 视觉验收：{state.VisualAcceptance}");
            ImGui.TextWrapped(windowPreview.Diagnostic);
            windowPreview.Draw(new(ImGui.GetContentRegionAvail().X, Math.Max(60, ImGui.GetContentRegionAvail().Y - 70)));
        }
        else
        {
            ImGui.BeginDisabled(!NativePreviewSafety.Enabled);
            if (ImGui.Button("测试自身光环（原始游戏路径）")) Run(() =>
            {
                const string path = "vfx/common/eff/dk10ht_wra0c.avfx";
                autoPreview = false;
                var effect = RawEffectLoader.Load(resources, ResourceReference.GamePath(path));
                coordinator.Play(PreviewMode.Self, PreviewScope.Asset, new("reference-aura", "自身光环对照", effect, path));
            });
            ImGui.EndDisabled();
            ImGui.TextWrapped("使用素材自身的角色挂点；原生返回句柄不作为图形对象读取或写入。" + preview.Status);
            ImGui.TextWrapped(preview.Diagnostic);
        }
        if (ImGui.SmallButton("复制播放诊断")) ImGui.SetClipboardText((layout.Mode == PreviewMode.Window ? windowPreview.Status + "\n" + windowPreview.Diagnostic : preview.Status + "\n" + preview.Diagnostic) + "\n素材：" + selectedAsset);
        if (Asset is { } record)
        {
            if (ImGui.SmallButton("复制资源信息")) ImGui.SetClipboardText(string.Join('\n', record.References.Select(x => x.Key)));
            ImGui.SameLine(); if (ImGui.SmallButton("资源详情／重命名")) settingsOpen = true;
            ImGui.TextWrapped(DisplayName(record));
        }
    }
    private void OpenIndependentPreview()
    {
        StopPreview();
        layout.Mode = PreviewMode.Window; layout.ShowPreview = true; layout.PreviewPopout = true;
        SaveLayout();
    }
    private void DrawIndexStatus()
    {
        var p = resourceIndex?.Progress;
        if (p is not null)
        {
            float ratio = p.Total == 0 ? 0 : (float)p.Scanned / p.Total;
            ImGui.ProgressBar(ratio, new(-1, 16), $"{p.State} · {p.Scanned:N0}/{p.Total:N0} · 目录 {resourceCatalog.Count:N0} · 失败 {p.Failed:N0}");
            ImGui.TextUnformatted(p.FullCoverage ? "归档覆盖完整" : "归档覆盖尚未完整验证");
        }
        else ImGui.TextUnformatted(browserStatus);
        ImGui.TextWrapped(associationIndexer?.Status ?? browserStatus);
    }
    private void DrawBrowserWindows()
    {
        if (exportOpen)
        {
            ImGui.SetNextWindowSize(new(760, 700), ImGuiCond.FirstUseEver);
            if (ImGui.Begin("装备绑定与 PMP 导出", ref exportOpen)) Run(DrawExport); ImGui.End();
        }
        if (layout.PreviewPopout && layout.ShowPreview)
        {
            ImGui.SetNextWindowSize(new(700, 600), ImGuiCond.FirstUseEver);
            bool visible = true;
            if (ImGui.Begin("独立预览###AetherVfxPreview", ref visible)) Run(DrawPreviewPanel); ImGui.End();
            if (!visible) { layout.ShowPreview = false; Run(StopPreview); }
        }
        if (settingsOpen)
        {
            ImGui.SetNextWindowSize(new(650, 520), ImGuiCond.FirstUseEver);
            if (ImGui.Begin("工作台设置与资源信息", ref settingsOpen)) Run(DrawSettings); ImGui.End();
        }
        if (indexOpen)
        {
            if (ImGui.Begin("索引诊断", ref indexOpen))
            {
                DrawIndexStatus(); ImGui.TextWrapped("包指纹：" + archive?.Fingerprint);
                foreach (var failure in resourceIndex?.Failures ?? []) ImGui.TextWrapped(failure);
            }
            ImGui.End();
        }
    }
    private void DrawSettings()
    {
        ImGui.InputText("工程路径", ref projectPath, 2048);
        if (ImGui.Button("更新怪物基础映射（联网）")) UpdateSnapshots();
        ImGui.TextWrapped("基础映射按上游提交保存，关联结果仍会校验本机文件；更新不会自动改动工程。");
        ImGui.InputText("直接输入 AVFX 路径", ref manualPath, 1024);
        if (ImGui.Button("收录路径")) Run(() =>
        {
            var bytes = resources.Read(manualPath); _ = Blocks.Root(bytes); string id = ResourceCatalog.Hash(bytes);
            resourceCatalog.Upsert(new() { Id = id, Name = Path.GetFileNameWithoutExtension(manualPath), References = [ResourceReference.GamePath(manualPath)] }); selectedAsset = id;
        });
        if (Asset is not { } record) return;
        ImGui.Separator(); ImGui.TextWrapped(record.Id);
        foreach (var reference in record.References) ImGui.TextWrapped(reference.Key);
        ImGui.InputText("显示名称", ref aliasInput, 256);
        if (ImGui.Button("保存名称")) { if (string.IsNullOrWhiteSpace(aliasInput)) library.Labels.Remove(record.Id); else library.Labels[record.Id] = aliasInput.Trim(); SaveLibrary(); }
        ImGui.InputText("自定义分类标签", ref userTagInput, 256);
        if (ImGui.BeginCombo("标签分类", userTagKind.ToString()))
        {
            foreach (var kind in new[] { AssociationKind.User, AssociationKind.Region, AssociationKind.Monster, AssociationKind.Dungeon })
                if (ImGui.Selectable(kind.ToString(), userTagKind == kind)) userTagKind = kind;
            ImGui.EndCombo();
        }
        if (ImGui.Button("添加用户标签") && !string.IsNullOrWhiteSpace(userTagInput))
        {
            var tag = new SourceAssociation(userTagKind, "user:" + userTagInput, userTagInput, AssociationEvidence.UserTag, "user");
            resourceCatalog.Upsert(record with { Associations = [tag] });
            library.UserAssociations[record.Id] = library.UserAssociations.GetValueOrDefault(record.Id, []).Append(tag).Distinct().ToArray(); SaveLibrary();
        }
        if (ImGui.Button("检查编辑支持")) Run(() =>
        {
            var result = compiler.Compile(new() { Layers = [new() { Name = DisplayName(record), Source = record.Preferred.Path, Resource = record.Preferred }] });
            resourceCatalog.SetEditingCapability(record.Id, result.Success, string.Join("；", result.Diagnostics.Select(x => x.Message)));
        });
        ImGui.TextWrapped("预览能力：" + CapabilityName(record.Preview) + "；" + record.Diagnostic);
        ImGui.TextWrapped("编辑能力：" + CapabilityName(record.Editing) + "；" + record.EditingDiagnostic);
        foreach (var a in record.Associations) ImGui.TextWrapped($"{a.Kind} · {a.Name} · {a.Evidence}\n{a.EvidencePath}");
    }
    private static string CapabilityName(ResourceCapability capability) => capability switch
    {
        ResourceCapability.Available => "检查通过", ResourceCapability.Unavailable => "不可用，见诊断",
        ResourceCapability.PendingGameValidation => "待游戏画面验收", _ => "尚未检查"
    };
    private static string KindName(AssociationKind kind) => kind switch
    {
        AssociationKind.Region => "地区", AssociationKind.Monster => "怪物", AssociationKind.Dungeon => "副本", AssociationKind.Skill => "技能",
        AssociationKind.Status => "状态", AssociationKind.Equipment => "装备", _ => "用户"
    };
    private void UpdateSnapshots()
    {
        if (associationTask is { IsCompleted: false }) { message = "来源索引正在运行，请完成或取消后更新映射"; return; }
        if (archive is null) { message = "资源服务尚未初始化"; return; }
        associationTask = Task.Run(async () =>
        {
            try
            {
                browserStatus = "正在下载带提交版本的怪物映射";
                string folder = await MonsterSnapshots.UpdateAsync(pi.GetPluginConfigDirectory(), browserCancellation.Token);
                associationIndexer = new(archive, resourceCatalog, folder);
                await associationIndexer.BuildAsync(browserCancellation.Token);
                resourceIndex?.SaveCheckpoint(); browserStatus = "怪物映射及来源索引已更新";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { browserStatus = "映射更新失败，原快照保留：" + ex.Message; log.Error(ex, "映射更新失败"); }
        });
    }
}

internal sealed class BrowserLayout
{
    public bool ShowTree = true, ShowList = true, ShowPreview = true, ShowEditor = true, PreviewPopout;
    public float TreeWidth = 210, ListWidth = 370, PreviewHeight = 330;
    public PreviewMode Mode = PreviewMode.Window;
}
