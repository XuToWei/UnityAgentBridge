---
doc_type: feature-ff-note
feature: window-controls
date: 2026-06-26
requirement: agent-editor-control
tags: [unity, editor-ui, window, menu, background-mode]
---

## 做了什么
把原本散在编辑器菜单里的开关整合进命令管理器窗口,并给窗口重命名。`Tools/AgentBridge` 下原来的 Start/Stop/Commands/Background 四个菜单项 → 现在只剩一个入口 `Tools/AgentBridge/Window`,桥接启停 + 失焦不节流都在窗口顶部工具条里。

## 改了哪些
- `Unity/Editor/Extensions/AgentBridgeWindow.cs` — 新增(由 ExtensionManagerWindow 重命名而来):类名 `AgentBridgeWindow`、菜单 `Tools/AgentBridge/Window`、标题 "AgentBridge";`OnGUI` 顶部加 `DrawControlBar()`——「桥接运行」Toggle(反映 `AgentBridgeHost.IsRunning`,切换 Start/Stop)+「失焦不节流」Toggle(反映 `BridgeBackgroundMode.IsNoThrottling`,切换 Enable/Restore)。其余(概览/过滤/分组/启停/卸载/状态配色)沿用。
- 删 `Unity/Editor/Extensions/ExtensionManagerWindow.cs`(+meta) — 旧窗口/旧菜单 `Tools/AgentBridge/Commands`。
- 删 `Unity/Editor/Host/AgentBridgeMenu.cs`(+meta) — Start/Stop 菜单项整类移除(改由窗口工具条)。
- `Unity/Editor/Host/BridgeBackgroundMode.cs` — 去掉两个 `[MenuItem]`;新增 `public static bool IsNoThrottling`;`EnableNoThrottling`/`RestoreDefault` 保持 public 供窗口调。
- 文档同步(菜单已变):`Unity/AGENT.md` §1/§7、根 `README.md`(中英)、`Unity/README.md`、`ARCHITECTURE.md` M4 + Extensions 清单——旧菜单路径 `Tools/AgentBridge/Commands`、`/Start`、`/Stop`、`/Enable Background` 统一改为 `Tools/AgentBridge/Window` + 顶部工具条开关。

## 怎么验证的
用户在 Unity 确认:窗口 `Tools/AgentBridge/Window` 打开,顶部两个 Toggle(桥接运行 / 失焦不节流)工作,旧菜单项已消失;编译通过。GF 风格遵守(m_ 字段、花括号、方法块体)。

## 顺手发现
- 无遗留。(本次连带把全仓文档里的过时菜单引用一并修正。)
