---
doc_type: feature-design
feature: 2026-06-25-agent-protocol-doc
requirement: agent-editor-control
roadmap: file-bridge
roadmap_item: agent-protocol-doc
status: approved
summary: 面向 AI 的桥接驱动协议文档(Unity/AGENT.md)+ 可粘贴的 CLAUDE.md 发现机制元知识片段
tags: [unity, agent, docs, protocol, claude-md]
---

# agent-protocol-doc design

## 0. 术语约定

| 术语 | 定义 |
|---|---|
| 驱动协议文档 | 面向驱动桥接的 AI(主要 Claude Code)的权威使用文档,本 feature 产出 `Unity/AGENT.md` |
| CLAUDE.md 片段 | 可粘贴进集成方项目 `CLAUDE.md` 的**稳定元知识**块(发现规矩 + 最小指引 + 指向完整 doc) |
| 发现流程 | 启动调 `list_commands` 缓存 → 命令请求 → 读响应 → 检查 `commandsVersion` → 变则刷新 |

无新代码术语。grep 防冲突:`AGENT.md` 未存在。

## 1. 决策与约束

### 需求摘要
- **做什么**:给驱动桥接的 Agent 一份权威协议文档 + 一段可粘贴进项目 `CLAUDE.md` 的稳定元知识,让 AI 知道:目录/原子写、请求/响应 schema(含 `commandsVersion`)、错误码、**发现流程**(`list_commands` + 三刷新触发)、最小「发请求→读响应」步骤。
- **为谁**:驱动桥接的 AI Agent(主要 Claude Code);集成该桥接的项目维护者(拿片段贴进自己的 CLAUDE.md)。
- **成功标准**:`Unity/AGENT.md` 覆盖协议全部要素且与实际响应一致;CLAUDE.md 片段自洽、含发现流程三刷新触发、不含命令清单、指向 AGENT.md,一个新 session 的 AI 照它即可正确驱动。
- **明确不做**:
  - 不实现/改任何命令或协议行为(只文档化现状)
  - 不把命令清单写进 CLAUDE.md 片段(遵守 decision `command-discovery-mechanism`)
  - 不做独立 Agent 客户端库(roadmap 明确)

### 复杂度档位
走默认档位(纯文档),无偏离。

### 关键决策
- **D1 文档分两份按受众**:① `Unity/AGENT.md`——面向 AI 的完整驱动协议;② CLAUDE.md 片段——短、稳定、指向 ①。理由:decision 要求 CLAUDE.md 只放稳定元知识,完整协议放别处避免腐烂。
- **D2(假设)不做辅助脚本**:Claude Code 用自带文件工具内联读写即可(本 feature 全链路开发中全程如此),不引入 `send-request` 脚本。若将来非 Claude 的 agent 接入再补。**标为假设,请 review 反驳**。
- **D3 CLAUDE.md 片段内容边界**:只放「发现流程 + 最小发请求/读响应指引 + 指向 AGENT.md」;**不放**命令清单(decision 约束)。

### 前置依赖
bridge-core + cmd-introspection(均 done)。本 feature 仅写文档,不改代码。

## 2. 名词与编排

### 2.1 名词层

**现状**:
- `Unity/README.md`:开发者向(安装 + 协议 + 扩展 + list_commands),已含协议/发现片段,但受众是包开发者,非「驱动桥接的 AI」。
- **无** 面向 AI 的驱动协议文档;**无** 可粘贴的 CLAUDE.md 片段。

**变化**(产出物):

| 产出 | 动作 | 内容 |
|---|---|---|
| `Unity/AGENT.md` | 新增 | 目录布局+原子写、请求/响应 schema(含 `commandsVersion`)、错误码表、发现流程(`list_commands`+三刷新触发)、最小「发请求→读响应」步骤、写新命令简述(指向 README) |
| CLAUDE.md 片段 | 新增(置于 `AGENT.md` 内「复制到你的 CLAUDE.md」代码块) | 发现规矩 + 最小指引 + 指向 AGENT.md;不含命令清单 |
| `Unity/README.md` | 交叉引用 | 在协议/发现处指一句「驱动桥接见 AGENT.md」,去重(只搬不改实质) |

**示例**——CLAUDE.md 片段(成品示意):
```markdown
## Unity Agent Bridge
本工程接入了 Unity Agent Bridge:向 `<root>/requests/` 写请求 JSON(先写 .tmp 再 rename),
轮询 `<root>/responses/{id}.response.json` 读结果。`<root>` 默认 `<工程>/AgentBridge/`。
**先调 `list_commands`** 拿可用命令并缓存;之后任意响应里 `commandsVersion` 与缓存不一致、
或装/卸扩展后、或遇 `UNKNOWN_COMMAND`,就重新 `list_commands`。完整协议见 Packages/.../AGENT.md。
```

### 2.2 编排层

**主流程图**(文档要讲清的 AI 驱动心智模型):
```mermaid
flowchart TD
  R[读 CLAUDE.md 片段] --> L[启动:list_commands → 缓存命令+version]
  L --> S[发命令请求: .tmp→rename 写 requests/]
  S --> P[轮询读 responses/{id}.response.json]
  P --> C{响应 commandsVersion == 缓存?}
  C -- 是 --> Next[继续下条命令]
  C -- 否 --> L
  P -- UNKNOWN_COMMAND --> L
  Next --> S
```

**现状**:Agent 驱动流程无成文版(散落 README + 本 session 实践)。

**变化**:`AGENT.md` 把上图与各步骤成文;CLAUDE.md 片段固化「启动规矩 + 刷新三触发」。

**流程级约束**:
- 文档须与 roadmap 4.6/4.7 + 实际响应**一致**(schema 字段、错误码、version 行为以 bridge-core/cmd-introspection 实测样例为准)。
- CLAUDE.md 片段**不含命令清单**(decision 约束)。
- 刷新三触发(version 变 / 装扩展 / `UNKNOWN_COMMAND`)必须写全。

### 2.3 挂载点清单

本 feature 为文档,**不引入新代码挂入点**。交付物 = `Unity/AGENT.md`(含 CLAUDE.md 片段块)+ README 交叉引用。可卸载性:删 `AGENT.md`、撤回 README 的指引行、集成方移除其 CLAUDE.md 里粘贴的片段即完全移除。

### 2.4 推进策略
```
1. 协议文档骨架:AGENT.md 写 目录/原子写/请求&响应 schema/错误码(从 README + 4.x + 实测样例提炼)
   退出:协议要素齐、与实测响应一致
2. 发现流程章节:list_commands + commandsVersion + 三刷新触发 + 交互流程图
   退出:与 4.7 一致、自洽
3. CLAUDE.md 片段:写可粘贴的稳定元知识块(发现规矩+最小指引+指向 AGENT.md,不含命令清单)
   退出:片段自洽、不含命令清单
4. README 交叉引用 + 校对:README 指向 AGENT.md、去重
   退出:两文档无矛盾、无重复
```

### 2.5 结构健康度与微重构

##### 评估
- compound 检索(目录组织/命名):无文件组织 convention 命中(仅内容类 decision)。
- 文件级(要改):`Unity/README.md`(~90 行,健康)。
- 目录级(落新文件):`Unity/`(根目录现有 package.json/README.md/Editor/),加 `AGENT.md` 不拥挤。

##### 结论:不做(微重构)
纯文档,要改文件健康、目录不挤。

##### 超出范围的观察
无。

## 3. 验收契约

### 关键场景清单
1. **AGENT.md 完整**:`Unity/AGENT.md` 存在且含——目录布局+原子写、请求&响应 schema(含 `commandsVersion`)、错误码表、发现流程(`list_commands`+三刷新触发)、最小「发请求→读响应」步骤。
2. **CLAUDE.md 片段可用**:片段存在,含发现规矩(启动调 list_commands、version 变/装扩展/UNKNOWN_COMMAND 刷新)、不含命令清单、指向 AGENT.md;人工核读「一个新 session 的 AI 照它能正确驱动」。
3. **契约一致**:AGENT.md 的 schema/错误码/version 行为与 roadmap 4.1/4.6/4.7 及实测响应(ping/list_commands 样例)一致。
4. **README 协调**:README 与 AGENT.md 无矛盾,AI 向内容已指向 AGENT.md 不重复。

### 明确不做的反向核对项
- 本 feature **不改任何 `.cs`**(`Unity/Editor/` 下无代码改动)。
- **无新增脚本**(无 `.ps1`/`.sh`/`.py` 辅助脚本;除非 review 推翻 D2)。
- CLAUDE.md 片段**不含**具体命令清单(片段内不出现 ping/list_commands 之外的命令枚举式清单)。

## 4. 与项目级架构文档的关系

acceptance 提炼回 `architecture/ARCHITECTURE.md`:
- **对外交互**:Agent 驱动协议 + 发现流程是系统级对外契约 → 第 3/6 节加一句指向 `Unity/AGENT.md`(描述其存在,非仅链接)。
- CLAUDE.md 片段为集成约定,acceptance 核实后视情况在 attention 或 architecture 记一句「集成方需粘贴 AGENT.md 中的 CLAUDE.md 片段」。

关联:roadmap `file-bridge` 4.6/4.7;decision `command-discovery-mechanism`;requirement `agent-editor-control`。
