---
doc_type: decision
category: architecture
date: 2026-06-24
slug: command-discovery-mechanism
status: active
area: file-bridge, agent-protocol
tags: [discovery, introspection, list-commands, cache, protocol]
---

## 背景

AI Agent 通过文件桥接调用 Unity 命令时,**无法知道有哪些命令可用、各自参数是什么**。命令在代码里用 `[Command]` 注册,且 extension-manager 允许从 GitHub **运行时**装扩展引入新命令。需要一套机制让 AI 准确发现当前可用命令,且能感知命令集变化(尤其装扩展后)。

## 决定

发现机制由三层组成:

1. **入口(静态,放 CLAUDE.md)**:只写**稳定元知识**——文件协议怎么用、根目录在哪、以及规矩「启动调一次 `list_commands` 缓存;响应里 `commandsVersion` 变了就刷新」。**不把命令清单本身写进 CLAUDE.md**。
2. **清单(动态,`list_commands` 元命令)**:返回当前所有命令的 名字 + 描述 + 参数 schema + `commandsVersion`。数据来自 `CommandRegistry` 反射注册表。
3. **失效信号(`commandsVersion`,内容 hash)**:对「排序后的命令名 + 各自参数 schema/描述」算短 hash。**每条响应都盖上当前 `commandsVersion`**;AI 缓存它,任何响应里 version ≠ 缓存即刷新 `list_commands`。

**调用频率约定**:`list_commands` 每 session 启动调一次并缓存;普通命令调用用缓存不重调;仅在 ① `commandsVersion` 不一致 ② 装/卸/启停扩展后 ③ 某命令返回 `UNKNOWN_COMMAND` 时刷新。

## 理由

- **纯静态文档会腐烂、且覆盖不了运行时装的扩展**——命令是代码/扩展定义的真相,手写文档必然漂移。
- **纯运行时获取**准确但 AI 需要「先调 list_commands」的入口提示,且要一种不靠每命令往返就能察觉过期的方式。
- **hash 盖在响应上**是 ETag 式缓存校验:happy path 零额外往返(信号搭在已有响应上),且能覆盖命令的**增/删/改**全部三种变化——这是单靠 `UNKNOWN_COMMAND` 兜底做不到的(它只能发现删/改名)。
- **hash 而非自增号**:内容寻址,同一命令集永远同一 hash,跨编辑器重启/换机器/domain reload 都稳定,无需持久化计数器。

## 考虑过的替代方案

- **纯 CLAUDE.md 静态命令清单**:否。会腐烂;extension-manager 运行时装的扩展根本无法预先写入;多工程/多扩展组合下一份文档无法反映各自实际命令集。
- **纯 list_commands、每条命令前都调一次**:否。白白翻倍每次操作的往返延迟,并把同样清单反复灌进 AI 上下文。
- **自增版本号代替 hash**:否。需持久化,跨重启/换机易错乱;hash 内容寻址天然稳定。
- **请求每条回传 version、由服务端算 stale 标志**:否,冗余。响应盖 `commandsVersion` + AI 端比对已足够,请求侧无需改。

## 后果

- **file-bridge 4.1 协议**:响应信封新增 `commandsVersion` 字段 → 需 `cs-roadmap update`。
- **file-bridge 4.3 框架**:`CommandRegistry` 注册后计算命令集 hash;`ICommandHandler`/`[Command]` 增加自描述(description + 可选参数 schema)→ 需 `cs-roadmap update`,并新增 `cmd-introspection` 子 feature 实现 `list_commands`。
- **所有命令 feature**(inspection/mutation/assets/csharp)今后**必须**给命令带描述(+尽量给参数 schema),以便被 `list_commands` 暴露给 AI。
- **agent-protocol-doc feature**:负责把入口元知识落地到 CLAUDE.md。
- **约束**:禁止把完整命令清单写进 CLAUDE.md(只放发现机制元知识),避免静态文档腐烂。

## 相关文档

- roadmap `file-bridge`(4.1 / 4.3 契约将被更新)
- roadmap `extension-manager`(运行时装扩展 → 触发命令集变化与刷新)
- requirement `agent-editor-control`
