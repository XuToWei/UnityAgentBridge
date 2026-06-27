---
doc_type: feature-ff-note
feature: responses-cleanup
date: 2026-06-27
requirement: agent-editor-control
tags: [unity, agent, file-ipc, cleanup, responses]
---

## 做了什么
治理 `responses/` 残留:Unity 端**每个编辑器会话、在生成首个响应前清一次** `responses/`(清上次会话遗留);AI 侧不再负责清理(读完不必删)。根因(跨会话累积 + id 复用读到陈旧响应)由"会话级清一次 + AI 用唯一 id"覆盖。

## 改了哪些
- `Unity/Editor/Channel/FileChannel.cs:124` `ClearResponses()` — 新增,删 responses/ 全部文件(IOException 跳过)。
- `Unity/Editor/Host/AgentBridgeHost.cs` `WriteStamped()` — 写响应前调 `ClearResponsesOncePerSession()`;新增该方法 + `ResponsesClearedKey` 常量,用 `SessionState` 守卫每会话清一次(reload 不重清,避免删掉刚写出未读的响应)。放在写响应路径而非 Start():天然保证本次响应在清理之后落地、不被误清,也消除了 clear 与 ReclaimOrphans 的顺序问题。
- `Unity/AGENT.md` §2 — 清理纪律改为"读完不必删,清理由 Unity 负责";保留"唯一 id 不复用";注明 Unity 每会话生成首响应前清一次。

## 怎么验证的
用户在 Unity 编辑器确认:编译通过;冷启动后首条命令触发清理(旧残留清掉、只剩新响应),会话内后续响应累积不再清,domain reload 不重清。GF 风格遵守。

## 顺手发现(不在本次范围)
- `Unity/AGENT.md` §1/§7 仍引用菜单 `Tools/AgentBridge/Enable Background (No Throttling)`——已被 window-controls(开关整合进窗口)移除,两处引用过时。归 window-controls 的 ff-note 收尾时一并修正。
