---
doc_type: feature-ff-note
feature: background-no-throttle
date: 2026-06-24
requirement: agent-editor-control
tags: [unity, editor, background, no-throttle, host]
---

## 做了什么

加了「失焦后台运行」开关。Unity 编辑器失焦时会节流主循环,导致 `EditorApplication.update` 近乎停摆、桥接不再轮询(失焦时 Agent 发的请求得不到响应)。本开关通过把编辑器 Interaction Mode 设为 No Throttling(idle=0),让编辑器失焦也持续运行,使 Agent 在用户不盯着 Unity 时也能驱动桥接。属 file-bridge 能力(requirement agent-editor-control)的体验增强,非 roadmap 计划内子 feature。

## 改了哪些

- `Unity/Editor/Host/BridgeBackgroundMode.cs`(新增)— 两个菜单:`Tools/AgentBridge/Enable Background (No Throttling)` 和 `Restore Default Throttling`。设 EditorPrefs `ApplicationIdleTime`/`InteractionMode` + 反射调用内部 `EditorApplication.UpdateInteractionModeSettings()`(枚举/方法均 internal,带 fallback:NoThrottling=1 / Default=0)。

## 怎么验证的

真机 Unity 6000.3.12f1:点 Enable Background 菜单(Console 打印 `Interaction Mode = No Throttling`,无反射错)→ 切走让 Unity 失焦 → 向 requests/ 写 ping(bg3)→ **失焦状态下也秒回 pong**。对比:未启用时同样失焦发 ping(bg1)会一直卡在 requests/ 直到聚焦才被消费。开关生效确认。

## 顺手发现

- Interaction Mode 是**全局 EditorPrefs**(影响本机该 Unity 版本所有工程、持久),故做成显式菜单开关而非包加载强制;No Throttling 会升高 idle CPU(Windows 上可接受;Linux 有近 100% CPU 的已知 issue)。已在代码注释说明,不阻塞。
