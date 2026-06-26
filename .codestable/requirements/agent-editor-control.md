---
doc_type: requirement
slug: agent-editor-control
pitch: 让 AI Agent 用文件就能指挥 Unity 编辑器干活,不用架网络服务
status: current
last_reviewed: 2026-06-24
implemented_by:
  - file-bridge
tags: [unity, agent, editor, automation, file-ipc]
---

# AI Agent 通过文件桥接控制 Unity 编辑器

## 用户故事

- 作为一个想让 AI 帮我改场景的开发者,我希望 AI 写个文件就能让 Unity 执行操作并把结果还给它,而不是先让我配一套 HTTP 服务、开端口、处理防火墙。
- 作为一个被编辑器频繁重编译打断的人,我希望 AI 和 Unity 的通讯在 domain reload 后能自动恢复、被打断的请求有明确反馈,而不是连接一断就得手动重连重来。
- 作为一个想给桥接加自己专属命令的人,我希望写一个类、打个标记就能让 AI 调到它,而不是去改桥接框架本身。

## 为什么需要

AI Agent 要操作 Unity 编辑器,最直接的想法是架个 HTTP 服务两边通讯。但编辑器环境对长连接很不友好:端口可能被占、有防火墙、每次脚本重编译(domain reload)都会重置进程内状态把连接打断。这些工程摩擦在真正想让 AI 自动化编辑器时反复消耗精力。

## 怎么解决

用文件做通讯介质:Agent 把请求写成 JSON 文件,Unity 编辑器轮询发现后在主线程执行,把结果写回响应 JSON 文件,Agent 读回。文件天然规避端口/防火墙/连接生命周期问题;轮询 + domain reload 后自动重挂,让通讯跨重编译稳定;被打断的请求会收到明确的中断反馈。命令通过"写 handler 类 + 打标记自动注册"扩展,加能力不动框架。

## 边界

- 不主动操作编辑器——Agent 不发请求就什么都不做。
- 不走网络——纯本地文件通讯,不跨机、不做远程。
- 不做安全隔离/认证——假设单 Agent、本地受信环境。
- 起步只覆盖 Unity 编辑器侧,不含运行时 / Play mode 控制。

## 变更日志

- 2026-06-24:backfill 落档。当前交付(roadmap file-bridge 的 bridge-core)= 文件通讯传输层 + handler 框架 + 内置 `ping`。场景查询、对象读写、菜单/资源操作、execute_csharp 等命令能力为 file-bridge 后续子 feature;从 GitHub 安装/搜索扩展为 extension-manager roadmap。
- 2026-06-25:交付进度——`cmd-introspection`(`list_commands` + 命令发现)、`agent-protocol-doc`(Unity/AGENT.md)、`cmd-inspection`(只读查询 `get_hierarchy`/`get_object`/`get_selection`/`list_assets` + 共享对象引用解析)已落地。用户视角(用户故事/边界/pitch)未变,仍为愿景内的子能力推进;对象读写(mutation)、资源、execute_csharp 仍待后续 feature。
- 2026-06-25:交付进度——`cmd-mutation`(写操作 `set_property`/`invoke_menu`/`create_object`/`delete_object`,记录 Undo、标 dirty 不自动保存)已落地。至此"读现场 + 改场景"闭环成形。用户视角未变(pitch「指挥编辑器干活」本就含写);资源级操作(cmd-assets)、execute_csharp(cmd-csharp)仍待后续。注:本 feature 真机测试经用户决定跳过,运行时行为以代码评审为证据。
- 2026-06-25:交付进度——`cmd-assets`(资源操作 `import_asset`/`create_asset`/`move_asset`/`delete_asset`/`refresh`,写路径限 Assets/ 下、删除走回收站、即时落盘)已落地。至此读/场景写/资源三类能力齐备,仅剩 `execute_csharp`(cmd-csharp)。用户视角未变。注:本 feature 真机测试经用户决定跳过,运行时以代码评审为证据。
- 2026-06-26:交付进度——`cmd-compile-check`(`recompile` 触发脚本重编译 + `get_compile_result` 读编译错误/警告,因 domain reload 做异步两步、编译结果经 SessionState 跨 reload 收集)已落地。新增"AI 写代码后自检编译报错并修复"子能力。用户视角(pitch/边界)未变——仍是"指挥编辑器干活"愿景内的子能力。注:真实编译+reload 闭环以活体验证为证据(EditMode 不可驱动 reload),映射/读态有单测。
