using AetherVfx.Core;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;
using System.Numerics;
using System.Text.Json;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace AetherVfx;

public sealed partial class Plugin : IDalamudPlugin
{
    private readonly IDalamudPluginInterface pi;
    private readonly ICommandManager commands;
    private readonly IFramework framework;
    private readonly IObjectTable objects;
    private readonly IGameGui gameGui;
    private readonly IPluginLog log;
    private readonly GameCatalog catalog;
    private readonly EffectCompiler compiler;
    private readonly EquipmentBinder binder;
    private readonly PreviewManager preview;
    private readonly NativeVfxEngine nativeVfx;
    private readonly ProjectHistory history = new();
    private WorkProject project = new();
    private LibraryState library = new();
    private bool open = true, indexed, autoPreview, disposed;
    private Guid? selected, solo;
    private DateTime dirtyAt = DateTime.MaxValue;
    private string search = "", itemSearch = "", manualPath = "", message = "", projectPath, exportPath;
    private string category = "全部";
    private int wizard;
    private BoundEffect? bound;
    private List<Diagnostic> diagnostics = [];
    private string? editBefore;
    private string? dragBefore;
    private int dragAxis = -1, gizmoMode;
    private Vector2 dragStart;
    private Vector3 dragPosition, dragRotation;
    private float dragScale;
    private Layer? Selected => project.Layers.FirstOrDefault(x => x.Id == selected);
    private readonly string libraryPath;

    public Plugin(IDalamudPluginInterface pi, ICommandManager commands, IDataManager data, IFramework framework,
        IObjectTable objects, IClientState client, ICondition condition, IGameGui gameGui, IPluginLog log,
        ISigScanner scanner, IGameInteropProvider interop)
    {
        this.pi = pi; this.commands = commands; this.framework = framework; this.objects = objects; this.gameGui = gameGui; this.log = log;
        resources = new GameResources(data, pi, () => archive);
        compiler = new(resources); binder = new(resources); catalog = new(data);
        nativeVfx = new(scanner, interop, log, framework);
        createNativeObserver = () => new(scanner, interop, framework);
        preview = new(pi, objects, client, condition, nativeVfx, log);
        windowPreview = new(pi, objects, client, condition, nativeVfx, log);
        coordinator = new(new Dictionary<PreviewMode, IPreviewSurface> { [PreviewMode.Self] = preview, [PreviewMode.Window] = windowPreview });
        var config = pi.GetPluginConfigDirectory(); Directory.CreateDirectory(config);
        projectPath = Path.Combine(config, "我的组合.aethervfx.json"); exportPath = Path.Combine(config, "我的组合.pmp");
        libraryPath = Path.Combine(config, "library.json");
        Run(preview.Recover);
        Run(windowPreview.Recover);
        try
        {
            if (File.Exists(libraryPath)) library = JsonSerializer.Deserialize<LibraryState>(File.ReadAllText(libraryPath), ProjectStore.Json) ?? new();
            if (File.Exists(projectPath)) project = ProjectStore.Load(projectPath);
        }
        catch (Exception ex) { message = "读取上次配置失败：" + ex.Message; }
        InitializeBrowser(data, config);
        commands.AddHandler("/avfx", new CommandInfo((_, _) => open = !open) { HelpMessage = "打开随身特效工作台" });
        commands.AddHandler("/avfxprobe", new CommandInfo((_, args) => Run(() => RunNativeProbe(args))) { HelpMessage = "单项手动验证：view / original / mapped / composition / stop" });
        pi.UiBuilder.Draw += Draw; pi.UiBuilder.OpenMainUi += Open; pi.UiBuilder.OpenConfigUi += Open;
        framework.Update += Update;
    }
    private void Open() => open = true;
    private void Run(Action action)
    {
        try { action(); }
        catch (Exception ex) { message = ex.Message; log.Error(ex, "AetherVfx 操作失败"); }
    }
    private void Change(Action action)
    {
        history.Record(project); action(); Dirty();
    }
    private void Dirty()
    {
        dirtyAt = DateTime.UtcNow; bound = null; wizard = 0;
    }
    private void Update(IFramework _)
    {
        if (disposed) return;
        Run(preview.Tick);
        Run(windowPreview.Tick);
        if (coordinator.Mode == PreviewMode.Window && !windowPreview.Active && coordinator.ResourceId is { } assetId)
        {
            string reason = windowPreview.Status;
            if (coordinator.Scope == PreviewScope.Asset) resourceCatalog.SetCapability(assetId, ResourceCapability.Unavailable, reason);
            Run(coordinator.Stop); message = reason;
        }
        if (autoPreview && coordinator.Scope == PreviewScope.Composition && dirtyAt != DateTime.MaxValue && DateTime.UtcNow - dirtyAt >= TimeSpan.FromMilliseconds(350))
        { dirtyAt = DateTime.MaxValue; Run(StartPreview); }
    }
    private void Draw()
    {
        if (disposed) return;
        if (open)
        {
            ImGui.SetNextWindowSize(new(1340, 880), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSizeConstraints(new(960, 640), new(float.MaxValue));
            if (ImGui.Begin("随身特效工作台###AetherVfx", ref open, ImGuiWindowFlags.MenuBar))
            {
                Run(DrawBrowserContent);
            }
            ImGui.End();
        }
        if (!open && !layout.PreviewPopout && coordinator.Mode == PreviewMode.Window) Run(StopPreview);
        Run(DrawBrowserWindows);
        if (open && preview.Active && coordinator.Scope == PreviewScope.Composition && Selected is { } layer) Run(() => DrawGizmo(layer));
    }
    private void SaveLibrary() { libraryRevision++; AtomicFile.Write(libraryPath, JsonSerializer.SerializeToUtf8Bytes(library, ProjectStore.Json)); }
    private void DrawLayers()
    {
        float height = Math.Max(120, ImGui.GetContentRegionAvail().Y - 32);
        ImGui.BeginChild("图层", new(Math.Min(200, ImGui.GetContentRegionAvail().X * .32f), height), true);
        foreach (var l in project.Layers.ToArray())
        {
            ImGui.PushID(l.Id.ToString()); bool visible = l.Visible;
            if (ImGui.Checkbox("##显示", ref visible)) Change(() => l.Visible = visible);
            ImGui.SameLine(); if (ImGui.Selectable(l.Name, selected == l.Id)) selected = l.Id;
            ImGui.PopID();
        }
        ImGui.EndChild(); ImGui.SameLine();
        ImGui.BeginChild("属性", new(0, height), true);
        if (Selected is { } layer)
        {
            string before = ProjectStore.Serialize(project);
            bool changed = false;
            var name = layer.Name; if (ImGui.InputText("名称", ref name, 128)) { layer.Name = name; changed = true; }
            if (ImGui.Button("名称存为资源标签")) { library.Labels[layer.Source] = layer.Name; SaveLibrary(); }
            ImGui.SameLine(); if (ImGui.Button(solo == layer.Id ? "恢复全部" : "单独显示")) { solo = solo == layer.Id ? null : layer.Id; Dirty(); }
            var pos = layer.Position; if (ImGui.DragFloat3("位置", ref pos, .01f)) { layer.Position = pos; changed = true; }
            var rot = layer.RotationDegrees; if (ImGui.DragFloat3("旋转（度）", ref rot, .5f)) { layer.RotationDegrees = rot; changed = true; }
            var scale = layer.Scale; if (ImGui.DragFloat("大小", ref scale, .01f, .01f, 100f)) { layer.Scale = Math.Clamp(scale, .01f, 100); changed = true; }
            var color = layer.Color; if (ImGui.ColorEdit4("颜色乘算／透明度", ref color)) { layer.Color = color; changed = true; }
            if (ImGui.BeginCombo("挂点", AnchorName(layer.Anchor)))
            {
                foreach (var anchor in Enum.GetValues<Anchor>())
                {
                    ImGui.BeginDisabled(anchor == Anchor.Weapon || project.Target.Kind == EquipmentKind.Weapon && anchor != Anchor.Original);
                    if (ImGui.Selectable(AnchorName(anchor), layer.Anchor == anchor)) { layer.Anchor = anchor; changed = true; }
                    ImGui.EndDisabled();
                }
                ImGui.EndCombo();
            }
            bool loop = layer.Playback == Playback.Loop;
            if (ImGui.Checkbox("循环播放", ref loop)) { layer.Playback = loop ? Playback.Loop : Playback.Original; changed = true; }
            if (loop)
            {
                int frames = layer.LoopFrames, gap = layer.GapFrames;
                if (ImGui.InputInt("循环长度（30 帧/秒）", ref frames)) { layer.LoopFrames = Math.Clamp(frames, 1, 30000); changed = true; }
                if (ImGui.InputInt("重播间隔（帧）", ref gap)) { layer.GapFrames = Math.Clamp(gap, 0, 30000 - layer.LoopFrames); changed = true; }
            }
            if (changed) { editBefore ??= before; Dirty(); }
            if (!ImGui.IsAnyItemActive() && editBefore is not null)
            { history.Record(ProjectStore.Parse(editBefore)); editBefore = null; }
            if (ImGui.Button("重置变换与颜色")) Change(() => { layer.Position = layer.RotationDegrees = Vector3.Zero; layer.Scale = 1; layer.Color = Vector4.One; });
            ImGui.SameLine(); if (ImGui.Button("复制图层")) Change(() => { var copy = layer with { Id = Guid.NewGuid(), Name = layer.Name + " 副本" }; project.Layers.Add(copy); selected = copy.Id; });
            ImGui.SameLine(); if (ImGui.Button("删除图层")) Change(() => { project.Layers.Remove(layer); if (solo == layer.Id) solo = null; selected = project.Layers.LastOrDefault()?.Id; });
            ImGui.BeginDisabled(Asset is null);
            if (ImGui.Button("用选中素材重新绑定来源")) Change(() =>
            {
                var reference = Asset!.Preferred; layer.Resource = reference;
                layer.Source = reference.Kind == ResourceKind.GamePath ? reference.Path : "归档 " + Asset.Id[..12];
            });
            ImGui.EndDisabled();
        }
        else ImGui.TextUnformatted("从特效库添加一个图层，再选择它进行调整。");
        ImGui.EndChild();
        ImGui.RadioButton("移动手柄", ref gizmoMode, 0); ImGui.SameLine();
        ImGui.RadioButton("旋转圆环", ref gizmoMode, 1); ImGui.SameLine(); ImGui.RadioButton("缩放手柄", ref gizmoMode, 2);
    }
    private static string AnchorName(Anchor anchor) => anchor switch
    {
        Anchor.Original => "保留原挂点", Anchor.Origin => "角色原点", Anchor.Feet => "脚下（角色原点）", Anchor.Head => "头部",
        Anchor.LeftHand => "左手", Anchor.RightHand => "右手", Anchor.Body => "身体中心", _ => "武器（适配中）"
    };
    private void UseWornBody()
    {
        var target = ReadWornBody(); Change(() => project.Target = target); message = "已选择当前身体装备：" + target.Name;
    }
    private unsafe EquipmentTarget ReadWornBody()
    {
        if (!indexed) { catalog.Build(); indexed = true; }
        var manager = InventoryManager.Instance();
        if (manager == null) throw new InvalidOperationException("角色背包尚未加载");
        var container = manager->GetInventoryContainer(InventoryType.EquippedItems);
        if (container == null) throw new InvalidOperationException("角色装备尚未加载");
        var item = container->GetInventorySlot(3);
        if (item == null) throw new InvalidOperationException("未穿戴身体装备");
        uint id = item->GlamourId != 0 ? item->GlamourId : item->GetItemId();
        var target = catalog.Equipment.FirstOrDefault(x => x.ItemId == id) ?? throw new InvalidOperationException("找不到当前身体装备数据");
        return target with { };
    }
    private void StartPreview()
    {
        autoPreview = false;
        if (!NativePreviewSafety.Enabled) { message = NativePreviewSafety.Message; autoPreview = false; return; }
        if (layout.Mode == PreviewMode.Window && !windowPreview.Access.CanStart) { message = windowPreview.Access.Diagnostic; return; }
        coordinator.Stop();
        preview.Recover();
        // Selecting an export target must not gate or alter workbench preview.
        var result = compiler.CompileForPreview(project, solo); diagnostics = result.Diagnostics.ToList();
        if (!result.Success) { autoPreview = false; return; }
        coordinator.Play(layout.Mode, PreviewScope.Composition, new("composition", project.Name, result.Effect!)); dirtyAt = DateTime.MaxValue;
        autoPreview = true;
    }
    private void StopPreview()
    {
        autoPreview = false; dirtyAt = DateTime.MaxValue;
        coordinator.Stop(); windowPreview.Stop();
    }
    private void DrawExport()
    {
        ImGui.TextUnformatted("1. 选择目标装备");
        if (ImGui.Button("读取当前身体装备（含幻化）")) Run(UseWornBody);
        ImGui.InputText("搜索装备", ref itemSearch, 128);
        if (itemSearch.Trim().Length > 0)
        {
            ImGui.BeginChild("装备搜索", new(0, 110), true);
            foreach (var target in catalog.Equipment.Where(x => x.Name.Contains(itemSearch, StringComparison.OrdinalIgnoreCase) || x.ItemId.ToString() == itemSearch).Take(60))
                if (ImGui.Selectable($"{target.Name} · {target.Slot}##{target.ItemId}"))
                    Change(() => project.Target = target with { });
            ImGui.EndChild();
        }
        ImGui.TextWrapped($"目标：{project.Target.Name} · {project.Target.Slot} · 模型 {project.Target.PrimaryId}/{project.Target.SecondaryId} · 变体 {project.Target.Variant}");
        if (ImGui.Button("把此装备特效加入资源库")) Run(() =>
        {
            foreach (var entry in catalog.ExpandEquipment(project.Target))
            {
                var data = resources.Read(entry.Path);
                resourceCatalog.Upsert(new() { Id = ResourceCatalog.Hash(data), Name = entry.Name, References = [ResourceReference.GamePath(entry.Path)],
                    Associations = [new(AssociationKind.Equipment, project.Target.ItemId.ToString(), project.Target.Name, AssociationEvidence.IndirectReference, project.Target.ImcPath)] });
            }
        });
        int vfx = project.Target.VfxId;
        if (ImGui.InputInt("目标特效 ID", ref vfx)) Change(() => project.Target.VfxId = (byte)Math.Clamp(vfx, 1, 255));
        bool keep = project.Target.KeepOriginal;
        if (ImGui.Checkbox("保留并合并装备原特效", ref keep)) Change(() => project.Target.KeepOriginal = keep);
        ImGui.TextUnformatted("2. 填写 Mod 信息");
        string name = project.Name, author = project.Author;
        if (ImGui.InputText("Mod 名称", ref name, 128)) Change(() => project.Name = name);
        if (ImGui.InputText("作者", ref author, 128)) Change(() => project.Author = author);
        ImGui.InputText("PMP 保存路径", ref exportPath, 2048);
        ImGui.TextUnformatted("3. 检查共享模型与导出内容");
        var shared = catalog.SharedEquipment(project.Target);
        ImGui.TextWrapped($"共享此模型的装备共 {shared.Count} 件；AVFX 路径由模型与特效 ID 共享，其他变体若引用相同 ID 也可能受影响。");
        if (ImGui.TreeNode("查看共享装备")) { foreach (var item in shared) ImGui.TextUnformatted(item); ImGui.TreePop(); }
        if (solo is not null) ImGui.TextWrapped("当前处于单独预览；导出始终包含所有可见图层。");
        if (ImGui.Button("检查导出")) Run(() =>
        {
            autoPreview = false; coordinator.Stop(); bound = null; wizard = 0;
            if (project.Target.Kind == EquipmentKind.Weapon && project.Layers.Any(l => l.Visible && l.Anchor != Anchor.Original))
                throw new InvalidOperationException("武器目标当前仅支持保留原挂点");
            var result = compiler.Compile(project); diagnostics = result.Diagnostics.ToList();
            if (!result.Success) return;
            bound = binder.Bind(result.Effect!, project.Target); wizard = 1;
            message = $"检查通过：{project.Layers.Count(x => x.Visible)} 个图层，{bound.Effect.Dependencies.Count} 项依赖；请复核共享装备范围。";
        });
        ImGui.SameLine(); ImGui.BeginDisabled(wizard != 1 || bound is null);
        if (ImGui.Button("确认影响范围并导出 PMP")) Run(() =>
        {
            if (!exportPath.EndsWith(".pmp", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("导出路径应以 .pmp 结尾");
            PmpExporter.Export(exportPath, project, bound!); message = "已导出：" + exportPath;
        });
        ImGui.EndDisabled();
    }

    private void DrawGizmo(Layer layer)
    {
        var player = objects.LocalPlayer; if (player is null) return;
        float yaw = player.Rotation;
        var orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);
        var origin = player.Position + Vector3.Transform(layer.Position, orientation);
        if (!gameGui.WorldToScreen(origin, out var center)) return;
        var draw = ImGui.GetForegroundDrawList();
        uint[] colors = [0xFF5555FF, 0xFF55DD55, 0xFFFFAA55];
        Vector3[] axes = [Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ];
        var mouse = ImGui.GetMousePos();
        if (gizmoMode == 1)
        {
            for (int axis = 0; axis < 3; axis++)
            {
                Vector2? previous = null;
                for (int j = 0; j <= 48; j++)
                {
                    float t = j * MathF.Tau / 48;
                    var point = axes[(axis + 1) % 3] * MathF.Cos(t) + axes[(axis + 2) % 3] * MathF.Sin(t);
                    if (!gameGui.WorldToScreen(origin + Vector3.Transform(point * .7f, orientation), out var screen)) { previous = null; continue; }
                    if (previous is { } p)
                    {
                        draw.AddLine(p, screen, colors[axis], 2);
                        if (dragAxis < 0 && !ImGui.GetIO().WantCaptureMouse && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && Distance(mouse, p, screen) < 7)
                            BeginDrag(axis);
                    }
                    previous = screen;
                }
            }
        }
        else for (int axis = 0; axis < 3; axis++)
        {
            if (!gameGui.WorldToScreen(origin + Vector3.Transform(axes[axis] * .9f, orientation), out var end)) continue;
            draw.AddLine(center, end, colors[axis], 3);
            if (gizmoMode == 0)
            {
                var direction = Vector2.Normalize(end - center); var side = new Vector2(-direction.Y, direction.X);
                draw.AddTriangleFilled(end, end - direction * 12 + side * 5, end - direction * 12 - side * 5, colors[axis]);
            }
            else draw.AddRectFilled(end - new Vector2(5), end + new Vector2(5), colors[axis]);
            if (dragAxis < 0 && !ImGui.GetIO().WantCaptureMouse && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && Distance(mouse, center, end) < 8) BeginDrag(axis);
        }
        draw.AddText(center + new Vector2(10, 10), 0xFFFFFFFF, layer.Name);
        if (dragAxis >= 0)
        {
            ImGui.SetNextFrameWantCaptureMouse(true);
            if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                var delta = mouse - dragStart;
                if (gizmoMode == 0 && gameGui.WorldToScreen(player.Position + Vector3.Transform(dragPosition, orientation), out var start) && gameGui.WorldToScreen(player.Position + Vector3.Transform(dragPosition + axes[dragAxis], orientation), out var end))
                {
                    var projected = end - start;
                    if (projected.LengthSquared() > 4) layer.Position = dragPosition + axes[dragAxis] * (Vector2.Dot(delta, projected) / projected.LengthSquared());
                }
                else if (gizmoMode == 1) layer.RotationDegrees = dragRotation + axes[dragAxis] * delta.X;
                else layer.Scale = Math.Clamp(dragScale * MathF.Exp(delta.X / 150), .01f, 100);
                Dirty();
            }
            else
            {
                if (dragBefore is not null && dragBefore != ProjectStore.Serialize(project)) history.Record(ProjectStore.Parse(dragBefore));
                dragAxis = -1; dragBefore = null;
            }
        }
        void BeginDrag(int axis)
        {
            dragAxis = axis; dragStart = mouse; dragPosition = layer.Position; dragRotation = layer.RotationDegrees; dragScale = layer.Scale;
            dragBefore = ProjectStore.Serialize(project);
        }
    }
    private static float Distance(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a; float t = ab.LengthSquared() < 1 ? 0 : Math.Clamp(Vector2.Dot(p - a, ab) / ab.LengthSquared(), 0, 1);
        return Vector2.Distance(p, a + ab * t);
    }
    public void Dispose()
    {
        disposed = true;
        Run(StopObserver);
        framework.Update -= Update; pi.UiBuilder.Draw -= Draw; pi.UiBuilder.OpenMainUi -= Open; pi.UiBuilder.OpenConfigUi -= Open;
        commands.RemoveHandler("/avfx"); commands.RemoveHandler("/avfxprobe"); DisposeBrowser(); Run(coordinator.Stop); Run(preview.Dispose); Run(windowPreview.Dispose); Run(nativeVfx.Dispose); Run(() => ProjectStore.Save(projectPath, project)); Run(SaveLibrary);
    }
}

internal sealed class LibraryState
{
    public HashSet<string> Favorites { get; set; } = [];
    public List<string> Recent { get; set; } = [];
    public Dictionary<string, string> Labels { get; set; } = [];
    public Dictionary<string, string[]> UserTags { get; set; } = [];
    public Dictionary<string, SourceAssociation[]> UserAssociations { get; set; } = [];
}
