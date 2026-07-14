# Unity Agent Bridge — 驱动协议（面向 AI Agent）

本文档是外部 Agent 驱动 Unity Editor 的 canonical contract。命令清单不写在文档里；实际 command、params schema 与 batch policy 始终来自运行时 `list_commands`。

## 铁律

1. **单通讯和显式确认**：发一条、等一条、完整读入响应、等待 `processing.json` 消失、删除 `response.json` 作为 ack，然后才能发下一条。Agent 不得同时发布第二条最终请求。
2. **固定槽位和原子发布**：先写 `request.json.tmp`，再原子 rename 为 `request.json`；绝不直接写最终文件。Unity 会将其原子 move 为 `processing.json`，再以 `response.json.tmp` → `response.json` 发布结果。
3. **全新且一致的 id**：id 只存在于 JSON envelope。每次请求使用从未用过的非空字符串，最长 64 字符；响应 JSON 的 id 必须与请求一致。
4. **先发现**：每个 session 第一条请求必须是 `list_commands`，并缓存 command set 与 `commandsVersion`。
5. **按版本刷新**：仅当响应版本变化、装卸/启停扩展或收到 `UNKNOWN_COMMAND` 时重新执行 `list_commands`。

## 1. 定位 Bridge root

从当前目录及父级寻找同时包含 `Assets/` 与 `.agentbridge/` 的 Unity 工程；必要时再扫描子目录。Bridge root 是 `<project>/.agentbridge/`。协议直接使用以下固定槽位：

```text
.agentbridge/
├── request.json.tmp    Agent 写入的临时文件
├── request.json        Agent 原子发布的请求
├── processing.json     Unity 已 claim 的请求；Agent 不得操作
├── response.json.tmp   Unity 写入的临时文件
└── response.json       Unity 原子发布的响应；Agent 等待 claim 清理后删除
```

这些文件是临时状态，空闲时不需要存在；定位时只要求 Unity 工程和已存在的 `.agentbridge/`。找不到 Bridge root 时停止并报告“Unity 没有安装或运行 AgentBridge”。不要自行创建或猜测 Bridge root。固定槽位协议不兼容任何旧布局。Unity 失焦时默认可能停止轮询；需要后台驱动时在 `Window/Agent Bridge` 启用失焦不节流。

## 2. 发现 command set

定位成功后只调用一次 `list_commands`，缓存：

- 每个 command 的 `paramsSchema`、`batchAllowed` 与 `supportsUndoCollapse`；
- 顶层 `commandsVersion`。

后续请求必须先在缓存中查 command 和 schema。不要凭文档、源码文件名或记忆拼参数。

## 3. 完成一次 exchange

1. 从缓存读取 command 的 `paramsSchema` 并构造 object 类型的 `params`。
2. 生成全新 id。
3. 确认不存在上一轮未确认的 `response.json`；写入 `request.json.tmp`，随后在 Bridge root 内原子 rename 为 `request.json`。
4. 轮询固定的 `response.json`；约每秒一次，通常最多等待 30 秒。
5. 文件出现后一次性完整读入内存，核对响应 id，处理 `status/result/error`，并比较 `commandsVersion`。
6. 完整读取后等待 Unity 删除 `processing.json`；等待期间必须保留 `response.json`。
7. `processing.json` 消失后，显式删除 `response.json` 作为 ack。删除成功后才可开始下一次 exchange。

这个顺序是协议不变量：如果 Agent 在 claim 清理前删除响应，恰好发生 domain reload 时，Unity 无法区分“响应已经发布”与“命令在响应前中断”，可能把已完成命令误报为 `INTERRUPTED`。

请求与响应：

```json
{ "v": 1, "id": "req-8f3a", "command": "ping", "params": {} }
```

```json
{
  "v": 1,
  "id": "req-8f3a",
  "status": "ok",
  "result": { "message": "pong" },
  "error": null,
  "commandsVersion": "1c5bb3655cdf454c",
  "timestamp": "2026-06-25T10:00:00.100Z"
}
```

请求与响应的固定上限均为 1 MiB，`params` 必须是 object。id 必须是非空字符串且最长 64 字符。JSON 无法解析，或 id 缺失、类型错误、超长时，`INVALID_REQUEST` 响应使用空 id；固定的 `response.json` 槽位仍能关联当前 exchange。响应超限时返回紧凑的 `RESPONSE_TOO_LARGE`，应缩小查询范围并换新 id。

## 4. 错误恢复

| code | 处理 |
|---|---|
| `INVALID_REQUEST` | 修正 envelope 或 id，换新 id 重发 |
| `INVALID_PARAMS` | 按缓存 schema 修正 params，换新 id 重发 |
| `UNKNOWN_COMMAND` | 刷新 command set，再换新 id 决定是否重发 |
| `COMMAND_DISABLED` | 不要绕过禁用状态；让用户在命令管理器启用 |
| `HANDLER_EXCEPTION` | 根据 message 定位 implementation；不要盲目重试 |
| `INTERRUPTED` | 副作用状态未知；先检查实际状态，再决定是否用新 id 重试 |
| `RESPONSE_TOO_LARGE` | 缩小 root、depth、limit 等范围后换新 id |
| `INTERNAL_ERROR` | Bridge framework 失败；报告 message |

command 可以返回额外领域错误码；其含义由 command description 和结果上下文决定。

## 5. 可往返引用与状态令牌

- Object reference 和 Component reference 应原样回传，不要手拼。Object path 使用 `~0` 表示 `~`、`~1` 表示 `/`、`~2` 表示空名称。
- `exactType=true` 的 Component reference 按精确 runtime type 计算 index；缺少该字段的旧 reference 保持兼容语义。
- `set_game_view_resolution` 返回的 `restore` 是一次性状态令牌；完成截图或验证后应原样回传以恢复原选择并删除临时尺寸。
- `batch` 会先预检全部 child command，再顺序执行；它不是事务，运行期失败不会回滚已经完成的 child command。

## 6. command set 何时失效

每次响应都比较 `commandsVersion`。只有以下情况刷新：

1. 响应版本与缓存不同；
2. Unity 中装卸或启停了扩展；
3. 收到 `UNKNOWN_COMMAND`。

其它时候继续使用当前缓存，不要在每条请求前重复 discovery。

## 7. 复制到你的项目 CLAUDE.md

命令管理器会从下面的 markdown code block 安装受管理片段。片段只负责把 Agent 导向本 canonical contract，不复制易漂移的 command metadata。

```markdown
## Unity Agent Bridge（驱动 Unity Editor）

本工程通过 Unity Agent Bridge 驱动编辑器。执行任何 Unity 查询或修改前，必须先阅读已安装包的 `AGENT.md`，并遵守其中的 fixed-slot single-flight exchange、原子请求写入、先等 `processing.json` 消失再删除响应的 ack、唯一 id、session 首次 `list_commands` 和 `commandsVersion` 刷新规则。

先寻找 `Assets/` 与已存在 `.agentbridge/` 同级的 Unity 工程；Bridge root 直接使用固定的 `request.json`、`processing.json`、`response.json` 槽位，空闲时这些文件可以不存在。找不到 Bridge root 时停止并报告 Unity 没有安装或运行 AgentBridge，不得自行创建目录。实际 command、params schema 和 batch policy 只从运行时 `list_commands` 获取，不得使用硬编码清单。
```
