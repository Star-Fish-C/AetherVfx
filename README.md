# 随身特效工作台 AetherVfx

**0.3.3 保护版**：2026-10-26 的验证构建在读取 VFX 内部资源指针时触发客户端崩溃。现已移除该指针读取、删除自动测试启动逻辑，并禁用自身／独立窗口原生预览。目录、工程编辑及导出代码保留；独立播放器尚未实现完成。详见 [崩溃记录](docs/2026-10-26_原生预览崩溃-report.md)。

**0.3.1 诊断版**：自身预览默认将 VFX 根坐标放在角色位置并跟随移动；新增“打开空场地预览窗口（实验）”、原始游戏路径光环对照播放，以及 AVFX 资源加载状态和坐标诊断。修正预览角色的隐藏调用。当前未确认“看不到特效”已解决，独立窗口真实粒子渲染仍未验收，详见 [预览诊断](docs/PREVIEW-0.3.1.md)。

**0.3.0 更新**：浏览和组合预览改为直接创建角色 VFX，最后导出时才选择目标装备。保留 0.2.1 的内存加载路径修复，当前共 31 项自动测试通过。原生播放仍需当前国服客户端游戏内验证。

中文 Dalamud 插件：全量 AVFX 目录、地区／怪物／副本关联，以及常驻图层工作台，支持装备 Penumbra `.pmp` 导出。

源码仓库不包含游戏资源、原生转储、构建产物或本机生成的目录缓存。首次运行会从本机游戏安装建立索引；怪物关联使用已注明来源的元数据快照。

**0.2 更新**：顶部菜单与分类栏目，左侧来源树、中间虚拟列表、右侧预览与组合编辑；单击只预览，双击或按钮加入工程。支持未知路径资源、后台扫描、检查点恢复和旧工程迁移。完整说明见 [全量浏览器说明](docs/BROWSER-0.2.md)。

**当前为开发预览版，已通过编译和文件级测试，尚未完成游戏内视觉验收。** 不应把生成测试包等同于已经确认游戏中的缩放、挂点、循环和拔收刀行为。验收记录见 [游戏内验收](docs/ACCEPTANCE.md)。

## 启动工作台

需要国服 Dalamud API 15、.NET 10，以及已启用的 Penumbra。当前编译使用本机 Penumbra 1.7.2.4 的 API。

1. 使用本项目 `artifacts/plugin` 内的开发构建，或按照下方命令自行构建。
2. 在 Dalamud 开发者插件设置中添加包含 `AetherVfx.dll` 的目录；保留同目录所有依赖文件。
3. 加载插件，输入 `/avfx` 打开或关闭窗口。
4. 等待目录初始化，扫描与来源关联自动在后台进行；通过顶部“索引”菜单暂停、继续、取消或查看诊断。
5. 切换“全部／地区／怪物／副本”等栏目，单击列表条目预览；默认直接在自身播放 VFX，无需选择承载装备，不会修改工程。
6. 双击条目或点击“加入组合”，在右侧常驻工作台调整图层。使用“组合预览”播放工程；自身模式仍支持游戏画面手柄。
7. 在顶部“工程 → 装备绑定与 PMP 导出”中选择装备，检查共享影响范围后导出。
8. 用“预览 → 停止并恢复”结束预览，在 Penumbra 导入 PMP、启用并重绘角色。关闭本插件验证独立显示。

自身预览通过 Penumbra 在角色当前集合注册临时资源映射，并直接调用原生 ActorVFX 创建接口；不修改装备 IMC，不重绘角色。当前角色没有有效集合时才创建临时集合；停止时仅删除本插件注册的映射或集合。预览仍需要启用 Penumbra。换区、角色地址变化、进入或退出 GPose 时停止预览，需要重新开始。

关闭工作台窗口会保留自身预览，需要结束时使用停止按钮。独立原生预览在关闭其窗口时释放。参数编辑约 350 毫秒合并更新，先清理旧 VFX，再播放最新编译结果。单次资源自然结束后可重新播放；编辑组合后可自动更新。接口匹配失败、资源重定向失败及创建失败均显示诊断并记录日志。独立预览已接入 CharaView，但粒子通道、深度、空场景和客户端槽位生命周期尚未实测，不能视为已验收功能。

## 编辑和导出

每个完整特效对应一个图层，拥有独立的位置、三轴旋转、等比例缩放、颜色乘算、透明度、挂点及播放设置。单独显示用于确认正在修改的效果；导出始终包含全部可见图层。支持复制、删除、撤销、重做及工程保存。

目录扫描本机全部归档条目，通过文件内容识别 AVFX；已知路径、名称与分类由独立来源索引补充。无名称和无可靠分类的资源保留。支持联合搜索、收藏、最近使用、自定义名称及用户分类。目录覆盖与播放、编辑能力分别记录。

导出的 PMP 包含编译后的 AVFX、所需贴图／声音依赖、文件映射与装备 IMC 修改，并附带工程 JSON。工程可以再次编辑，但重新编译仍需原始游戏资源；若来源为其他 Mod，需保留该来源。最终 PMP 显示不需要本插件运行。

装备绑定支持防具、饰品和武器。默认替换原特效，可选择保留合并。由于装备按模型和 VFX ID 共享路径，可能影响共享资源的其他物品与变体；导出页会列出相同模型的装备供检查，并非精确判断所有活动 Mod 的影响范围。

## 已知支持边界

- 颜色使用乘算，保留颜色动画和透明度变化；不是将所有像素强制替换成选定颜色。缺少标准颜色曲线时拒绝调色。
- 整体变换使用新增父发射器，不重写原粒子动画。继承规则复杂的特效是否完整响应变换，仍须逐类游戏实测。
- 循环写入时间线数据，不使用插件计时器反复生成。自动转换仅支持无裁剪片段、有限时间线且循环长度覆盖原时间线的特效。原本自然循环的资源可保持原播放设置。
- 仅支持单调度器。事件触发槽会按槽合并为组合时间线；全局设置、触发延迟或事件循环配置不兼容时拒绝合并。
- 含灯光／屏幕效果节点的资源当前拒绝组合。合并后每类 AVFX 节点和曲线关键帧限制为本地样本已知容量 127。
- 身体中心、头部和左右手使用标准 EID 挂点；身体原点与脚下均为角色原点。武器目标当前仅支持保留原挂点，单独“武器挂点”不可选。
- 画面手柄以角色局部坐标为基准，不从粒子可见形状拾取，也不追踪每个动画挂点的实时中心。
- 素材与组合预览不依赖装备。只有导出页的“使用当前身体装备”读取当前穿戴信息，包含游戏自身幻化，未解析 Glamourer 的显示覆盖。
- IMC 基线读取原始游戏文件或文件重定向，尚未合并其他插件已有的 IMC 元数据操作。遇到冲突应先在独立 Penumbra 集合验收。

## 构建和测试

在项目根目录执行：

```powershell
dotnet build src/AetherVfx.Plugin/AetherVfx.Plugin.csproj -c Release
dotnet run --project tests/AetherVfx.Tests -c Release -- ..\originals
```

SDK 默认优先使用已有 `DALAMUD_HOME`，否则使用 `%APPDATA%\XIVLauncherCN\addon\Hooks\dev`。如 Penumbra 不在默认版本目录，可显式设置：

```powershell
dotnet build src/AetherVfx.Plugin/AetherVfx.Plugin.csproj -c Release -p:PenumbraApiPath="C:\Users\Administrator\AppData\Roaming\XIVLauncherCN\installedPlugins\Penumbra\1.7.2.4\Penumbra.Api.dll"
```

开发 DLL 输出在 `src/AetherVfx.Plugin/bin/Release`。SDK 同时生成 `AetherVfx/latest.zip`。无外部测试包依赖；测试程序失败时返回非零状态。

## 离线编译及检查 PMP

CLI 使用已安装的 Lumina 读取本地游戏文件，不需要运行游戏。以本机游戏目录为例：

```powershell
dotnet run --project tools/AetherVfx.Cli -c Release -- probe F:\FF14\Game\game\sqpack vfx/common/eff/dk10ht_wra0c.avfx
dotnet run --project tools/AetherVfx.Cli -c Release -- demo F:\FF14\Game\game\sqpack artifacts\samples
dotnet run --project tools/AetherVfx.Cli -c Release -- verify artifacts\samples\Equipment.pmp
dotnet run --project tools/AetherVfx.Cli -c Release -- compile artifacts\samples\Equipment.aethervfx.json F:\FF14\Game\game\sqpack artifacts\samples\recompiled.pmp
```

`demo` 从本机数据挑选防具、武器和饰品，生成三个图层的测试工程及 PMP。它用于验证制作链路，不代表完成了“法阵＋光环＋装饰”的美术成品。样本均来自游戏内战士光环，分别调整大小、位置、旋转和颜色；三种外形不同的成品组合仍需在游戏内选材验收。

`verify` 检查包版本、AVFX 结构、节点引用、文件映射、贴图依赖及装备元数据存在性，不替代 Penumbra 导入和游戏画面测试。

## 实现结构

- `AetherVfx.Core`：工程与历史、保留未知块的 AVFX 读写、变换和调色、图层合并、结构校验、IMC 绑定及 PMP 导出。
- `AetherVfx.Plugin`：中文工作台、国服资源目录、画面手柄、原生 VFX 播放、Penumbra 临时资源映射及生命周期管理。
- `AetherVfx.GameData`：归档定位与首块检测、来源关联和带版本的怪物快照更新。
- `AetherVfx.Cli`：本地 SqPack 资源验证、示例生成和独立工程编译。

无新增对外 IPC；仅消费 Penumbra IPC。第三方来源见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。
