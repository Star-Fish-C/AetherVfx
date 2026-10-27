# 本机原生二进制核验（2026-10-27）

本轮首次直接分析本机 EXE 和本机 ClientStructs DLL，未启动游戏。所有地址均为 RVA，相对于 PE ImageBase `0x140000000`；不直接用于其他构建。

## 另一台电脑的离线复核

2026-10-27 在另一台电脑核对的 EXE SHA-256 与下方一致。基于 unwind 区间和指令边界扫描 `+0x12C`，获得 714 个候选；相同偏移在不同类型中可表示不同字段，不能将所有候选归为视图分组。结果及完整指令仅保存在忽略目录 `artifacts/native-analysis/office-20261027/field-12c/`。

对已知两条路径继续核查：`3B9E8D` 与 `3BA621` 均调用 `3831A0`；返回后分别执行对象虚表 `+0x120`（`3B9EAE`）和 `+0x128`（`3BA638`）。`3831A0` 包含线程局部上下文访问，`383269` 将一个栈参数字节写入该上下文的 `+0x130`。这是可复现的调用顺序，不足以确定两个虚函数的具体类型、最终渲染目标、透明混合或深度状态。

例如 `3F2D69` 也读取偏移 `+0x12C`，但其基址来自 `[rdi+0x430]`，随后写入步长为 48 字节的记录；目前没有证据证明其为 `3839C8` 使用的线程局部对象，不要仅凭偏移一致连接这两处。进一步扫描 `+0x430` 得到 1055 个候选：`3F8B6F` 把从一个按 `0x500` 字节步长索引的池取得的指针写入对象 `+0x430`；`38FB74` 在检查资源管理器的代次及对象字段后也写入某个管理器条目的对象 `+0x430`。字段来源已缩小到候选构造和赋值路径，但对象的动态类型、两个赋值路径之间的关系仍未证明。

`3CFE08` 申请 `0x470` 字节后在 `3CFE47` 调用 `3F8A80`，后者于 `3F8AC6` 设置候选虚表 `2160360`。该表 `+0x120` 指向 `3E2FB0`，`+0x128` 指向 `3E47E0`；后者的首条指令是三字节 `ret 0`。这为前述两个虚调用提供了一组**条件候选**，但尚未证明实际管理器对象使用这张表，不能把它们命名为粒子通道。按现有直接引用扫描，`40C5A0` 没有直接 CALL/JMP 命中，不能据此排除虚表或其他间接调用。

分析脚本新增 `--output-dir` 与 `--structs-dir`，可在不同目录保存独立运行结果并复用本机导出的结构定义；新增 `--vtable RVA:COUNT` 读取 `.rdata` 中指定候选表。对缺少 unwind 信息的入口，仅解码到首个返回、无条件跳转或中断等终止指令，并标记为 `fallback-terminated`，不会把后续对齐区或其他函数的调用算到同一函数。此边界仍是保守近似，不是完整控制流图。复现本次候选扫描（需本地安装 capstone 和 pefile）：

```powershell
python scripts/Inspect-NativePreview.py '本机游戏目录\game\ffxiv_dx11.exe' `
  --field-offset 12c --output-dir artifacts/native-analysis/office-20261027/field-12c
python scripts/Inspect-NativePreview.py '本机游戏目录\game\ffxiv_dx11.exe' `
  --field-offset 430 --output-dir artifacts/native-analysis/office-20261027/field-430
python scripts/Inspect-NativePreview.py '本机游戏目录\game\ffxiv_dx11.exe' `
  --vtable 2160360:38 --function 3e2fb0 --function 3e47e0 `
  --output-dir artifacts/native-analysis/office-20261027/vtable-validated
```

真实粒子绘制路径和游戏内画面仍未证实，保护门槛保持不变。

客户端版本：`2026.10.06.0000.0000`。EXE SHA-256：`958b78e278ad896493c484eb313ff605ab41edefef74f92b0ee078e69b281bf9`。这比版本字符串更精确；未来启用必须按进程实际模块和文件指纹核验，不能全局硬编码“已兼容”。

## 已证实的控制流

| 对象 | 本机指令位置 | 观察与结论 |
|---|---|---|
| ActorVFX 创建 | `857100`，内部调用 `3944D0` | RCX 路径，RDX/R8 角色地址，XMM3 时长；第 5/6/7 参数分别读取 byte/ushort/byte；返回 RAX。与本项目委托的参数宽度一致。 |
| Actor 返回对象 | `39450B`、`394524`、`394626` | 分配 `0x1E0` 字节，调用 `391900` 初始化后返回该指针。它不能当作本机声明的 `VfxObject` 使用。 |
| 真正的 Scene.VfxObject 创建 | 本机 DLL 的 Create 签名解析至 `7992E0`，分配位于 `7992F4` | 分配 `0x390` 字节；与本机 DLL 的显式大小 912 对应。这是另一条创建路径。 |
| Actor 析构 | 返回对象虚表 `215CF60` 的首项 `40C5A0` | 清理内部对象，`40C654` 按参数低位决定是否释放，`40C65A` 释放大小为 `0x1E0`。移除 hook 可跟踪该不透明对象销毁，但线程及竞态还需核验。 |
| CharaView 初始化 | 虚表 `219F838` 第 1 项 `9CD2D0` | `9CD2E6` 将请求视图索引加 `0xF0`（240）后写入 ClientObjectId；之后删除该角色索引已有对象，再创建载体。Initialize 本身不是“申请空闲槽”的协议。 |
| CharaView Release | 第 2 项 `9CD3E0` | 删除 ClientObjectId 对应对象，将相机表该索引清零，释放 CameraManager 和 Camera，清除回调及状态。必须确认拥有该槽位后才可调用。 |
| CharaView 析构 | 第 0 项 `9D9180` | 只重设虚表并可选释放 `0x318` 字节内存，没有调用 Release。单独 Dtor 不能代替视图清理。 |
| CharaView 渲染状态机 | `9CD4D0..9CD800` | 角色获取、资源准备、创建 CameraManager 与 Camera、注册相机，状态 6 后执行更新；不能仅以状态 6 证明粒子可见。 |

由此确认一个独立于此前 Describe 崩溃位置的错误：旧 `NativeVfxEngine.Center` 将 Actor 返回对象强转成 Scene.VfxObject，写 Position/Rotation 并调用图形对象虚函数。当前已删除整个强转、位置写入与相关 UI 选项；Actor 句柄只允许作为不透明创建／销毁标识。挂点与变换继续由 AVFX 表达。

这不能单独证明 2026-10-26 崩溃的全部根因；没有读取或重新产生旧转储。

## 粒子路由的证据与待确认部分

Actor 创建调用链 `857224 → 89CC90 → 2690F0` 获取角色关联 CameraManager 的索引（末端读取 `+0x10`），将结果传给 `3944D0`。后者在 `3945AC` 调用 `3900E0`，并在 `3945B8..3945D1` 为索引 0–7 编码分组位。

CharaView 在 `9CD718` 初始化自身 CameraManager，传入视图索引；同一初始化函数 `269020` 会更新每视图的引用计数。以上为“ActorVFX 能沿载体相机分组进入离屏通道”提供了具体调查方向，但尚未追踪到最终粒子渲染 pass，也没有画面证据，不能宣布通道支持已通过。

需要继续核对：

1. 已追踪角色模型的相机绑定及 `3900E0` 的实例分组写入（下节）；继续追踪分组在最终粒子 pass 中的读取与筛选。
2. 基类 Initialize 在游戏所有实际使用者中的调用路径；能否在覆盖插件槽位前交还所有权。
3. 共享视图引用计数、相机槽和 ClientObjectManager 占用标志的联合使用规则。不能回退到 DrawObject 空值判断。
4. Framework 调度与原生 UI/销毁线程；所有创建、相机修改、Render 和 Release 都必须有明确的执行上下文。

## 复现工具与边界

### 追加核验：视图编号的消费端

新增分析参数 `--field-offset 2080`，从 unwind 区间解码后筛选具有指定内存位移的指令。这只是候选查找；相同偏移在不同对象中没有相同语义，不能把全部结果都标记为 VFX。结果保存在 `artifacts/native-analysis/vfx-group-readers.json/.txt`。

与已知资源管理器 `+0x1498`、条目步长 `0x88` 相连的读取位置包括 `3B67AE`、`3B9D98`、`3BA2DA`、`3BA53B`。这些读取将实例分组乘 `0x1CC`，加到管理器 `+0xCC`，并放入线程局部上下文 `+0x2E0`。`3B9DC7` 和 `3BA569` 随后把同一编号传给 `383780`。这证明此前写入的视图编号确有消费端，不能再把它视为仅用于登记的字段。

`383780..3839FD` 从所选上下文的 `+0xE8` 和 `+0xA8` 读取两组数据，经 `37E680` 处理，再调用 `389030` 写入各 4 个单元；结合指令形状可推断与矩阵常量有关，尚未给这些内部函数正式命名。末尾 `3839C8..3839DA` 比较并更新另一线程局部对象的 `+0x12C` 为当前分组，同时使 `+0x128` 状态失效。`3B9EAE` 随后进入实例虚表 `+0x120` 的调用。

第二段证据保存在 `vfx-group-context.json/.txt`，复现参数 `--function 383780 --function 3b9c80:3b9fd0 --function 3ba450:3ba820`。这进一步支持按视图处理特效的路线，但尚未识别最终透明／深度状态及离屏输出纹理，因此不是粒子画面验收。下一步是核对 `+0x12C` 分组到渲染目标选择的路径；游戏内调用观察仍等待用户约定时段。

### 追加核验：ClientObjectManager 不是视图租约分配器

`1756410..175666B` 的自动索引分支只扫描 0–239（`1756462` 比较 `0xF0`）。CharaView 对应的 240–247 不会由该分支分配。显式索引分支在 `r8b == 0` 时直接进入构造；没有检查该目标槽的 ObjectKind 或对象指针是否已占用。`r8b != 0` 时还检查相邻槽，随后可将相邻槽标成类型 3 并记录关联索引；这不是普通的“已预订”标记。

删除函数 `1756840..1756957` 会跳过类型 3 槽；对于类型 1 且条目 `+0xC` 非零的情况，也会跳过实际删除。只有实际清理分支才清空条目并扫描关联槽。因此不能把调用 DeleteObject 后返回当作载体已释放，也不能把条目 `+0xC` 简单解释为插件可写的占用锁。

直接调用扫描发现创建入口 19 处、删除入口 43 处调用指令。原始范围保存在 `artifacts/native-analysis/clientobject-callers.json` 和 `.txt`，复现参数为 `--references 1756410 --references 1756840 --function 1756410:1756680 --function 1756840:1756980`。例如 `BC3DAF/BC3E6F` 显式传入索引 248，且不是 CharaView.Initialize 调用；其他入口中也有动态索引。不能据此断言它们会抢占 240–247，但足以说明仅枚举 CharaView 使用者不是完整的角色槽位审计。

这排除了“调用 CreateBattleCharacter(-1) 自动申请一个 CharaView 槽位”的方案。待验证的实现方向是：在已确认线程上同时检查角色槽、相机槽及引用计数，并在相关原生写入入口执行前交还；还需证明入口覆盖和释放顺序，当前未实现或批准该协议。

#### 本机创建调用参数复核

再次对 19 处直接调用的参数准备和分支条件逐一核对：`9CD326` 使用 CharaView 的显式角色索引；`BC3DB4` 显式使用 248；其余 17 处将索引设为 `0xFFFFFFFF`，进入只扫描 0–239 的自动分配分支。其中 `110E4C1` 前有 `cmp eax, 0xA` 和不相等跳转，沿调用分支 `edx = eax - 0xB = -1`；`17BCEBB` 前只有在 `rbx == 0` 时才进入调用分支，故 `edx = rbx - 1 = -1`。其他自动分配调用直接载入 -1 或由已清零寄存器减一得到。

这缩小了已枚举的直接创建调用的风险范围：扫描结果中只有 CharaView 直接创建位置能够用已知视图索引访问 240–247；但它**不证明其他调用不存在**，扫描不覆盖间接调用、无 unwind 元数据的调用或直接写入角色槽、相机表和引用计数的路径，也不证明初始化钩子能安全交还。复现使用 `--references 1756410 --references 1756840 --references 9cd2d0 --function 9cd2d0 --function 9cd3e0`，本机证据保存在忽略目录 `artifacts/native-analysis/office-20261027-slot-callers`。

外部检索也未找到可直接作为本机租约证明的实现：[FireGaze 的槽位观察模型](https://github.com/DCritHitFireIV/FireGaze/blob/72809085fd3d0fc05570dd305fae7a70ba8fc894/src/FireGaze/PortraitPreview/PortraitOwnedSceneSlotCensus.cs)明确将 `ProductionAdmission` 固定为 false，稳定空槽观察不等于预订。它仅作为对照，未复制其代码，也未据此提升兼容状态。

### 追加核验：申请入口调用者与框架线程

分析脚本新增 `--references 9cd2d0 --references 9cd3e0`。它检查直接 CALL/JMP 目标，并从所属 unwind 区间起点解码以验证指令边界；不把数据中偶然出现的 E8/E9 当成调用，也不声称覆盖间接调用或无 unwind 信息的代码。

本机得到 Initialize 的 13 个直接调用位置：`9CF878`、`9D384E`、`9D3993`、`C77B7B`、`C77E3D`、`C794E4`、`C7A2B9`、`CABB49`、`CBE5E9`、`DBBBE9`、`F465B4`、`FFA494`、`10D6BE0`。其中有派生视图初始化，表明只观察某一个 UI 窗口不足以发现所有冲突。未找到 Release 的直接调用不代表无人释放：它是虚函数，间接调用不在此扫描覆盖内。证据保存在 `artifacts/native-analysis/charaview-callers.json` 和 `.txt`。下一步须覆盖虚表调用和 ClientObjectManager 的直接槽位操作，才可判断初始化拦截是否足够。

读取本机 Dalamud 15.0.3.9 的 `Dalamud.Game.Framework` 实现，确认 `HandleFrameworkUpdate` 先执行 `RunFrameworkTick`，随后才调用原游戏 Tick。前者将当前线程绑定到调度器，运行排队任务，再调用 `Update` 事件。因此 Update 是游戏 Tick 钩子的执行线程；这不自动证明其他工作线程不会销毁 VFX。

`RunOnFrameworkThread` 在 `frameworkDestroyed` 已取消时可能直接执行传入回调，即使调用者不在框架线程。原生后端必须在实际执行点检查 `IsInFrameworkUpdateThread` 与 `IsFrameworkUnloading`，不能仅凭调用了调度 API 宣称安全。卸载应在框架仍存活时交还资源，不能到框架销毁后再尝试排队清理。此证据尚不覆盖 UiBuilder.Draw 或 Actor 析构的执行线程，五项门槛保持未批准。

### 追加核验：角色与特效的视图编号连接

`9CEA20..9CEAD8` 是 CharaView 虚表第 11 项：获取角色模型后，以 `view+0x18` 为参数调用模型虚表 `+0xF8`；随后对三个武器模型槽和另一个附属模型执行相同绑定。它不是只把相机登记到全局表。

本机 CharacterBase 静态虚表第 30/31 项分别解析到 `440930` / `440940`。前者返回模型 `+0x348`；后者先对新对象调用虚表 `+0x10`，对旧对象调用 `+0x18`，然后写入 `+0x348`。这些指令证明这里存在引用交接，不能在模型仍引用相机管理对象时直接释放该对象。此前 ActorVFX 创建路径的模型虚表 `+0xF0` 恰好是第 30 项，因而能读回 CharaView 绑定的同一对象。派生模型是否覆盖这些虚函数仍需核对。

`3900E0..390162` 将传入编号加一写入特效资源包装对象 `+0xB0`；满足资源状态及带代次句柄校验后，还写入管理器条目 `+0x2080`。因此默认编号 -1 编码为 0，视图 0–7 编码为 1–8。这里只描述游戏自身指令，插件不读取或写入这些内部资源字段。

本机 `Scene.CameraManager.Instance` 签名解析到全局存储 RVA `28FD978`，与 CharaView 的相机表访问一致；`OffscreenRenderingManager.Instance` 是另一处 `2A957D0`。不能把前者的表偏移 `+0xA0` 当成后者声明偏移不匹配的证据。

对这两个全局地址新增 RIP 相对引用扫描，分别得到 65 和 20 个经 unwind 指令边界确认的引用（本机构建）。其中 `9CD784` 和 `9CEB9A` 都将相机写入 CameraManager 的 `+0xA0` 索引表；`9CD417` 和另一清理函数 `9D3C02` 都会清空该表项。后者在同一函数内还删除角色对象并释放成员，表明仅观察基础 `9CD3E0` Release 不能覆盖已发现的所有相机表清理路径。仍需确认这些函数的动态调用条件、写入者覆盖及线程，不能凭静态结果断言任一槽位可安全持有。

复现：`--rip-target 28fd978 --rip-target 2a957d0 --output-dir artifacts/native-analysis/office-20261027-camera-global`。该扫描先寻找可能的 RIP 位移字节，再从所属 unwind 区间解码并核对位移位置及实际目标，支持位移后带立即数的指令；不覆盖无 unwind 区间、间接计算地址、别名指针或所有控制流，因此数字是候选下界而非完整写入者清单。

复现本节：先导出本机 `Scene.CharacterBase` 和 `Scene.CameraManager`，再运行分析脚本加 `--function 3900e0:390163 --function 9cea20:9cead9`。脚本现已正确解析 `StaticVirtualTable` 的 RIP 相对地址，并输出模型第 30/31 项。以上连接支持继续调查该路线，但尚未证明最终粒子绘制、深度或透明混合正确。

分析库安装在被 Git 忽略的 `artifacts/tools`：capstone 5.0.9、pefile 2024.8.26、dnfile 0.18.0；本轮仅脚本使用前两者。ILSpy 命令行 11.1.0.9782 仅用于读取本机 DLL。工具不修改游戏文件，也不执行游戏代码。

### 追加核验：候选虚函数的直接调用边界

在相同 EXE 指纹下，候选表 `2160360` 的 `+0x120` 目标 `3E2FB0` 的 unwind 区间为 `3E2FB0..3E47CC`，包含对 `3B5D10` 的 8 个直接调用；全 `.text` 中按 unwind 边界验证的 `3B5D10` 直接引用共 362 个。它被许多位置使用，不能仅凭调用把它命名为特效绘制或渲染目标选择。`+0x128` 候选目标 `3E47E0` 在首条 `ret 0` 结束，保守边界为 `3E47E3`；本次扫描没有找到这两个目标的直接引用，但不覆盖虚调用、无 unwind 区间代码或所有控制流。候选对象的动态类型和实际绘制通路仍未证明。

复现：`--references 3e2fb0 --references 3e47e0 --references 3831a0 --function 3e2fb0 --function 3e47e0`；再使用 `--references 3b5d10 --function 3b5d10 --function 37d750 --function 37de30`。本机报告分别位于忽略目录 `artifacts/native-analysis/office-20261027-dispatch` 和 `office-20261027-dispatch-callees`。这些静态结果不是运行时占用证明，也不是画面验收。

进一步核对 TLS 索引：`3B9D0C` 和 `3E3B7C` 的 RIP 相对读取均指向同一全局位置 `2C208C0`。前者通过 `gs:[0x58]` 取得线程局部块，在 `3B9DAA` 向块的 `+0x2E0` 写入按分组计算的上下文指针；后者通过相同 TLS 索引和偏移取出指针，在 `3E3BA0..3E3BA5` 读取该上下文的 `+0x30/+0x34` 并进行浮点相加。两者如果在同一线程且候选虚表实际被调用，可连接此前分组上下文与候选函数的数据使用；这仍不能证明条件成立、字段语义、渲染目标或实际粒子可见。复现使用 `--function 3b9c80:3b9d80 --function 3e2fb0`，证据保存在 `artifacts/native-analysis/office-20261027-tls-link` 和 `office-20261027-dispatch`。

先使用 ILSpy 的 `-t` 导出本机 `CharaView`、`OffscreenRenderingManager` 和 `Scene.VfxObject` 至 `artifacts/native-analysis/structs`，再运行：

```powershell
python scripts/Inspect-NativePreview.py 'G:\FF14\本体\最终幻想XIV\game\ffxiv_dx11.exe' `
  --function 3944d0 --function 89cc90:89ccf0 `
  --function 2690f0:269130 --function 269020:2690f0 `
  --function 9cd4d0:9cd810 --function 1756840:1756980 `
  --function 1756410:1756680 --function 9d9180:9d91b0 `
  --function 9cd180:9cd2d0 --function 26a850:26a8e0
```

`report.json` 保存文件指纹、签名命中、相对引用和虚表；`functions.txt` 保存本机离线反汇编。显式范围可能包含对齐和跳转表；异常处理表也可能将同一逻辑函数拆成多个区间，输出不是自动重建的完整控制流图，必须结合分支人工核对。原生字节、反编译源和游戏资源均留在 artifacts，不提交公开仓库。
