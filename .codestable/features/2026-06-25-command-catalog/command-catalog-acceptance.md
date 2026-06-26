---
doc_type: feature-acceptance
feature: 2026-06-25-command-catalog
status: accepted
summary: command-catalog 验收闭环——命令管理器(TypeCache 目录+全局禁用名单 EditorPrefs+窗口重写,合并 command-manager-window);契约/范围/返工全核对;架构重订归并+req 升级;真机经用户决定跳过
tags: [unity, agent, command-manager, typecache, acceptance]
---

# command-catalog 验收报告

> 阶段:阶段 3(验收闭环)
> 验收日期:2026-06-26
> 关联方案 doc:`.codestable/features/2026-06-25-command-catalog/command-catalog-design.md`

验收方式:**代码评审 + grep 契约核对 + design 对照**;**用户决定跳过真机测试**(沿用本会话),第 3 节运行时场景以代码层证据为准、标"真机未测";无 headless 编译。ext 系列真机需含 `Assets/` 的宿主工程。

## 1. 接口契约核对

对照 design 第 2.1 节:

- [x] `CommandCatalog.All()`(`Extensions/CommandCatalog.cs`):`TypeCache.GetTypesDerivedFrom<ICommandHandler>()` → 每类型 `[Command]` 名/描述 + `Type.Assembly` 来源 + `IsBuiltin`(==AgentBridge.Editor)+ `ExtensionId`(LocalRegistry 归属)+ `Enabled`(交叉 CommandToggle.Disabled)→ **一致**。**用 TypeCache 非 GetAll(可见集)**,目录含禁用项 ✓。
- [x] `CommandToggle.SetEnabled/Reapply/Disabled`(`CommandToggle.cs`):EditorPrefs key=`"AgentBridge.DisabledCommands."+Application.dataPath`;SetEnabled 改名单+Reapply;Reapply→`CommandRegistry.SetDisabledCommands` → **一致**。
- [x] `CommandEntry`:Name/Description/Assembly/IsBuiltin/ExtensionId/Enabled,**无 schema** → **一致(D6)**。
- [x] `CommandToggleBootstrap`(`[InitializeOnLoad]`)delayCall→Reapply → **一致(D5)**。
- [x] 窗口 `ExtensionManagerWindow`:`CommandCatalog.All()` 按来源分组 + 逐命令 `CommandToggle.SetEnabled` + 扩展项 `ExtensionInstaller.Uninstall` + 概览/过滤/ScrollView → **一致(D7)**。

**流程图核对**(第 2.2 节):列举(TypeCache→attr→交叉禁用名单/归属)、启停(SetEnabled→EditorPrefs→SetDisabledCommands)、域重载(InitializeOnLoad→Reapply)三子图节点 grep 均有落点。

**偏差**:无未处理偏差。一处合理增量:窗口菜单由 `Tools/AgentBridge/Extensions` 改为 `Tools/AgentBridge/Commands`(契合"命令管理器"定位);经 grep 无其它文档引用旧菜单(AGENT.md/README 仅引用 Start/Stop/Enable Background,不涉及)。

## 2. 行为与决策核对

**需求摘要逐项**:
- [x] TypeCache 列所有命令(内置+扩展)+ 来源 + 启停态:`CommandCatalog.All` 落地。
- [x] 全局禁用名单启停任意命令(含内置)+ 域重载重应用:`CommandToggle` + `CommandToggleBootstrap`。
- [x] 命令管理器窗口:重写完成。

**关键决策落地**:
- [x] D1 TypeCache(非 GetAll 可见集)——目录含禁用项。
- [x] D2 全局禁用名单存 EditorPrefs(工程标识 key)取代 per-ext meta;**ExtensionState 已删**。
- [x] D3 复用 file-bridge 过滤(仅调 SetDisabledCommands,未改 file-bridge)。
- [x] D4 扩展归属复用 LocalRegistry(命令∈manifest.commands→ExtensionId)。
- [x] D5 [InitializeOnLoad]→CommandToggle.Reapply。
- [x] D6 schema 不进 CommandEntry。
- [x] D7 窗口重写(合并 command-manager-window)。

**明确不做逐项**(反向核对):
- [x] 不改 file-bridge 过滤:`CommandRegistry`/`CommandDispatcher`/`ErrorCodes` 本 feature 无 diff(git status 的 M/D 是之前接口合并+ext-enable-disable 的历史改动)。
- [x] 不新写卸载/远程:窗口卸载复用 `ExtensionInstaller.Uninstall`(grep 无新卸载实现);无 HttpClient/远程。
- [x] 不动 InstalledMeta 文件模型本身(仅去启停字段)。
- [x] 不做比命令更细粒度。

**流程级约束**:
- [x] 单一真相源(全局 EditorPrefs 名单;Enabled 派生;Reapply 幂等)/ 复用不改 file-bridge / 来源稳定(Assembly)/ 域重载重应用 —— 逐条代码体现。

**挂载点反向核对(可卸载性)**——第 2.3 节:
- [x] 挂载点逐条有落点:CommandCatalog/CommandToggle+存储/InitializeOnLoad钩子/窗口重写;旧 ExtensionState 已删(grep 仅 CommandToggleBootstrap 一句注释提及历史名)。
- [x] **反向核查**:`CommandCatalog`/`CommandToggle` 引用全在 `Extensions/`(窗口 + bootstrap);无清单外引用。
- [x] **拔除沙盘推演**:删 CommandCatalog/CommandToggle/bootstrap/窗口 → 命令管理层消失;file-bridge 过滤仍在但无人喂禁用名单→全可见。LocalRegistry/Uninstall/manifest(ext-core)+ file-bridge 过滤(ext-enable-disable)留存。无残留耦合。

## 3. 验收场景核对

对照 design 第 3 节。**运行时证据 = 代码评审(真机跳过)**:

- [~] **S1 目录列全(含禁用)**:TypeCache 列举,不经可见集过滤。代码评审;**真机未测**。
- [~] **S2 来源区分**:IsBuiltin/Assembly/ExtensionId 由 Type.Assembly + 归属计算。代码评审;**真机未测**。
- [~] **S3 禁用生效**:SetEnabled→EditorPrefs→SetDisabledCommands→file-bridge 隐藏/拒调。代码评审;**真机未测**。
- [~] **S4 启用恢复**:移出名单→重现。代码评审;**真机未测**。
- [~] **S5 内置可禁**:CommandToggle 按命令名,不限来源。代码评审(逻辑确认);**真机未测**。
- [~] **S6 持久+重应用**:EditorPrefs 持久 + [InitializeOnLoad]→Reapply。代码评审;**真机未测**。
- [x] **S7 旧模型退场**:`ExtensionState` 已删(grep 仅注释);窗口/扫描不读 meta.enabled/disabledCommands(字段已移除)。代码评审确证。
- [~] **S8 窗口=命令浏览器**:按来源分组 + 逐命令启停 + 扩展卸载。代码评审;**真机未测**(EditorWindow)。
- [~] **S9 内置在窗口**:GroupBySource 内置组。代码评审;**真机未测**。

说明:S1–S6/S8/S9 标 `[~]`(代码完成、真机未测,用户同意跳过);S7 代码层可静态确证标 `[x]`。

## 4. 术语一致性

- `CommandCatalog`/`CommandEntry`/`CommandToggle`/`CommandToggleBootstrap` 命名与 design 第 0 节一致 ✓
- TypeCache/EditorPrefs/来源(Assembly)用法一致 ✓
- 防冲突:grep 无既有同名 ✓
- 旧术语 `ExtensionState` 已从代码消失(仅一句历史注释)✓

## 5. 架构归并

已**实际写入** `.codestable/architecture/ARCHITECTURE.md`:
- [x] §3 子系统块**重写**:"扩展管理子系统"→"命令管理器子系统"——文件清单换为 CommandCatalog/CommandToggle/CommandToggleBootstrap/窗口/LocalRegistry(归属+卸载);删 ExtensionState;描述改 TypeCache 发现 + EditorPrefs 全局名单 + 按命令名启停(覆盖内置)✓
- [x] §5 "命令管理器纪律"重写:TypeCache 发现 + 全局 EditorPrefs 名单覆盖内置+扩展 + 窗口=命令浏览器 + per-ext meta 已废 ✓
- [x] 头部最近更新 → 2026-06-26(command-catalog 验收归并)✓
- §5 "命令发现"(禁用从 list_commands/commandsVersion 剔除)条仍准确,无需改。

判据自检:未读 design 的人打开 ARCHITECTURE 现可知"命令管理器用 TypeCache 列所有命令、启停按命令名经全局 EditorPrefs 名单覆盖内置+扩展、窗口在 Tools/AgentBridge/Commands"。归并到位。

## 6. requirement 回写

frontmatter `requirement: extension-management`(current)。本次**改了 pitch/用户故事/边界**(从"本地扩展管理"→"命令管理器,管所有命令含内置)。

- [x] current req **已 update**:`requirements/extension-management.md` 重写 pitch/用户故事/怎么解决/边界为"命令管理器"视角;追加 2026-06-26 变更日志(第 3 次重订、TypeCache、全局 EditorPrefs 名单覆盖内置、窗口重写、合并 command-manager-window、返工)。yaml 校验通过。

## 7. roadmap 回写

frontmatter `roadmap: extension-manager` / `roadmap_item: command-catalog`:
- [x] items.yaml:`command-catalog` `in-progress`→`done`;`command-manager-window` 已 `dropped`(absorbed)。校验通过。
- [x] 主文档 §5 第 5/6 条同步(command-catalog done+合并说明;command-manager-window dropped(absorbed))。
- [x] **所有条目 done/dropped → 主文档 `status: completed`**(extension-manager roadmap 完成)。

## 8. attention.md 候选盘点

- [x] 候选(沿用前序未决同一条):**ext/命令管理器系列真机测试需含 `Assets/` 的宿主 Unity 工程**(本仓库 `Unity/` 是纯 UPM 包)。登记不擅写。

## 9. 遗留

- **真机验证缺位**:S1–S6/S8/S9 全代码评审。尤其需真机验:TypeCache 在本 Unity 版本列举 ICommandHandler、EditorPrefs 工程 key 读写、[InitializeOnLoad] 与 file-bridge 加载时序下 Reapply 生效、窗口交互。
- **InstalledMeta 可能整体无用**:去启停字段后纯本地无人写 `.agentbridge-meta.json`;LocalRegistry 只读 manifest。meta 模型留着但实际空转——建议后续单独 `cs-refactor` 评估删除(design 2.5 已记)。
- **菜单更名**:`Tools/AgentBridge/Extensions`→`Tools/AgentBridge/Commands`;无文档引用旧名,已核。
- 实现阶段无"顺手发现"。
