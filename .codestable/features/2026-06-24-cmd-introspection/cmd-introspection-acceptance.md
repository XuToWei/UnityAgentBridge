# cmd-introspection 验收报告

> 阶段：阶段 3（验收闭环）
> 验收日期：2026-06-25
> 关联方案 doc：.codestable/features/2026-06-24-cmd-introspection/cmd-introspection-design.md
> 真机环境：Unity 6000.3.12f1,工程 G:\GitHub\X\Unity

## 1. 接口契约核对

**接口示例逐项核对**(真机实测):
- [x] `list_commands` 返回 `{commands:[{command,description,paramsSchema}], commandsVersion}`(lc1/lc2)——符合 4.7。
- [x] 响应顶层 `commandsVersion` 字段(lc1/lc2/pg1)——符合 4.1。
- [x] `[Command(name, description)]` 描述出现在清单;`ICommandSchema.GetParamsSchema()` 的 schema 出现在 echo 的 `paramsSchema`(lc2)——符合 4.3。

**名词层"现状 → 变化"逐项核对**:
- [x] `Response.CommandsVersion`:新增字段,实测出现在每条响应。
- [x] `CommandAttribute.Description`:实测描述出现在 list_commands。
- [x] `ICommandSchema`/`CommandInfo`:实测 echo 的 schema 正确投影。
- [x] `CommandRegistry.Version`/`GetAll()`:实测 list_commands 拿到清单 + hash。
- [x] `PingHandler` 补描述:实测 ping 描述「连通性测试,返回 pong 与 Unity 版本」。

**流程图核对**:Rebuild 收集 CommandInfo→算 Version、list_commands 读 GetAll+Version、Host 盖戳——均有代码落点且实测生效。

## 2. 行为与决策核对

**需求摘要逐项验证**:
- [x] AI 发现命令:lc1 返回 ping/list_commands 清单。
- [x] 感知变化:加 echo → version 变(4edbd7f0c7cc0579);删 echo → version 回 1c5bb3655cdf454c。

**明确不做逐项核对**(反向核对):
- [x] 除 list_commands 外不新增业务命令:`grep \[Command\(` 仅 ping + list_commands(echo 为临时测试,已删)。
- [x] commandsVersion 不用 GetHashCode:`grep GetHashCode` 仅注释,无调用(改用 MD5 over JSON)。
- [x] 不写 CLAUDE.md 内容:未碰 agent-protocol-doc 范围。

**关键决策落地**:
- [x] D1 确定性 hash:实测 echo 增删后 version 精确回到原值(内容寻址,不随重启变)。实现用 `MD5(JsonConvert.SerializeObject(排序后 Infos))`(见实现自决)。
- [x] D2 盖戳单点:`AgentBridgeHost.WriteStamped` 覆盖 Tick + ReclaimOrphans;实测 ping/list_commands 顶层均带 version。
- [x] D3 自描述分两件:description 走特性(ping/list_commands 都有)、schema 走可选接口(echo 实现→非 null,ping/list_commands 未实现→null)。
- [x] D4 list_commands 是普通 handler:实测它出现在自己的清单里。

**流程级约束核对**:
- [x] 每条响应带 version(ok 路径实测;error/INTERRUPTED 路径经同一 WriteStamped 单点,代码保证)。
- [x] 向后兼容:ping 仍 `ok`+pong(pg1);description/schema 缺省为 null,不报错。

**挂载点反向核对**:
- [x] M1 `list_commands` 命令注册 → `ListCommandsHandler.cs`(`[Command("list_commands")]`),实测可调。
- [x] 反向 grep:`[Command(` 仅 ping/list_commands,无清单外注册。
- [x] 拔除沙盘:删 `ListCommandsHandler.cs` → 发现入口消失(其余 version 字段/Registry.Version 为内部增强,属 implement 改动)。

## 3. 验收场景核对

| 场景 | 证据 | 结果 |
|---|---|---|
| S1 list_commands 清单 | 真机 lc1 | [x] 通过——ping/list_commands 各带 description,commandsVersion=1c5bb3655cdf454c |
| S2 每条响应带 version | 真机 lc1/pg1 | [x] 通过——顶层 commandsVersion = result 内同值 |
| S3 可选 schema | 真机 lc2 | [x] 通过——echo(实现 ICommandSchema)paramsSchema 为完整 schema 对象;ping/list_commands 为 null |
| S4 version 稳定+敏感 | 真机 lc1/lc2/lc3 | [x] 通过——加 echo→4edbd7f0c7cc0579;删 echo→回 1c5bb3655cdf454c(内容寻址) |
| S5 向后兼容 | 真机 pg1 | [x] 通过——ping 仍 ok+pong,且带 version |

**反向核对项**:全部守住(见第 2 节)。

## 4. 术语一致性

- `namespace AgentBridge`:全部新增/修改文件一致。
- 术语 list_commands / commandsVersion / ICommandSchema / CommandInfo:命名与 design 第 0 节一致,grep 无冲突。

## 5. 架构归并

已**实际写入** `architecture/ARCHITECTURE.md`：
- [x] 第 3 节模块索引:M5 增 `list_commands`;主循环注明响应盖 commandsVersion。
- [x] 第 4 节关键决定:发现机制条目去掉「尚待实现」,标为已落地。
- [x] 第 5 节已知约束:增「每条响应带 commandsVersion(命令集内容 hash);命令 feature 必带描述」。

## 6. requirement 回写

design `requirement: agent-editor-control`(current)。本 feature 是发现机制的实现,属能力的内部机制,未改愿景层的用户故事/边界/pitch。
- [x] 结论:req `agent-editor-control` 未变,无需更新(发现机制细节由 decision `command-discovery-mechanism` 承载)。

## 7. roadmap 回写

design `roadmap: file-bridge` / `roadmap_item: cmd-introspection`：
- [x] `file-bridge-items.yaml`:`cmd-introspection` `in-progress` → `done`,validate 通过。
- [x] `file-bridge-roadmap.md` 第 5 节:`cmd-introspection` 标 done。

## 8. attention.md 候选盘点

- [x] 无新候选。失焦不轮询那条已在 background-no-throttle ff 暴露并待处理(本 feature 验证全程 No Throttling 已开,无新增坑)。

## 9. 遗留

- 已知限制:无。
- 后续:`list_commands` 元知识需由 `agent-protocol-doc` feature 落地到 CLAUDE.md(roadmap 已记依赖)。
- 实现阶段"顺手发现":无。
- 实现自决:确定性 hash 由「控制字符分隔符拼串」改为「`MD5(JsonConvert.SerializeObject(排序后 Infos))`」——等价更稳健,已在 impl 汇报说明。
