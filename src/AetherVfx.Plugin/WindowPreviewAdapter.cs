using AetherVfx.Core;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Client.System.Memory;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using System.Numerics;

namespace AetherVfx;

// A native CharaView/texture probe, not a capture of the world viewport.
// The particle/empty-scene pass remains PendingGameValidation until verified in the client.
internal sealed unsafe class WindowPreviewAdapter : IPreviewSurface, IDisposable
{
    private readonly IObjectTable objects;
    private readonly IClientState client;
    private readonly PreviewManager collection;
    private readonly IPluginLog logger;
    private CharaView* view;
    private Character* ownedCharacter;
    private PreviewPayload? pending;
    private uint frame;
    private uint territory;
    private bool gpose;
    private int framesWaiting;
    private uint textureIndex = 7;
    private ushort clientObjectId;
    private bool slotAccepted;
    public bool Active => view != null;
    public string Status { get; private set; } = "独立窗口原生渲染尚未进行游戏内验证";
    public ResourceCapability Capability => ResourceCapability.PendingGameValidation;
    public bool EmptyScene { get; set; } = true;
    public string Diagnostic => collection.Diagnostic;
    public string EffectCreationError { get; private set; } = "";
    public string SceneDiagnostic => view == null ? "未分配视图" : $"State={view->State}, ClientId={view->ClientObjectId}, Index={view->ClientObjectIndex}, Frame={frame}, Carrier={(nint)ownedCharacter:X}";
    public bool RenderTargetReady => view != null && view->State == 6 && pending is null;
    public bool ShowGrid { get; set; } = true;
    public Vector4 Background { get; set; } = new(.08f, .09f, .12f, 1);

    public WindowPreviewAdapter(IDalamudPluginInterface pi, IObjectTable objects, IClientState client, ICondition condition, NativeVfxEngine engine, IPluginLog log)
    { this.objects = objects; this.client = client; logger = log; collection = new(pi, objects, client, condition, engine, log, "window"); }
    public void Recover() => collection.Recover();
    public void Play(PreviewPayload payload)
    {
        NativePreviewSafety.RequireEnabled();
        Stop();
        EffectCreationError = "";
        var player = objects.LocalPlayer ?? throw new InvalidOperationException("请先登录角色");
        var manager = ClientObjectManager.Instance();
        if (manager == null) throw new InvalidOperationException("角色视图管理器尚未就绪");
        if (PartyPortraitsActive()) throw new InvalidOperationException("队伍肖像正在使用共享预览槽，暂不能启动独立预览");
        try
        {
            var inventory = new List<string>();
            var offscreen = OffscreenRenderingManager.Instance();
            if (offscreen == null) throw new InvalidOperationException("原生离屏渲染管理器尚未就绪");
            for (int index = 7; index >= 5; index--)
            {
                view = IMemorySpace.GetUISpace()->Create<CharaView>();
                if (view == null) throw new InvalidOperationException("无法分配原生角色预览");
                // Initialize takes a view index. Runtime CN mapping is index + 240, not upstream's old +40.
                view->Initialize(null, (uint)index, 0);
                clientObjectId = PreviewViewSlot.Validate((uint)index, view->ClientObjectIndex, view->ClientObjectId, manager->BattleCharas.Length);
                var existing = manager->GetObjectByIndex(clientObjectId);
                Camera* camera = offscreen->Cameras[index];
                // ClientObjectManager preallocates all eight carrier shells. A non-null shell is not an active view.
                if (existing == null || existing->GameObject.DrawObject == null)
                { textureIndex = (uint)index; slotAccepted = true; break; }
                var entry = manager->BattleCharas[clientObjectId];
                inventory.Add($"view={index},id={clientObjectId},kind={entry.ObjectKind},actor={(nint)existing:X},draw={(nint)(existing == null ? null : existing->GameObject.DrawObject):X},camera={(nint)camera:X}");
                IMemorySpace.Free(view); view = null;
            }
            if (!slotAccepted) throw new InvalidOperationException("独立视图无可独占的空闲槽，未接管游戏角色：" + string.Join("; ", inventory));
            var model = new CharaViewModelData(); model.CopyFromCharacter((Character*)player.Address);
            view->SetModelData(&model);
            pending = payload; territory = client.TerritoryType; gpose = client.IsGPosing; framesWaiting = 0;
            Status = "正在创建独立角色及渲染目标；VFX 通道待游戏内验收";
        }
        catch { Stop(); throw; }
    }
    public void Tick()
    {
        if (!Active) return;
        if (objects.LocalPlayer is null || client.TerritoryType != territory || client.IsGPosing != gpose) { Stop(); Status = "场景已变化，独立预览已释放"; return; }
        if (PartyPortraitsActive()) { Stop(); Status = "队伍肖像已打开，独立预览已停止"; return; }
        collection.Tick();
    }
    public void Draw(Vector2 size)
    {
        size = Vector2.Max(size, new(50));
        var start = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(start, start + size, ImGui.ColorConvertFloat4ToU32(Background));
        if (view == null) { ImGui.TextWrapped(Status); ImGui.Dummy(new(size.X, Math.Max(1, size.Y - 40))); return; }
        try
        {
            var manager = ClientObjectManager.Instance();
            var current = manager == null ? null : manager->GetObjectByIndex(clientObjectId);
            if (ownedCharacter != null && current != ownedCharacter)
            {
                // Never Release a game-owned replacement at a reused slot.
                collection.Stop(); IMemorySpace.Free(view); view = null; ownedCharacter = null; pending = null; slotAccepted = false;
                Status = "游戏接管了预览槽；已停止。原视图的原生清理需客户端复核"; ImGui.TextWrapped(Status); return;
            }
            // Suppress only CharacterBase draw objects, never character Alpha or VFX draw objects.
            if (ownedCharacter != null)
            {
                SetCarrierModelVisibility(ownedCharacter, !EmptyScene);
                if (ownedCharacter->GameObject.DrawObject != null)
                    collection.Center(ownedCharacter->GameObject.DrawObject->Position, ownedCharacter->GameObject.Rotation);
            }
            view->Render(frame++);
            var character = view->GetCharacter();
            if (character != null) ownedCharacter = character;
            if (view->State == 6 && character != null && pending is { } payload)
            {
                pending = null;
                try
                {
                    collection.ShowFor(payload.Effect, character->GameObject.ObjectIndex, (nint)character, false, payload.NativeGamePath);
                    Status = "原生角色画面已建立；VFX 粒子画面仍待验证";
                }
                catch (Exception ex)
                {
                    EffectCreationError = ex.Message;
                    Status = "独立视图已就绪，但 VFX 创建失败：" + ex.Message;
                    logger.Warning("AetherVfx 原生载体创建 VFX 失败：{Error}；{Scene}", ex.ToString(), SceneDiagnostic);
                }
            }
            if (character != null)
            {
                SetCarrierModelVisibility(character, !EmptyScene);
                if (character->GameObject.DrawObject != null)
                    collection.Center(character->GameObject.DrawObject->Position, character->GameObject.Rotation);
            }
            var targets = RenderTargetManager.Instance();
            var texture = targets == null ? null : targets->GetCharaViewTexture(textureIndex);
            if (texture != null && texture->D3D11ShaderResourceView != null)
            {
                ImGui.Image(new ImTextureID((nint)texture->D3D11ShaderResourceView), size);
                if (ImGui.IsItemHovered())
                {
                    var io = ImGui.GetIO();
                    if (ImGui.IsMouseDragging(ImGuiMouseButton.Left)) view->SetCameraYawAndPitch(io.MouseDelta.X * .01f, io.MouseDelta.Y * .01f);
                    if (ImGui.IsMouseDragging(ImGuiMouseButton.Right)) view->SetCameraXAndY(io.MouseDelta.X * .005f, -io.MouseDelta.Y * .005f);
                    if (io.MouseWheel != 0) view->SetCameraDistance(-io.MouseWheel * .2f);
                }
            }
            else
            {
                ImGui.TextWrapped("等待原生渲染目标……"); ImGui.Dummy(new(size.X, Math.Max(1, size.Y - 30)));
                if (++framesWaiting > 600) { Stop(); Status = "原生视图未产生渲染目标，已释放；请查看日志并使用自身预览"; }
            }
        }
        catch (Exception ex) { Stop(); Status = "原生预览失败：" + ex.Message; logger.Warning("AetherVfx 原生视图失败：{Error}", ex.ToString()); }
    }
    public void ResetCamera() { if (view != null) view->ResetPositions(); }
    private static bool PartyPortraitsActive()
    {
        var agents = AgentModule.Instance();
        var banner = agents == null ? null : agents->GetAgentByInternalId(AgentId.BannerParty);
        return banner != null && banner->IsAgentActive();
    }
    public NativeTargetReadback RetainRenderTarget()
    {
        if (view == null || view->State != 6) throw new InvalidOperationException("独立视图尚未就绪：" + SceneDiagnostic);
        var targets = RenderTargetManager.Instance();
        var texture = targets == null ? null : targets->GetCharaViewTexture(textureIndex);
        if (texture == null || texture->D3D11ShaderResourceView == null) throw new InvalidOperationException("独立视图未产生 SRV");
        return new((nint)texture->D3D11ShaderResourceView, (int)texture->ActualWidth, (int)texture->ActualHeight);
    }
    private static void SetCarrierModelVisibility(Character* character, bool visible)
    {
        var root = character->GameObject.DrawObject;
        if (root == null) return;
        Visit((FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object*)root, visible, 0);
        static void Visit(FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object* node, bool visible, int depth)
        {
            if (depth > 8) return;
            if (node->GetObjectType() == FFXIVClientStructs.FFXIV.Client.Graphics.Scene.ObjectType.CharacterBase)
                ((FFXIVClientStructs.FFXIV.Client.Graphics.Scene.DrawObject*)node)->IsVisible = visible;
            var first = node->ChildObject; var child = first;
            for (int i = 0; child != null && i < 64; i++)
            {
                Visit(child, visible, depth + 1);
                child = child->NextSiblingObject;
                if (child == first) break;
            }
        }
    }
    public void Fit() { if (view != null) { view->ResetPositions(); view->SetCameraDistance(1); } }
    public void Stop()
    {
        try { collection.Stop(); }
        finally { ReleaseNative(); }
    }
    private void ReleaseNative()
    {
        pending = null;
        if (view != null)
        {
            var manager = ClientObjectManager.Instance();
            var current = manager == null || !slotAccepted ? null : manager->GetObjectByIndex(clientObjectId);
            if (!slotAccepted || ownedCharacter != null && current != null && current != ownedCharacter) IMemorySpace.Free(view);
            else view->Dtor(1);
            view = null;
        }
        ownedCharacter = null; slotAccepted = false; framesWaiting = 0; Status = "独立预览已停止；VFX 渲染仍待游戏内验收";
    }
    public void Dispose() => Stop();
}
