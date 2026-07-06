# Unity Agent Bridge — 驱动协议(面向 AI Agent)

## 你的职责

**你是驱动这座桥的 AI Agent(主要 Claude Code)。** 本工程接入了 Unity Agent Bridge:你可以**通过读写文件**让 Unity 编辑器在主线程执行命令——无网络、无 socket。

本文档是你与 Unity 之间的**契约**。只要这个工程在,你的每一次驱动都必须遵守它。读完你就该知道:命令写去哪、响应从哪读、有哪些命令可用、出错怎么办。

> 包开发 / 写扩展命令见 `README.md`;本文档只讲**怎么驱动**。

## 铁律(每次驱动都必须遵守)

这五条是硬性契约,任何一条违反都会导致请求丢失、被读半截、或命令集不同步。开始前请记牢——落到每条命令上的**具体执行步骤**见第 7 节「每让 Unity 做一件事,严格按此 5 步」:

1. **原子写请求** —— 永远先写 `requests/{id}.request.json.tmp`,再 rename 成 `requests/{id}.request.json`。**绝不直接写最终名**(会被 Unity 读到半截)。
2. **id 每次唯一** —— `{id}` 由你生成,**每条请求全新、绝不复用**(即便重试也换新 id)。
3. **发一条、等一条、读完再发下一条** —— 不要并发堆多条请求;`responses/` 只保留最新一条响应。
4. **启动只调一次 `list_commands` 并记住结果** —— 命令清单不在文档里写死,必须运行时发现;调一次、缓存清单 + `commandsVersion`,之后**一直用缓存,不要重复调**。
5. **仅当命令集变了才刷新** —— 只有响应的 `commandsVersion` 与缓存不一致、装/卸/启停扩展后、或收到 `UNKNOWN_COMMAND` 时,才重调一次 `list_commands`;其余时候不重调。

> ⚠️ Unity 编辑器**失焦时默认不轮询**(你写了请求也不会被处理)。若需失焦也驱动:在 Unity 开 `Window/Agent Bridge`,勾顶部「失焦不节流」。

---

## 1. 通讯方式

Agent ↔ Unity 通过**文件**通讯,无网络。根目录 `<root>` 默认 `<UnityProject>/.agentbridge/`:

```
<root>/
├── requests/    你写 {id}.request.json
├── processing/  Unity 认领中(勿动)
└── responses/   Unity 写 {id}.response.json(你读)
```

一次驱动只碰两个目录:**写** `requests/`、**读** `responses/`。`processing/` 是 Unity 的认领区,你不要碰。

## 2. 发一条命令

1. **原子写请求**(铁律 1):先写 `requests/{id}.request.json.tmp`,再 rename 成 `requests/{id}.request.json`。
2. **轮询响应**:读 `responses/{id}.response.json`,**文件出现即为完成**(Unity 也用 tmp→rename 原子写,不会读到半截)。
3. **读完不必删** —— 清理由 Unity 端负责。

> 清理机制:Unity 端**每次写响应前都会清空** `responses/`,再写入当前 `{id}.response.json`。所以目录里**只有最新一次响应**——这正是铁律 3「发一条、等一条」的原因:上一条没读走,发下一条就会把它冲掉。

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
- `id`:必须与你发的请求一致——**用它对上号**,别用文件顺序判断。
- `status`:`"ok"` | `"error"`。`ok` 时 `error=null`;`error` 时 `result=null`。
- `result`:命令结果(`ok` 时)。
- `error`:`{ "code": string, "message": string }`(`error` 时)。
- `commandsVersion`:命令集内容 hash,**每条响应都有**——每次读到都拿它对照缓存(铁律 5)。

## 3. 错误码

| code | 含义 | 你该怎么做 |
|---|---|---|
| `UNKNOWN_COMMAND` | command 未注册(命令集可能变了) | 重调 `list_commands` 刷新缓存,再重试 |
| `INVALID_PARAMS` | params 缺字段 / 类型错 | 对照 `paramsSchema` 修正参数后重发 |
| `HANDLER_EXCEPTION` | 命令执行抛异常(message 带堆栈摘要) | 读 message 定位;非临时问题别盲目重试 |
| `INTERRUPTED` | 处理中途遇编辑器重编译(domain reload) | 换新 id 重发同一请求 |
| `INTERNAL_ERROR` | 框架内部错误(如请求 JSON 解析失败) | 检查你写的 JSON 是否合法 |
| `CONSOLE_UNAVAILABLE` | 命令需访问 Console 但内部 API 反射失败(版本不兼容) | 该命令在当前 Unity 版本不可用,换别的方式 |

handler 也可返回自有错误码(如 `MENU_NOT_FOUND`),含义见该命令 `description`。

## 4. 发现可用命令(务必先做)

命令清单**不写死在文档里**——它随代码 / 装的扩展变。用 `list_commands` 运行时获取:

```json
// 请求 command="list_commands"
// 响应 result:
{ "commands": [
    { "command": "ping", "description": "连通性测试,返回 pong 与 Unity 版本", "paramsSchema": null },
    { "command": "search_logs", 
      "description": "搜索编辑器 Console 当前日志条目:query(子串,regex=true 则正则)/type(error|warning|log)/ignoreCase/limit;返回 {total,matched,truncated,entries:[{message,type,file,line}]}", 
      "paramsSchema": { "type": "object", "properties": { "query": { "type": "string" }, "regex": { "type": "boolean" }, "ignoreCase": { "type": "boolean" }, "type": { "type": "string", "enum": ["error","warning","log"] }, "limit": { "type": "integer" } } } }
  ],
  "commandsVersion": "1c5bb3655cdf454c" }
```
每条 `{ command, description, paramsSchema }`;`paramsSchema` 是该命令参数的 JSON Schema(命令未声明参数则为 null)。**发任何命令前,先从缓存里查它的 schema 拼参数。**

**缓存与刷新规矩**(即铁律 4、5):
- **启动时调一次 `list_commands`**,缓存命令清单 + `commandsVersion`,**整个 session 一直用这份缓存**。
- 这是**唯一常规的一次调用**——之后每条命令都从缓存查 `paramsSchema`,**绝不因为"要发下一条命令"就重调**。
- 只有命令集**确实变了**才重调一次(属例外,平时不会发生),触发条件三选一:
  1. 任意响应的 `commandsVersion` 与你缓存的不一致(命令集变了);
  2. 装 / 卸 / 启停了扩展;
  3. 某命令返回 `UNKNOWN_COMMAND`(缓存过期 → 刷新后重试)。

`commandsVersion` 是内容 hash:同一命令集恒定(跨编辑器重启稳定),增删/改命令即变。

## 5. 驱动流程一图

```
读本文档(记牢铁律)
  → 启动:list_commands → 缓存 命令清单 + commandsVersion
  → 循环每条命令:
      查缓存拿 schema 拼参数
      → 写请求(.tmp → rename 到 requests/)
      → 轮询读 responses/{id}.response.json(按 id 对号)
      → 读响应.commandsVersion:
            == 缓存? → 处理 result / 继续下一条
            != 缓存? → 重新 list_commands 刷新,再继续
      → 遇 UNKNOWN_COMMAND → 重新 list_commands 后换新 id 重试
```

## 6. 写一个新命令

写一个 `ICommandHandler` 实现类放进被编译的程序集即自动注册(无需特性)。六个成员:`Command` 命令名(唯一)、`Description` 描述(供 `list_commands`)、`Group` 功能分组(管理器窗口按它分组)、`CanDisable` 能否被禁用(`false` 则锁定常开,如 ping / list_commands)、`Execute` 执行、`GetParamsSchema()` 参数 schema(无参返回空 `{}`,即 `new JObject()`)。详见 `README.md` 的「扩展」一节。

---

## 7. 复制到你的项目 CLAUDE.md

把下面这段粘进集成方项目的 `CLAUDE.md`,让 AI 一开 session 就知道怎么驱动、且**始终遵守流程**(**只放稳定元知识,不放命令清单**):

```markdown
## Unity Agent Bridge(驱动 Unity 编辑器)——必读,严格遵守

本工程接入了 Unity Agent Bridge:**任何需要让 Unity 编辑器做的事**(查场景、改物体、建资源、跑编译……),你(AI)都必须通过"写请求文件 / 读响应文件"来完成,不能凭空假设 Unity 状态,也不能跳过这套流程直接改工程文件。完整协议见 Unity 包内 `AGENT.md`。

`<root>` 为本 Unity 工程下**已存在**的 `.agentbridge/`。启动时直接查找 `.agentbridge/`(若当前目录是多个 Unity 工程的父目录,到各工程子目录下找),且必须包含 `requests/`、`processing/`、`responses/`。找不到就报错并停止;不要自行创建目录或猜路径。

### 第 0 步(每个 session 开头,只做一次)
在发任何其它命令**之前**,先发一次 `list_commands`,把返回的命令清单连同 `commandsVersion`、每条的 `paramsSchema` **记在本 session 里**。可用命令不在文档里、也不写死,只能这样运行时发现。**没做过第 0 步就发别的命令 = 错误。** 之后一直用这份缓存,不要重复调 `list_commands`(何时才需重调见文末)。

### 每让 Unity 做一件事,严格按此 5 步(不可跳步、不可合并、不可并发)
1. **取 schema**:从缓存里找到该命令,按它的 `paramsSchema` 拼 `params`(命令不存在 → 先做"重新发现",别猜)。
2. **起唯一 id**:为这条请求生成一个**全新、从未用过**的 `id`(哪怕是重试,也换新 id)。
3. **原子写**:先写 `<root>/requests/{id}.request.json.tmp`,**再改名**成 `<root>/requests/{id}.request.json`。**绝不能直接写最终名**(会被读到半截)。
   - Windows:`Move-Item -Force {id}.request.json.tmp {id}.request.json`
   - macOS/Linux:`mv {id}.request.json.tmp {id}.request.json`
4. **等这一条的响应**:反复读 `<root>/responses/{id}.response.json`,直到该文件出现(约每 1 秒一次,最多等 ~30 秒;超时按失败处理)。**在读到它之前,绝不发下一条命令**——`responses/` 只保留最新一条,抢发会把你要的那条冲掉。
5. **按 id 核对并处理**:确认响应 `id` 与你发的一致;`status=="ok"` 用 `result`,`status=="error"` 看 `error.code`(如 `INVALID_PARAMS` 改参数、`INTERRUPTED` 换新 id 重发)。顺便对照响应里的 `commandsVersion`(见文末)。

### 完整示例:让 Unity 执行 ping
- 生成 id:`req-8f3a`
- 写 `<root>/requests/req-8f3a.request.json.tmp`,内容:`{"v":1,"id":"req-8f3a","command":"ping","params":{}}`
- 改名为 `<root>/requests/req-8f3a.request.json`
- 轮询读 `<root>/responses/req-8f3a.response.json`,直到出现
- 得到 `{"id":"req-8f3a","status":"ok","result":{"message":"pong",...},"commandsVersion":"..."}` → 成功

### 绝不(违反任一条都会出错)
- 绝不直接写 `.request.json`(必须先 `.tmp` 再改名)。
- 绝不复用 `id`。
- 绝不在上一条响应读到之前,发下一条命令。
- 绝不跳过第 0 步、凭记忆或猜测发命令。

### 何时才重新 `list_commands`(平时都不需要)
仅当以下之一发生:任意响应的 `commandsVersion` 与你缓存的不一致 / 在 Unity 里装卸或启停了扩展 / 某命令返回 `UNKNOWN_COMMAND`。这时重发一次 `list_commands` 刷新缓存,再继续。

（可选)Unity 失焦时默认不轮询;若需失焦也驱动,在 Unity 开 `Window/Agent Bridge` 勾顶部「失焦不节流」。
```
