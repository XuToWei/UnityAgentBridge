---
doc_type: decision
category: convention
date: 2026-06-26
slug: new-command-requires-test
status: active
area: file-bridge, command-handlers, testing
tags: [convention, testing, editmode, commands, handlers, m5]
---

## 背景

M5 命令集随 `cmd-*` 子 feature 持续增多(目前 17 个命令:自省/只读/场景写/资源/编译自检)。在 `command-test-suite`(2026-06-26)落地前,这些命令**没有任何回归保护**——改一处可能默默破坏另一处,只能靠真机肉眼发现。`command-test-suite` 建立了 EditMode 测试程序集 `AgentBridge.Editor.Tests` + `BridgeTestBase` 基类(临时场景/资产/root 自建自清、Dispatch 助手、禁用名单隔离)+ 每域测试样例后,"给命令写测试"的边际成本已很低。

## 决定

**每个新增命令(实现 `ICommandHandler` 的 handler)必须在 `Unity/Tests/Editor/` 有对应 EditMode 测试**,经 `CommandDispatcher.Dispatch` 覆盖 **正常 + 边界 + 错误** 三类路径。

配套:

1. **以 `Dispatch` 为入口测命令**(集成视角:Request→Response),框架/共享层可直测 API。
2. **测试落本包 `Unity/Tests/Editor/`**,复用 `BridgeTestBase`(自建自清,跑完仓库/EditorPrefs/host 无残留)。
3. **EditMode 不可驱动的真实行为走活体验证 + 单测可测部分**:如 `cmd-compile-check` 的真实"编译+domain reload"无法在 EditMode 可靠触发(会中断测试运行),则单测覆盖映射/读态、真实闭环由用户活体验证(写请求文件驱动真 Unity)。这是该规约的**显式边界**,不是豁免——可测部分仍必须有单测。

## 理由

- **回归保护**:命令是桥接对 AI 的对外契约,改动面广(共享解析层、错误码、commandsVersion);无测试则每次改动都是赌博。
- **成本已降到位**:基类 + 样例就绪后,加一个命令的测试通常几十行,收益远大于成本。
- **可执行**:有统一入口(Dispatch)和基类,规约不是空话,验收阶段可机械核对"测试是否存在"。

## 考虑过的替代方案

- **仅靠真机/活体验证**:否——不可重复回归、易随时间腐烂、改动后无人重跑,等于没有保护。
- **只测框架不测每个命令**:否——命令各自的参数解析/错误码/副作用纪律(dirty/Undo/路径守卫)正是最易回归处,框架测试覆盖不到。

## 影响 / 约束

- 后续所有命令 feature 的 `cs-feat-accept` 验收**必须**核对:新命令有对应 EditMode 测试,覆盖正常+边界+错误。
- 真实行为无法在 EditMode 驱动时(reload/长任务类),在 feature design 里显式声明测试边界(参考 `cmd-compile-check` D6),并仍为可测部分写单测。
- 软规约(convention):偏离不报错,但 review/accept 应纠正。

## 相关文档

- feature `command-test-suite`(2026-06-26,D7 识别本规约 + 建立 `BridgeTestBase`/测试样例)、`cmd-compile-check`(D6,首次按"活体 + 可测单测"边界落地)。
- architecture `ARCHITECTURE.md` §5"新命令必带测试"摘要条目(本 decision 为详细版)。
- decision `commands-category-subdirectory`(命令目录组织,与本规约正交)。
