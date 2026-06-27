# Unity Agent Bridge — 驱动协议(面向 AI Agent)

本文档给**驱动桥接的 AI Agent**(主要 Claude Code)。读完即可通过文件操作让 Unity 编辑器执行命令。
包开发 / 写扩展命令见 `README.md`。

## 1. 通讯方式

Agent ↔ Unity 通过**文件**通讯,无网络。根目录 `<root>` 默认 `<UnityProject>/AgentBridge/`:

```
<root>/
├── requests/    你写 {id}.request.json
├── processing/  Unity 认领中(勿动)
└── responses/   Unity 写 {id}.response.json(你读)
```

> ⚠️ Unity 编辑器**失焦时默认不轮询**。若需失焦也驱动,在 Unity 开 `Window/Agent Bridge Window`、勾选顶部「失焦不节流」开关。

## 2. 发一条命令

1. **原子写请求**:先写 `requests/{id}.request.json.tmp`,再 rename 成 `requests/{id}.request.json`(切勿直接写最终名——会被读到半截)。`{id}` 由你生成、**每次唯一、绝不复用**(复用旧 id 可能读到上次残留的旧响应)。
2. **轮询响应**:读 `responses/{id}.response.json`,出现即为完成(Unity 也用 tmp→rename 原子写)。**读完不必删——清理由 Unity 端负责。**

> 清理:Unity 端**每个编辑器会话、在生成首个响应前清一次** `responses/`(清掉上次会话遗留;domain reload 不重清,避免删掉刚写出未读的响应)。本会话内响应会累积但不影响读取(你用唯一 id 即可)。

**请求** `{id}.request.json`:
```json
{ "v": 1, "id": "x1", "command": "ping", "params": {}, "timestamp": "2026-06-25T10:00:00Z" }
```

**响应** `{id}.response.json`:
```json
{ "v": 1, "id": "x1", "status": "ok",
  "result": { "message": "pong", "unityVersion": "6000.3.12f1" },
  "error": null, "commandsVersion": "1c5bb3655cdf454c", "timestamp": "2026-06-25T10:00:00.1Z" }
```

字段:
- `status`:`"ok"` | `"error"`。`ok` 时 `error=null`;`error` 时 `result=null`。
- `result`:命令结果(`ok` 时)。
- `error`:`{ "code": string, "message": string }`(`error` 时)。
- `commandsVersion`:命令集内容 hash,**每条响应都有**(见第 4 节)。

## 3. 错误码

| code | 含义 |
|---|---|
| `UNKNOWN_COMMAND` | command 未注册(命令集可能变了 → 重新发现) |
| `INVALID_PARAMS` | params 缺字段 / 类型错 |
| `HANDLER_EXCEPTION` | 命令执行抛异常(message 带堆栈摘要) |
| `INTERRUPTED` | 请求处理中途遇编辑器重编译(domain reload),需重发 |
| `INTERNAL_ERROR` | 框架内部错误(如请求 JSON 解析失败) |

handler 可返回自有错误码(如 `MENU_NOT_FOUND`)。

## 4. 发现可用命令(重要)

命令清单**不写死在文档里**——它随代码 / 装的扩展变。用 `list_commands` 运行时获取:

```json
// 请求 command="list_commands"
// 响应 result:
{ "commands": [
    { "command": "ping", "description": "连通性测试,返回 pong 与 Unity 版本", "paramsSchema": null }
  ],
  "commandsVersion": "1c5bb3655cdf454c" }
```
每条 `{ command, description, paramsSchema }`;`paramsSchema` 是该命令参数的 JSON Schema(命令未声明则 null)。

**缓存与刷新规矩**:
- **启动时调一次 `list_commands`,缓存命令清单 + `commandsVersion`**。
- 普通命令用缓存,**不要每条都重调**。
- 以下任一**刷新一次**(重调 `list_commands`):
  1. 任意响应的 `commandsVersion` 与你缓存的不一致(命令集变了);
  2. 装 / 卸 / 启停了扩展;
  3. 某命令返回 `UNKNOWN_COMMAND`(缓存过期 → 刷新后重试)。

`commandsVersion` 是内容 hash:同一命令集恒定(跨编辑器重启稳定),增删/改命令即变。

## 5. 驱动流程一图

```
读 CLAUDE.md 片段
  → 启动:list_commands → 缓存 命令 + commandsVersion
  → 发命令(.tmp→rename 写 requests/)→ 读 responses/{id}
  → 响应 commandsVersion == 缓存? 是→下一条 / 否→重新 list_commands
  → 遇 UNKNOWN_COMMAND → 重新 list_commands 后重试
```

## 6. 写一个新命令

写一个 `ICommandHandler` 实现类放进被编译的程序集即自动注册(无需特性)。`ICommandHandler` 含 `Command`/`Description`/`Group`/`CanDisable`/`Execute`/`GetParamsSchema` 六个成员——`Command` 命令名(唯一)、`Description` 描述(供 list_commands)、`Group` 功能分组(管理器窗口按它分组)、`CanDisable` 能否被禁用(`false` 则锁定常开,如 ping/list_commands)、`GetParamsSchema()` 参数 schema(无参返回空 `{}`,`new JObject()`)。详见 `README.md` 的「扩展」一节。

---

## 7. 复制到你的项目 CLAUDE.md

把下面这段粘进集成方项目的 `CLAUDE.md`,让 AI 一开 session 就知道怎么驱动(**只放稳定元知识,不放命令清单**):

```markdown
## Unity Agent Bridge
本工程接入了 Unity Agent Bridge:AI 可通过文件驱动 Unity 编辑器执行命令。
- 写请求:先写 `<root>/requests/{id}.request.json.tmp` 再 rename 成 `.request.json`(原子);`<root>` 默认 `<工程>/AgentBridge/`。
- 读响应:轮询 `<root>/responses/{id}.response.json`。
- **先调 `list_commands`** 拿可用命令并缓存(含 `commandsVersion`)。之后任意响应的 `commandsVersion` 与缓存不一致、或装/卸扩展后、或遇 `UNKNOWN_COMMAND`,就重新 `list_commands`。
- 完整协议见 Unity 包内 `AGENT.md`。
- (可选)失焦也要驱动时,在 Unity 开 `Window/Agent Bridge Window` 勾顶部「失焦不节流」。
```
