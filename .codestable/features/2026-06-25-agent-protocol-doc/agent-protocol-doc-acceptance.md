# agent-protocol-doc 验收报告

> 阶段：阶段 3（验收闭环）
> 验收日期：2026-06-25
> 关联方案 doc：.codestable/features/2026-06-25-agent-protocol-doc/agent-protocol-doc-design.md

## 1. 接口契约核对

文档型 feature,「接口」= 文档结构/片段内容。逐项核读:
- [x] `Unity/AGENT.md` 存在,7 节齐(通讯方式/发命令/错误码/发现/流程图/写命令/CLAUDE.md 片段)。
- [x] 请求&响应 schema 与实测一致:响应含 `commandsVersion`,示例用真实值 `1c5bb3655cdf454c`、`unityVersion: 6000.3.12f1`。
- [x] CLAUDE.md 片段(§7)= 可粘贴块,含发现规矩 + 最小指引 + 指向 AGENT.md。

**名词层"现状 → 变化"核对**:
- [x] 新增 `Unity/AGENT.md`(面向 AI 驱动协议)——已建。
- [x] 新增 CLAUDE.md 片段——内嵌 AGENT.md §7。
- [x] README 交叉引用——「协议/发现」两节收敛为指向 AGENT.md(README:26)。

**流程图核对**(2.2 驱动流程):AGENT.md §5 成文该流程图(读片段→list_commands→发请求→读响应→查 version→刷新)。

## 2. 行为与决策核对

**需求摘要逐项**:
- [x] AGENT.md 覆盖协议全部要素(目录/原子写/schema/错误码/发现/步骤)。
- [x] CLAUDE.md 片段自洽、含三刷新触发、不含命令清单、指向 AGENT.md。

**明确不做逐项核对**(反向核对,grep):
- [x] 不改任何 `.cs`:本次仅 AGENT.md(新)+ README(去重),`Unity/Editor/` 无改动。
- [x] 无新增脚本:`find *.ps1/*.sh/*.py` → 无。
- [x] CLAUDE.md 片段不含命令清单:§7 仅出现 `list_commands`(发现命令)/`UNKNOWN_COMMAND`(错误码),无可用命令枚举。

**关键决策落地**:
- [x] D1 文档分两份按受众:AGENT.md(完整)+ §7 片段(稳定元知识指向 AGENT.md)。
- [x] D2 不做辅助脚本:无脚本(用户 review 确认)。
- [x] D3 片段边界:只放发现流程+最小指引+指向,不含命令清单。

**流程级约束核对**:
- [x] 与契约一致:schema/错误码/version 行为对齐 roadmap 4.1/4.6/4.7 + 实测样例。
- [x] 片段不含命令清单(decision 约束)——见反向核对。
- [x] 三刷新触发(version 变/装扩展/UNKNOWN_COMMAND)写全(AGENT.md §4 + §7)。

**挂载点反向核对**:
- [x] design 第 2.3 节明确「无新代码挂入点」,本 feature 为文档。
- [x] 反向 grep:无 `.cs` 改动,无注册/配置类挂入。
- [x] 拔除沙盘:删 `AGENT.md` + 撤回 README 第 26 行引用即完全移除;集成方移除其 CLAUDE.md 粘贴块。无残留。

## 3. 验收场景核对

| 场景 | 证据来源 | 结果 |
|---|---|---|
| S1 AGENT.md 完整 | 核读 + grep 章节 | [x] 通过——7 节覆盖目录/原子写/schema/错误码/发现/步骤 |
| S2 CLAUDE.md 片段可用 | 核读 §7 | [x] 通过——含发现规矩+三触发、指向 AGENT.md、无命令清单;新 session AI 照此可正确驱动 |
| S3 契约一致 | 对照 4.1/4.6/4.7 + 实测 | [x] 通过——schema/错误码/version 与实测响应一致(commandsVersion 用真实值) |
| S4 README 协调 | 核读 README | [x] 通过——协议/发现已指向 AGENT.md,无重复矛盾 |

**反向核对项**:全部守住(见第 2 节)。

## 4. 术语一致性

- AGENT.md 术语(commandsVersion / list_commands / ICommandSchema / 原子写)与代码/契约一致。
- 无禁用词;CLAUDE.md 片段措辞与 decision `command-discovery-mechanism` 一致。

## 5. 架构归并

已**实际写入** `architecture/ARCHITECTURE.md`：
- [x] 第 3 节 M6:由「文件协议文档(尚未实现)」→「`AGENT.md` 面向 AI 驱动协议(已实现)」。
- [x] 第 6 节关联:加一句「集成方需把 `AGENT.md` 的 CLAUDE.md 片段粘进项目 CLAUDE.md」。

## 6. requirement 回写

design `requirement: agent-editor-control`(current)。本 feature 为文档化现状,未改愿景层用户故事/边界/pitch。
- [x] 结论:req `agent-editor-control` 未变,无需更新。

## 7. roadmap 回写

design `roadmap: file-bridge` / `roadmap_item: agent-protocol-doc`：
- [x] `file-bridge-items.yaml`:`agent-protocol-doc` `in-progress` → `done`,validate 通过。
- [x] `file-bridge-roadmap.md` 第 5 节:`agent-protocol-doc` 标 done。

## 8. attention.md 候选盘点

- [x] 无新候选(纯文档,未暴露新环境/工具坑)。失焦 No Throttling 那条仍待 background-no-throttle 收尾时处理。

## 9. 遗留

- 顺手发现:`Unity/README.md:1,5` 标题仍写「core (bridge-core)…内置 ping」,现含 list_commands,表述轻微过时——不在本 feature 范围,留后续小改/issue。
- 已知限制:无辅助脚本(D2);非 Claude 的 agent 接入若需封装再补。
