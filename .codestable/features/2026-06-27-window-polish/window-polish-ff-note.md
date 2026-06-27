---
doc_type: feature-ff-note
feature: window-polish
date: 2026-06-27
requirement: agent-editor-control
tags: [unity, editor-ui, window, commands, grouping, sorting]
---

## 做了什么
对命令管理器窗口(AgentBridgeWindow)做了一整轮 UI 打磨 + 命令分组/可禁用能力:按功能分组显示、表头点击排序、批量启停、必须命令锁定、布局与字体优化、菜单入口移到 Unity `Window` 菜单。配套给 `ICommandHandler` 增了两个声明性成员 `Group`(功能分组)与 `CanDisable`(能否禁用)。

## 改了哪些
- `Unity/Editor/Dispatch/ICommandHandler.cs` — 新增 `string Group`(功能分组)+ `bool CanDisable`(能否禁用);20 个 handler(17 内置 + 3 测试)各实现:Group=Meta/Inspection/Mutation/Assets/Compilation/Test;ping、list_commands 的 `CanDisable=false`(协议刚需,不可禁)。
- `CommandRegistry.cs` — Rebuild 收集 `CanDisable=false` 命令 + `CanDisable(name)` 查询(不进 list_commands/commandsVersion)。
- `CommandToggle.cs` — 删写死的 essential 名单,改由 `CommandRegistry.CanDisable` 驱动:`SetEnabled` 拒禁不可禁命令、`Disabled()` 过滤之、`IsEssential = !CanDisable`。
- `CommandEntry.cs` / `CommandCatalog.cs` — entry 带 Group/CanDisable,catalog 从 handler 读。
- `Unity/Editor/Extensions/AgentBridgeWindow.cs`(主体重写):菜单 `Window/Agent Bridge Window`;两区块标题(桥接控制 / 命令)+ 分隔线;控制条复选框「启用桥接」「后台运行」+ 刷新挨其右;命令计数行;搜索行;分组**单选**下拉(顺序取自收集到的 Group 名,不写死)+「全部启用/全部禁用」批量(对当前筛选生效、自动跳过不可禁);列表表头点击排序(点「启用」切 启用在前▲/禁用在前▼,点「命令」切名称升▲/降▼)+ 列对齐 + 斑马纹 + 默认字体(仅区块标题加粗);不可禁命令勾选框锁定;扩展卸载底栏;`minSize=(440,320)`。
- `CommandManagerTests.cs` — 加 `NonDisablableCommandsCannotBeDisabled`(ping+list_commands 拒禁、普通可禁)+ catalog 断言 Group/CanDisable。
- 文档同步菜单路径:`AGENT.md` / 根 `README.md`(中英)/ `Unity/README.md` / `ARCHITECTURE.md` → `Window/Agent Bridge Window`;ARCHITECTURE M4 + Extensions 清单更新为 AgentBridgeWindow。

## 怎么验证的
EditMode 单测覆盖 Group/CanDisable/必须命令(CommandManagerTests)。窗口为纯 UI,需用户在 Unity 编译 + 目视确认(本轮多次因改名未改全出过编译错,均已修正,grep 确认无残留)。**用户尚未跑最终编译/Test Runner——待验证。**

## 顺手发现(不在本次范围)
- 整轮(命令测试套件 / Command 特性合并 / GF 全量风格 / window-controls / entityid / TypeFinder / cmd-compile-check / responses-cleanup / 本轮 window-polish)全程零提交,强烈建议分轮 scoped-commit 固化。
