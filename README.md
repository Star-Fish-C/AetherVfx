# 随身特效工作台 AetherVfx

**当前版本：0.3.4 开发接续版。** 中文 Dalamud 特效目录、组合编辑与装备 Penumbra PMP 导出工具。

**原生预览保持停用，独立播放器尚未完成。** 0.3.3 曾因原生指针读取导致客户端崩溃；本版删除了独立视图的槽位猜测和自动连续探针。实验开关不能绕过兼容性检查。打开独立面板只显示状态，不申请视图，不在真实玩家角色上播放。

本版已在家中环境完成插件与 CLI 构建、39 项自动测试、真实游戏素材编译及三类 PMP 文件验证。未进行游戏内视觉验收，也未确认 CharaView 的粒子通道。换电脑继续开发先读 [跨电脑接续说明](docs/HANDOFF-20261027.md)；详细证据见 [验收记录](docs/ACCEPTANCE.md)、[接续开发](docs/DEVELOPMENT.md) 和 [原生兼容性调查](docs/NATIVE-COMPATIBILITY.md)。历史诊断见 [0.3.1](docs/PREVIEW-0.3.1.md) 与 [崩溃记录](docs/2026-10-26_原生预览崩溃-report.md)，其中的播放操作不代表当前可用功能。

## 当前可以做什么

- 从本机游戏归档建立 AVFX 目录，浏览地区／怪物／副本等来源关联，搜索、收藏和管理素材。
- 将素材加入组合，调整位置、旋转、缩放、颜色、挂点与播放参数，保存工程、撤销和重做。
- 选择装备绑定目标，编译并导出包含依赖、IMC 操作与工程 JSON 的 PMP。
- 查看独立预览的停用原因、会话状态及实验开关；开关只在本次插件实例中有效。

编辑与导出代码可用不等于所有素材都已通过游戏画面测试。完整流程仍需在独立 Penumbra 集合进行验收。

## 启动工作台

需要国服 Dalamud API 15、.NET 10，以及已启用的 Penumbra；当前构建依赖 Penumbra API 1.7.2.4。

1. 按下方命令构建；完整依赖在 `artifacts/plugin`，不要只复制一个 DLL。
2. 在 Dalamud 开发者插件设置中添加该目录，加载后输入 `/avfx`。
3. 等待后台目录初始化，通过“索引”菜单暂停、继续或查看诊断。
4. 单击选择素材；双击或“加入组合”才改变工程。目前单击不会播放。
5. 在组合编辑区调整参数，在“工程 → 装备绑定与 PMP 导出”选择装备并检查共享影响范围。
6. 导出后在 Penumbra 导入测试；绑定、挂点和粒子显示均须人工确认。

“打开独立预览面板”默认采用角色与特效同屏的开发方向。纯特效空场地、相机交互、实际播放和帧回读均暂未交付，不展示为已完成能力。

## 编辑与导出边界

每个完整特效对应一个图层，包含位置、三轴旋转、等比例缩放、颜色乘算、透明度、挂点及播放设置。导出包含全部可见图层。工程再次编译仍需原始游戏资源；来自其他 Mod 的素材必须保留来源。PMP 的目标是通过 Penumbra 独立显示，实际效果尚待游戏内验收。

- 颜色采用乘算并保留动画；缺少标准颜色曲线时拒绝调色。
- 整体变换添加父发射器，复杂继承规则仍需逐类实测。
- 自动循环只支持有限时间线、无裁剪片段且循环长度覆盖原时间线的资源。
- 仅支持单调度器；全局设置或事件配置不兼容时拒绝合并。
- 含灯光／屏幕效果节点的资源拒绝组合；节点和关键帧容量上限为本地已知的 127。
- 身体中心、头部和左右手使用标准 EID；武器目标仅支持原挂点。
- 装备共享模型和 VFX 路径，导出可能影响其他物品与变体，导出页的列表不是完整活动 Mod 冲突分析。
- 当前穿戴读取包含游戏自身幻化，不解析 Glamourer 覆盖；IMC 基线尚未合并其他插件的元数据操作。
- 未知路径资源、用户名称与分类、索引版本失效和工程迁移继续保留；目录覆盖与播放能力分别记录。

## 构建和验证

在仓库根目录执行：

```powershell
./scripts/Verify-Development.ps1
```

验证脚本构建插件与 CLI、运行测试，将开发文件复制到 `artifacts/plugin`，并在 `artifacts/validation/home` 保存日志、环境和文件摘要。首次需要访问 NuGet。只需验证离线游戏资源时传入本机 SqPack 路径：

```powershell
./scripts/Verify-Development.ps1 -Sqpack '你的游戏目录\game\sqpack'
```

它会编译已知素材，生成防具、武器和饰品三类三图层样本，检查 PMP 并重编译一个工程；不会启动游戏或修改游戏安装。样本均使用同一光环验证数据流程，不是三种外观的美术成品。资源与产物保存在被 Git 忽略的 `artifacts` 下，不上传游戏资源。

也可以单独执行：

```powershell
dotnet build src/AetherVfx.Plugin/AetherVfx.Plugin.csproj -c Release
dotnet run --project tests/AetherVfx.Tests -c Release
```

如有真实 AVFX 样本目录，测试命令末尾可追加 `-- '本机样本目录'`，或向脚本传入 `-OriginalSamples`。未提供时不宣称完成原始文件往返测试。

Dalamud 优先使用 `DALAMUD_HOME`，否则使用 `%APPDATA%\XIVLauncherCN\addon\Hooks\dev`。GameData 与 CLI 也沿用该选择；可使用 `-p:GameLibPath=...` 覆盖。Penumbra 默认位于 `%APPDATA%\XIVLauncherCN\installedPlugins\Penumbra\1.7.2.4`，其他安装位置使用 `-p:PenumbraApiPath=...\Penumbra.Api.dll`。

SDK 同时生成 `src/AetherVfx.Plugin/bin/Release/AetherVfx/latest.zip`。不要将本版称为可播放的 0.4 实验版。

## 手动验证入口

`/avfxprobe view|original|mapped|composition|stop` 每次只请求一项，不排队、不自动继续、不在加载后运行。目前兼容门槛未通过，启动请求只记录拒绝原因，不调用原生接口。通过门槛并另行约定验证时段后，才接入实际视图后端。

## 实现结构

- `AetherVfx.Core`：工程与历史、AVFX 读写、变换调色、合并校验、装备绑定与 PMP 导出，以及托管预览会话模型。
- `AetherVfx.Plugin`：中文工作台、目录、Penumbra 接入与预览兼容门槛；独立原生分配器尚未接入。
- `AetherVfx.GameData`：归档读取与来源关联、版本化怪物快照。
- `AetherVfx.Cli`：离线资源检查、示例生成和工程编译。

无新增对外 IPC。源码不包含游戏资源、原生转储或本机目录缓存。第三方来源见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。
