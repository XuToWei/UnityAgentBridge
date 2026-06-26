---
doc_type: feature-acceptance
feature: 2026-06-25-ext-enable-disable
status: accepted
summary: ext-enable-disable 验收闭环——隐藏式两级启停(扩展级+命令级)+ file-bridge 禁用名单过滤;契约/范围守护全核对;架构归并(含扩展子系统纯本地纠正)+ req 刷新;真机经用户决定跳过
tags: [unity, agent, extension, enable-disable, acceptance]
---

# ext-enable-disable 验收报告

> 阶段:阶段 3(验收闭环)
> 验收日期:2026-06-25
> 关联方案 doc:`.codestable/features/2026-06-25-ext-enable-disable/ext-enable-disable-design.md`

验收方式:**代码评审 + grep 契约核对 + design 对照**;**用户决定跳过真机测试**(沿用本会话节奏),第 3 节运行时场景以代码层证据为准、标"真机未测";无 headless 编译。ext 系列真机需含 `Assets/` 的宿主工程(见第 8/9 节)。

## 1. 接口契约核对

对照 design 第 2.1 节:

**接口示例逐项核对**:
- [x] `ExtensionState.SetEnabled(id,false)`(`Extensions/ExtensionState.cs`):改 `meta.enabled`→`WriteMeta`→`Reapply`。`Reapply` 按 D1 算并集 → `CommandRegistry.SetDisabledCommands` → **一致**。
- [x] `ExtensionState.SetCommandEnabled(id,cmd,false)`:增删 `meta.disabledCommands`(去重)→ WriteMeta → Reapply → **一致**。
- [x] file-bridge:`CommandRegistry.SetDisabledCommands/IsDisabled`,`GetAll()`/`Version` 经 `VisibleInfos()` 剔除禁用;`CommandDispatcher` 先 `TryGet` 再 `IsDisabled`→`COMMAND_DISABLED` → **一致**。
- [x] domain reload:`ExtensionStateBootstrap`(`[InitializeOnLoad]`)`delayCall += ExtensionState.Reapply` → **一致**。

**名词层"现状 → 变化"逐项核对**:
- [x] `ErrorCodes.CommandDisabled` 新增 ✓
- [x] `CommandRegistry`:`Disabled` 集 + `SetDisabledCommands`/`IsDisabled`/`VisibleInfos`;`GetAll`+Rebuild 用可见集 ✓
- [x] `CommandDispatcher` 禁用分支 ✓
- [x] `InstalledMeta.DisabledCommands` + `InstalledExtension.DisabledCommands` + `LocalRegistry.Scan` 填充 ✓
- [x] `ExtensionState`(3 方法)+ `ExtensionStateBootstrap` ✓
- [x] `ExtensionManagerWindow`:foldout + 扩展级启停 + 逐命令启停 ✓

**流程图核对**(第 2.2 节 mermaid):
- [x] 用户操作链(SetEnabled/SetCommandEnabled→写 meta→重算并集→SetDisabledCommands→重算 version)、运行期(list_commands 过滤 / dispatch COMMAND_DISABLED)、域重载(InitializeOnLoad→Reapply)三子图节点均有代码落点(grep 确认)。

**偏差**:无未处理偏差。一处 design 外的**机械补充**(已透明):`InstalledExtension.DisabledCommands` + `LocalRegistry.Scan` 填充——design 2.1 列了 `InstalledMeta.DisabledCommands` 与窗口逐命令 UI,要渲染逐命令状态必须把 disabledCommands 经 Scan 带到窗口,属命令级粒度的必然机械后果,与意图一致。

## 2. 行为与决策核对

**需求摘要逐项验证**:
- [x] 两级启停:`SetEnabled`(扩展级)+ `SetCommandEnabled`(命令级)均落地。
- [x] 禁用即从 list_commands 消失 + 调用被拒 + 不重编译:`GetAll` 可见集过滤 + dispatch `COMMAND_DISABLED` + 启停路径无 Refresh/删码(grep)。

**明确不做逐项核对**(第 3 节反向核对项):
- [x] 不删码/不重编译/不改名:`ExtensionState`/`Bootstrap` grep 无 `File.Delete`/`File.Move`/`.cs.disabled`/`AssetDatabase.Refresh`(exit 1)。
- [x] 不引入远程/安装:本 feature 未碰 `InstallFromGitHub`/下载逻辑。
- [x] 不做比命令更细粒度:启停单位是扩展/命令名。

**关键决策落地**:
- [x] D1 两级算法:`Reapply` 实现 `!Enabled→全部命令 / else→DisabledCommands` 取并集。
- [x] D2 file-bridge 禁用名单:`SetDisabledCommands`/`IsDisabled` + dispatch 分支。
- [x] D3 commandsVersion 统一:`GetAll()` 与 `_version` 均基于 `VisibleInfos()`(Rebuild 与 SetDisabledCommands 都按可见集算 version)。
- [x] D4 dispatch 仍找得到 handler:`TryGet` 不剔除禁用,`IsDisabled` 判定返 `COMMAND_DISABLED`(非 UNKNOWN_COMMAND)。
- [x] D5 域重载重应用:`ExtensionStateBootstrap [InitializeOnLoad]` delayCall→Reapply。
- [x] D6 窗口:每扩展行 foldout + 扩展级 Enable/Disable + 逐命令 Enable/Disable。

**流程级约束核对**:
- [x] 隐藏≠卸载(TryGet 仍有 handler)/ 真相源单一(meta 派生,Reapply 幂等)/ 缓存一致(list_commands 与 version 同可见集)/ 错误语义(禁用→COMMAND_DISABLED,未注册→UNKNOWN_COMMAND)。

**挂载点反向核对(可卸载性)**——对照第 2.3 节:
- [x] 挂载点逐条有落点:CommandRegistry 禁用集/可见集过滤、Dispatcher 禁用分支、GetAll 可见集、ExtensionState API、InitializeOnLoad 钩子、窗口启停 UI(grep 全部命中)。
- [x] **反向核查**:`SetDisabledCommands`/`IsDisabled`/`CommandDisabled` 引用全部落在 Dispatch/Protocol/Extensions 内,无清单外引用。
- [x] **拔除沙盘推演**:删 `ExtensionState`+`Bootstrap`、回退 `CommandRegistry`/`Dispatcher`/`GetAll` 的禁用分支、删 `COMMAND_DISABLED`、窗口去启停按钮 → 回到"凡注册即可见"。`meta` 的 `enabled`/`disabledCommands` 字段是数据,留着无害。无残留耦合。

## 3. 验收场景核对

对照 design 第 3 节。**运行时证据 = 代码评审(真机经用户决定跳过)**:

- [~] **S1 扩展级禁用**:`SetEnabled(id,false)`→Reapply 加全部命令→GetAll/Version 剔除。**真机未测**。
- [~] **S2 命令级禁用**:`SetCommandEnabled` 加单命令→仅该命令剔除。**真机未测**。
- [~] **S3 禁用拒调**:dispatch `IsDisabled`→`COMMAND_DISABLED`。**真机未测**。
- [~] **S4 启用恢复**:置 true→移出名单→重现+version 再变。**真机未测**。
- [~] **S5 优先级**:Reapply 中 `!Enabled` 分支优先(整扩展),else 才看 disabledCommands。**真机未测**(逻辑评审确认)。
- [~] **S6 持久+重应用**:meta 持久 + `[InitializeOnLoad]`→Reapply。**真机未测**。
- [~] **S7 真相源**:Reapply 全程从 `LocalRegistry.Scan`(读 meta)重建,幂等。**真机未测**。
- [~] **S8 窗口**:foldout + 两级按钮调 ExtensionState + Rescan。**真机未测**(EditorWindow)。
- [~] **S9 多扩展并存**:禁用名单是跨扩展并集,互不影响。**真机未测**(逻辑评审确认)。

说明:S1–S9 全标 `[~]`(代码完成、真机未测,用户同意跳过);代码层静态确证(契约一致、范围守护、错误码链路)在第 1/2/4 节已勾选。

## 4. 术语一致性

对照 design 第 0 节 grep:
- `ExtensionState`/`COMMAND_DISABLED`/`SetDisabledCommands`/`IsDisabled`/`DisabledCommands` 命名一致 ✓
- `meta.enabled`/`meta.disabledCommands` 与 roadmap 4.2 一致 ✓
- 防冲突:grep 无既有同名 ✓
无不一致。

## 5. 架构归并

已**实际写入** `.codestable/architecture/ARCHITECTURE.md`:
- [x] **file-bridge 约束修订**:§4(命令发现)补"禁用命令从 list_commands+commandsVersion 剔除";§5 错误语义加 `COMMAND_DISABLED` ✓
- [x] **扩展子系统重写**:§3"扩展管理子系统"小节改为**纯本地**(去掉远程安装流程)+ 加 `ExtensionState`/`ExtensionStateBootstrap` + 启停(隐藏式两级)说明;`ExtensionInstaller` 注明远程安装作废待清理 ✓
- [x] **扩展纪律重写**:§5"扩展管理纪律"由"GitHub zip 安装"改为"纯本地 + 隐藏式启停"✓
- [x] 头部最近更新 → ext-enable-disable + 纯本地重订 ✓

判据自检:未读 design 的人打开 ARCHITECTURE.md 现可知"扩展是本地源码包、可两级隐藏式启停(禁用从 list_commands/version 剔除+COMMAND_DISABLED)、远程安装已废、命令注册仍归 M3"。归并到位。

## 6. requirement 回写

frontmatter `requirement: extension-management`(current,本会话早些 backfill,含 GitHub 安装,已被纯本地重订作废)。

- [x] current req 且本次**改了 pitch/用户故事/边界/怎么解决** → **已 update**:`requirements/extension-management.md` 重写为纯本地 + 两级启停;pitch 改"管理本地命令扩展(扫描/启停/卸载)";用户故事换为启停+卸载视角;边界去远程加启停;追加变更日志记录方向性重订 + 启停落地(保留 backfill 溯源)。yaml 校验通过。

## 7. roadmap 回写

frontmatter `roadmap: extension-manager` / `roadmap_item: ext-enable-disable`:
- [x] items.yaml:`ext-enable-disable` `in-progress`→`done`(feature 核对一致),校验通过。
- [x] 主文档 §5 第 2 条同步:标 ✅ done、状态填实、备注补 file-bridge 改动落地细节。

## 8. attention.md 候选盘点

- [x] **有候选**(沿用 ext-core 未决的同一条,登记不擅写):
  - 候选 1:**ext 系列真机测试需含 `Assets/` 的宿主 Unity 工程**(本仓库 `Unity/` 是纯 UPM 包)。已第二次 ext feature 撞到。建议放 attention.md「运行与本地起服务」节。

## 9. 遗留

- **真机验证缺位**:S1–S9 全代码评审。尤其需真机验证的:`[InitializeOnLoad]` 与 file-bridge 宿主加载时序下 Reapply 是否在首个 list_commands 前生效;disabled-set 在 domain reload 后确实重建;dispatch COMMAND_DISABLED 实际返回。
- **ext-core 远程死代码仍在**(`InstallFromGitHub`/zip 下载/窗口粘 URL 控件):本 feature 按 design 2.5 边界**只加启停未删它**,架构已注明作废。**建议下一步走 `cs-refactor` 清理**,使代码与纯本地口径一致。
- 已知限制:Reapply 每次 `LocalRegistry.Scan` 全量重算禁用名单(扩展少无虞;极多扩展时可优化为增量)。
- 实现阶段无"顺手发现"。
