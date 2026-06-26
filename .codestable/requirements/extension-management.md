---
doc_type: requirement
slug: extension-management
pitch: 让用户在一个窗口里浏览所有命令(内置+扩展)、按需启停任意命令、卸载本地扩展,不改框架
status: current
last_reviewed: 2026-06-26
implemented_by:
  - extension-manager
tags: [unity, agent, command-manager, editor-ui, local]
---

# 命令管理器:浏览 / 启停所有命令 + 管理本地扩展

## 用户故事

- 作为一个用桥接的人,我希望在一个窗口里看到**所有**命令(内置 + 我放进来的扩展),按来源分组、看清哪些生效哪些被禁,而不是只能在 `list_commands` 的扁平输出里翻。
- 作为一个想临时关掉某条命令的人(无论它是内置还是扩展的),我希望点一下就让它从 AI 的 `list_commands` 里消失、调它被拒,但**源码留着、不用等重编译**;想用了再点回来。
- 作为一个放了若干本地扩展的人,我希望能一键卸载某个扩展,而不是去 `Assets/` 里翻文件夹删。

## 为什么需要

file-bridge 的 handler 框架让"写个类打标记就扩展一条命令"成立(编译期扩展),但命令多起来后**怎么看、怎么管**仍是空白:有哪些命令(内置+扩展)看不全、想临时关掉一条只能删文件等重编译。命令管理器补上"可见 + 可控":一个窗口列全、不重编译地启停任意命令、干净卸载扩展。

## 怎么解决

- **发现**:用 Unity `TypeCache` 列出所有 `ICommandHandler`(内置+扩展),按 `Type.Assembly` 区分来源(内置=`AgentBridge.Editor`,扩展=其自带 asmdef);扩展放在 `Assets/AgentBridgeExtensions/{id}/`(用户自放,无远程)。
- **启停(隐藏式,按命令名,覆盖内置+扩展)**:禁用 = 命令仍编译注册,但从 `list_commands`/`commandsVersion` 剔除、被调返 `COMMAND_DISABLED`;**不重编译、不删码**。状态存**全局禁用名单**(EditorPrefs,工程标识 key),domain reload 后重应用。
- **卸载**(仅扩展):删扩展目录 + Refresh。
一个 Editor 窗口(`Tools/AgentBridge/Commands`)按来源分组列所有命令 + 逐命令启停 + 扩展项卸载 + 概览/过滤/滚动。

## 边界

- **不做远程获取/安装**——扩展由用户自行放入(纯本地)。
- 不做策展索引/搜索/市场。
- 不做扩展间依赖解析 / 自动更新 / 签名审查。
- 不做运行时(非编辑器)。
- 启停粒度到"命令"为止,不做按参数/条件的更细禁用。

## 变更日志

- 2026-06-25:backfill 落档(由 `ext-core` 验收触发)。初版含"从 GitHub 安装"。
- 2026-06-25:方向性重订 + 启停落地。去远程改纯本地;ext-enable-disable 落地隐藏式两级启停(per-ext meta)。
- 2026-06-26:**第 3 次重订:命令管理器(command-catalog 落地)**。由"本地扩展管理"升级为"命令管理器"——pitch/用户故事/怎么解决/边界据此重写。① 命令发现改 `TypeCache`(列**所有** ICommandHandler,内置+扩展);② 启停真相源由 per-ext meta 改**全局禁用名单(EditorPrefs,工程标识 key)**,**覆盖内置命令**(原只能禁扩展);③ 窗口重写为命令浏览器(`Tools/AgentBridge/Commands`,按来源分组)。当前交付 = command-catalog(目录+全局启停+窗口,合并了原 command-manager-window)。返工:废 `ExtensionState`/per-ext meta 启停;`ext-manager-ui` dropped(superseded)。注:真机测试经用户决定跳过,运行时以代码评审为证据。
