---
doc_type: roadmap
slug: file-bridge
status: completed
created: 2026-06-24
last_reviewed: 2026-06-25
tags: [unity, agent, file-ipc, editor-automation]
related_requirements: [agent-editor-control]
related_architecture: []
---

# UnityAgentBridge:基于文件通讯的 Unity 编辑器 Agent 桥接

## 1. 背景

让 AI Agent(Claude Code)通过**文件**驱动 Unity 编辑器执行操作——Agent 写一个请求 JSON 文件,Unity 编辑器轮询发现后在主线程执行,把结果写回响应 JSON 文件,Agent 读回。选文件而非 HTTP 是为了规避端口占用、防火墙、连接生命周期管理、以及编辑器 domain reload 打断长连接等问题。

核心设计目标是**易扩展**:加一条新命令应当只需写一个带 `[Command]` 标记的 handler 类(编译期扩展)。v1 聚焦 Unity 编辑器侧,运行时/Play mode 控制留到后续。(曾规划 `execute_csharp` 运行时代码执行逃生舱,2026-06-25 移除——见 §8 变更日志。)

> 扩展的「从 GitHub 安装 / 搜索 / 选择启用」生态不在本 roadmap,见独立 roadmap `extension-manager`(依赖本 roadmap 的 handler 框架)。

## 2. 范围与明确不做

### 本 roadmap 覆盖
- 文件通讯协议(请求/响应 JSON 信封 + 错误码)
- 文件通道:原子写、请求认领、响应产出、目录布局
- Editor 宿主:`EditorApplication.update` 轮询、domain reload 存活、主线程执行保证
- 命令处理器框架:`ICommandHandler` + `[Command]` 自动注册 + 分发器(**扩展核心**)
- 内置命令:ping、场景层级查询、对象属性读写、菜单执行、资源操作
- Agent 侧协议约定文档 + 可选辅助脚本(Claude Code 直接读写,不做独立客户端库)
- 命令自省:`list_commands` 元命令、handler 自描述(描述+可选参数 schema)、`commandsVersion` 缓存失效信号

### 明确不做
- **运行时/Play mode 控制**(选了仅编辑器起步)——后续另起 roadmap/feature
- **独立 Agent 客户端库**(选 Claude Code 直接读写)——只出协议文档 + 轻量脚本
- **Unity 主动事件推送**(server-push,如「编译完成」事件主动通知 Agent)——v1 是请求-响应模型
- **多 Agent 并发 / 认证 / 权限**——假设单 agent、本地受信环境
- **网络/远程跨机**——明确是本地文件 IPC
- **运行时 C# 代码执行(`execute_csharp` 逃生舱)**——2026-06-25 移除:任意代码执行安全面过大、Roslyn 在本 Unity 版本可行性不确定、收益不抵风险。需要免重编译扩展时走 `extension-manager` 的 handler 安装路径
- **FileSystemWatcher 事件驱动**(选了轮询)——若轮询延迟成瓶颈再评估,记为观察项
- **扩展安装/搜索/管理 UI**——归 `extension-manager` roadmap

## 3. 模块拆分(概设)

```
UnityAgentBridge
├── M1 协议消息模型      请求/响应 JSON schema、错误码、版本号(纯契约无逻辑)
├── M2 文件通道          目录布局、原子写、请求认领、响应产出、文件清理
├── M3 命令处理器框架    ICommandHandler 接口 + [Command] 自动注册 + 分发器(扩展核心)
├── M4 Editor 宿主       轮询驱动、domain reload 存活、主线程执行、启停与配置
├── M5 内置命令集        ping/层级查询/属性读写/菜单执行/资源操作
└── M6 Agent 侧约定      协议文档 + 可选辅助脚本(Claude Code 直接读写)
```

### M1 · 协议消息模型
- **职责**:定义 Agent↔Unity 的线上契约——请求/响应 JSON 信封、错误码枚举、版本字段。纯数据结构,不含执行逻辑。
- **承载子 feature**:`bridge-core`、`cmd-introspection`(响应加 `commandsVersion`)
- **触碰现有代码**:全新

### M2 · 文件通道
- **职责**:落地文件 IPC 的物理读写——目录布局、原子写(temp+rename)、请求认领(rename 防重复)、响应写出、旧文件清理。平台无关。
- **承载子 feature**:`bridge-core`
- **触碰现有代码**:全新

### M3 · 命令处理器框架(扩展核心)
- **职责**:`ICommandHandler` 接口 + `[Command]` 特性 + 反射自动注册 + 分发器(把请求路由到 handler、捕获异常转错误响应)。这是「方便自己扩展」的落点:写个类、打标记即生效。
- **承载子 feature**:`bridge-core`、`cmd-introspection`(自描述契约 + `CommandRegistry.Version` hash)
- **触碰现有代码**:全新

### M4 · Editor 宿主
- **职责**:把通道挂到 `EditorApplication.update` 轮询;`[InitializeOnLoad]` 保证 domain reload 后重新挂载;保证 handler 在主线程执行;启停控制 + 配置(轮询间隔、根目录)。Unity 编辑器特有。
- **承载子 feature**:`bridge-core`
- **触碰现有代码**:全新

### M5 · 内置命令集
- **职责**:基于 M3 框架的具体 handler。每个 handler 是框架的一个扩展样例,也是实际能力。`bridge-core` 只含 `ping`;其余按类别拆成独立 feature。
- **承载子 feature**:`bridge-core`(ping)、`cmd-introspection`(list_commands)、`cmd-inspection`、`cmd-mutation`、`cmd-assets`(`cmd-csharp` 已于 2026-06-25 移除)
- **触碰现有代码**:全新

### M6 · Agent 侧约定
- **职责**:把文件协议写成 Claude Code 能照着读写的文档 + 可选辅助脚本(生成请求、轮询响应)。不是 Unity 模块,不做独立客户端库。
- **承载子 feature**:`agent-protocol-doc`
- **触碰现有代码**:全新

## 4. 模块间接口契约 / 共享协议(架构层详设)

> 以下是所有子 feature 的**硬约束输入**。feature-design 要改这里得回 `cs-roadmap update`。

### 4.1 协议消息格式(M1,Agent↔Unity 线上契约)

**方向**:Agent → Unity(请求)/ Unity → Agent(响应)
**形式**:文件协议,JSON 内容

**请求文件** `{id}.request.json`:
```
{
  "v":         int,        // 协议版本,当前 1
  "id":        string,     // Agent 生成的唯一 ID(建议 uuid 或 时间戳+序号)
  "command":   string,     // handler 名,对应 [Command("...")]
  "params":    object,     // 命令专属参数对象,可为 {}
  "timestamp": string      // ISO-8601
}
```

**响应文件** `{id}.response.json`:
```
{
  "v":         int,        // 1
  "id":        string,     // 回显请求 id
  "status":    string,     // "ok" | "error"
  "result":    object|null,// status=ok 时为命令结果;error 时 null
  "error":     {           // status=error 时存在;ok 时 null
                 "code":    string,
                 "message": string
               } | null,
  "commandsVersion": string, // 当前命令集内容 hash(见 4.7);每条响应都盖,AI 据此判断缓存是否过期
  "timestamp": string
}
```

**错误码枚举**(框架级,handler 可追加自有码):
```
UNKNOWN_COMMAND     command 未注册
INVALID_PARAMS      params 缺字段/类型错
HANDLER_EXCEPTION   handler 执行抛未分类异常(message 带堆栈摘要)
INTERRUPTED         请求处理中途遇 domain reload,重启后补发
INTERNAL_ERROR      框架内部错误(解析失败等)
```

**约束**:
- `status=ok` 时 `error` 必须为 null;`status=error` 时 `result` 必须为 null
- handler 要自定义错误码,抛 `CommandException(code, message)`,code 命名建议带前缀如 `MENU_NOT_FOUND`
- `commandsVersion` 必须出现在**每条响应**(含 error 响应),值为 `CommandRegistry.Version`(见 4.7)

### 4.2 目录布局与文件协议(M2)

**根目录**:可配置,默认 `<UnityProject>/AgentBridge/`(置于工程内便于相对路径定位)
```
<root>/
├── requests/      Agent 写入 {id}.request.json
├── processing/    Unity 认领后移入(防重复处理)
├── responses/     Unity 写出 {id}.response.json
└── logs/          可选,运行日志
```

**原子写协议**:任何写方先写 `{finalname}.tmp`,完成后 `rename` 成最终名(同卷 rename 原子)。读方只处理最终名文件 → 杜绝读到半截文件。

**请求认领协议**:Unity 每次轮询,对 `requests/{id}.request.json` 先原子 `rename` 到 `processing/{id}.request.json` 再解析 → 即使扫描重叠也保证单次处理。

**响应读取协议**:Agent 写完请求后轮询 `responses/{id}.response.json`;出现(已 rename 到位)后读取,读完可删。Agent 必须先写请求再轮询响应。

### 4.3 命令处理器接口(M3,扩展核心)

**方向**:M4 宿主 → M3 分发器 → handler
**形式**:C# 接口 + 特性

```csharp
public interface ICommandHandler {
    string Command { get; }            // 命令名,需全局唯一
    // 在 Unity 主线程执行。返回值序列化进 response.result。
    // 抛异常 → status:error;抛 CommandException 用其 code,否则归 HANDLER_EXCEPTION。
    object Execute(JObject @params);
    // params 的 JSON Schema,供 list_commands 暴露给 AI。无参命令返回空 schema `{}`(new JObject()),不返回 null。
    // (2026-06-25:原可选接口 ICommandSchema 已并入 ICommandHandler,见 §8。)
    JObject GetParamsSchema();
}

[AttributeUsage(AttributeTargets.Class)]
public sealed class CommandAttribute : Attribute {
    public CommandAttribute(string command, string description = null); // description 可选,向后兼容
    public string Command { get; }
    public string Description { get; }                                  // 供 list_commands 展示
}

public sealed class CommandException : Exception {
    public string Code { get; }
    public CommandException(string code, string message);
}
```

**自动注册**:宿主启动(`[InitializeOnLoad]`)时反射扫描已加载程序集,凡带 `[Command]` 且实现 `ICommandHandler` 的类(需无参构造)即注册,按 `Command` 建索引。**命令名重复 → 拒绝注册并记错误日志**(不静默覆盖)。

**命令集版本**:`CommandRegistry.Version`(string)= 对「排序后的 命令名+描述+参数 schema」算的短内容 hash,每次 `Rebuild` 重算。同一命令集永远同一 hash(跨重启/换机稳定)。用于 4.7 失效信号。

**分发器**:
```csharp
public static class CommandDispatcher {
    // 给定已解析 Request 返回 Response;永不抛异常(内部异常转 error 响应)。
    public static Response Dispatch(Request request);
}
```

**扩展契约(给用户的「方便扩展」承诺)**:新增命令 = 写一个 `ICommandHandler` 实现 + 打 `[Command("xxx")]`,放进工程任意被编译的程序集即生效,**无需改动 M1~M4 任何代码**。这条契约同时是 `extension-manager` roadmap 的地基(扩展即一包此类 handler)。

### 4.4 Editor 宿主接口(M4)

```csharp
[InitializeOnLoad]
public static class AgentBridgeHost {
    public static bool IsRunning { get; }
    public static void Start();        // 挂 EditorApplication.update 轮询
    public static void Stop();
}

// 配置(EditorPrefs 或 ScriptableObject 持久化)
public static class BridgeSettings {
    public static int    PollIntervalMs { get; set; }  // 默认 200
    public static string RootDir        { get; set; }  // 默认 <project>/AgentBridge
}
```

**约束**:
- handler 的 `Execute` 由轮询在 `EditorApplication.update` 回调内同步调用 → 天然主线程,handler 内可直接用 Unity API
- domain reload 后 `[InitializeOnLoad]` 重跑,重新挂载轮询;启动时若 `processing/` 有孤儿请求(无对应响应)→ 写 `INTERRUPTED` 错误响应

### 4.5 对象引用方案(M5 内多 feature 共享)

命令参数里引用 GameObject / Component 的统一方式,inspection 与 mutation 必须一致:
```
ObjectRef = {
  "path":       string|null,   // 层级路径 "Parent/Child/Leaf"(场景内唯一时优先)
  "instanceId": int|null       // GetInstanceID(),跨调用不稳定,仅同会话内有效
}                              // 二者至少给一个;都给以 instanceId 优先
ComponentRef = {
  "object":     ObjectRef,
  "type":       string,        // 组件类型全名 "UnityEngine.Transform"
  "index":      int            // 同类型多个时的序号,默认 0
}
```

### 4.6 Agent 侧约定(M6)

Agent(Claude Code)用文件工具:`Write` 写 `requests/{id}.request.json`(走 temp+rename 由辅助脚本保证,或直接写最终名——见 feature 决策),`Read` 轮询 `responses/{id}.response.json`。ID 由 Agent 生成保证唯一。辅助脚本可选,封装「发请求+等响应」。

### 4.7 命令发现机制(M1+M3+M5,跨 feature 硬约束)

落地 decision `command-discovery-mechanism`。让 AI 准确发现当前可用命令并感知变化(尤其装扩展后)。

**`list_commands` 元命令**(内置 handler):
```
请求 command="list_commands"
响应 result = {
  "commands": [ { "command": string, "description": string, "paramsSchema": object|null } ],
  "commandsVersion": string
}
```

**失效信号**:每条响应盖 `commandsVersion`(= `CommandRegistry.Version`,见 4.3)。AI 缓存它,任何响应里 version ≠ 缓存即刷新 `list_commands`。覆盖命令的增/删/改全部三种变化(单靠 `UNKNOWN_COMMAND` 只能发现删/改名)。

**调用频率约定**(写进 CLAUDE.md 元知识,由 agent-protocol-doc 落地):
- 启动调一次 `list_commands` 缓存;普通命令用缓存不重调
- 仅在 ① `commandsVersion` 不一致 ② 装/卸/启停扩展后 ③ 某命令返回 `UNKNOWN_COMMAND` 时刷新
- **禁止**把完整命令清单写进 CLAUDE.md(只放发现机制元知识),避免静态文档腐烂

**对命令 feature 的硬约束**:每个命令**必须**带描述(`[Command(name, description)]`),并实现 `ICommandHandler.GetParamsSchema()` 暴露参数 schema(无参返回空 `{}`),以便被 `list_commands` 暴露给 AI。(2026-06-25:`GetParamsSchema` 由原可选 `ICommandSchema` 并入 `ICommandHandler`,从"尽量"变为"必须",见 §8。)

## 5. 子 feature 清单

1. **bridge-core** — 协议模型 + 文件通道 + Editor 宿主 + handler 框架 + 一个 `ping` handler,打通端到端骨架 ✅ done
   - 所属模块:M1 + M2 + M3 + M4 + M5(ping)
   - 依赖:无
   - 状态:done(2026-06-24,真机 Unity 6000.3.12f1 验收)
   - 对应 feature:2026-06-24-bridge-core
   - 备注:最小闭环。粒度偏大但这是最窄的可演示闭环——少任何一块都跑不通

2. **cmd-introspection** — `list_commands` 元命令 + `commandsVersion`(注册表 hash,盖每条响应)+ handler 自描述(`[Command]` 描述 + 可选 `ICommandSchema`)✅ done
   - 所属模块:M1(响应加字段)+ M3(框架扩展)+ M5(list_commands handler)
   - 依赖:bridge-core
   - 状态:done(2026-06-25,真机 Unity 6000.3.12f1 验收;含内容寻址 version 增删回稳测试)
   - 对应 feature:2026-06-24-cmd-introspection
   - 备注:落地 decision `command-discovery-mechanism`;追加修改 bridge-core 的 Response/CommandAttribute/CommandRegistry(均向后兼容)

3. **agent-protocol-doc** — Agent 侧协议约定文档 + 可选 send-request 辅助脚本(含 4.7 发现机制元知识落地到 CLAUDE.md)✅ done
   - 所属模块:M6
   - 依赖:bridge-core, cmd-introspection(需文档化 list_commands 发现流程)
   - 状态:done(2026-06-25;产出 Unity/AGENT.md + CLAUDE.md 片段;辅助脚本按 D2 不做)
   - 对应 feature:2026-06-25-agent-protocol-doc

4. **cmd-inspection** — 只读查询 handler:`get_hierarchy`(场景树)、`get_object`(组件+属性)、`get_selection`、`list_assets` ✅ done
   - 所属模块:M5
   - 依赖:bridge-core, cmd-introspection(需自描述契约带描述/schema)
   - 状态:done(2026-06-25,2026-06-25-cmd-inspection 验收)
   - 对应 feature:2026-06-25-cmd-inspection
   - 备注:确立 4.5 对象引用方案的实际用法;共享 `ObjectRef`/`SceneObjectResolver`/`PropertySerializer` 落地(供 cmd-mutation 复用)

5. **cmd-mutation** — 写操作 handler:`set_property`、`invoke_menu`、`create_object`、`delete_object` ✅ done
   - 所属模块:M5
   - 依赖:bridge-core, cmd-inspection(复用对象引用方案,且改前常需先查;传递依赖 cmd-introspection)
   - 状态:done(2026-06-25,2026-06-25-cmd-mutation 验收;真机测试经用户决定跳过)
   - 对应 feature:2026-06-25-cmd-mutation
   - 备注:复用 SceneObjectResolver/ObjectRef;补 PropertyDeserializer(写,PropertySerializer 对偶);写语义=标 dirty 不自动 save + 记录 Undo

6. **cmd-assets** — 资源操作 handler:`import_asset`、`create_asset`、`move_asset`、`delete_asset`、`refresh`(AssetDatabase) ✅ done
   - 所属模块:M5
   - 依赖:bridge-core, cmd-introspection(需自描述契约)
   - 状态:done(2026-06-25,2026-06-25-cmd-assets 验收;真机测试经用户决定跳过)
   - 对应 feature:2026-06-25-cmd-assets
   - 备注:资产即时落盘(非 dirty-only)、删除走回收站、写路径限 Assets/ 下;首个消费 commands-category-subdirectory convention(Commands/Assets/)

7. **cmd-csharp** — `execute_csharp` 逃生舱:编译并运行 C# 片段返回结果(运行时扩展) ❌ dropped
   - 所属模块:M5
   - 依赖:bridge-core, cmd-introspection(需自描述契约)
   - 状态:dropped(2026-06-25)
   - 对应 feature:未启动
   - 移除理由:任意代码执行安全面过大(无沙箱可言);运行时编译依赖(Roslyn)在本 Unity 版本可行性不确定、技术最重;收益不抵风险。"免重编译扩展"诉求改由 `extension-manager` 的 handler 安装路径承接。

**最小闭环**:第 1 条 `bridge-core`(已 done)——Agent 写 `ping` 请求 → Unity 轮询认领 → 回写 `pong` → Agent 读到,端到端打通。

## 6. 排期思路

技术依赖强制 `bridge-core` 第一(所有命令都挂在它的框架上)。core 落地后 `cmd-inspection`(只读、安全、立刻有价值)与 `agent-protocol-doc`(让 Claude Code 可靠驱动)是自然的第二梯队;`cmd-mutation` 依赖 inspection 的对象引用用法故排其后;`cmd-assets` 独立挂在 core 上。

**实际落地顺序**(均 done):bridge-core → cmd-introspection → agent-protocol-doc → cmd-inspection → cmd-mutation → cmd-assets。`cmd-csharp` 于 2026-06-25 移除(见 §8)。至此本 roadmap 所有保留子 feature 完成。

## 7. 观察项

- `requirements/` 与 `architecture/` 目前为空。建议 `bridge-core` 落地后用 `cs-req` 补一份能力愿景,并由 `cs-feat-accept` 把 4.1~4.5 的接口现状回写进 `architecture/ARCHITECTURE.md`。
- domain reload 在命令执行中途发生的精确处理策略,已在 `bridge-core` 落地为「补发 INTERRUPTED 响应」(原 `cmd-csharp` 重编译触发场景随该 feature 移除已不适用)。
- FileSystemWatcher 已否决。若 200ms 轮询延迟成为体验瓶颈,再评估事件驱动备选。
- 扩展生态(从 GitHub 安装/搜索/选择启用)拆到独立 roadmap `extension-manager`,依赖本 roadmap 4.3 的 handler 框架契约。
- **存量影响(2026-06-24 update)**:bridge-core 已 done。`cmd-introspection` 将回头追加修改其 `Response`(加 `commandsVersion`)、`CommandAttribute`(加 `description`)、`CommandRegistry`(加 `Version` hash)——均向后兼容,不破坏现有 ping 行为。届时由 `cmd-introspection` 的 design/impl 处理,acceptance 再把 4.7 现状回写进 architecture。

## 8. 变更日志

- **2026-06-25(契约简化)**:**`ICommandSchema` 并入 `ICommandHandler`**(用户决定)。`GetParamsSchema()` 成为 `ICommandHandler` 的必选成员、删除 `ICommandSchema` 接口;无参命令返回空 schema `{}`(原为 null)。
  - 4.3 契约变化:接口由两个并成一个;自描述从"可选实现"变"必选实现"。
  - 微行为变更:`ping`/`list_commands` 等无参命令的 `paramsSchema` 由 `null` → `{}`,`commandsVersion` 随之变化(AI 重拉一次)。
  - 影响:所有内置命令 handler(15 个)去掉 `, ICommandSchema`,`ping`/`list_commands` 补 `GetParamsSchema()=>{}`;`CommandRegistry` 改直调;扩展作者今后须实现该方法(无参返回 `{}`)。`Unity/AGENT.md` 扩展指引同步。
- **2026-06-25**:移除子 feature `cmd-csharp`(`execute_csharp` 运行时代码执行逃生舱)。
  - 理由:任意代码执行安全面过大(无沙箱)、运行时编译(Roslyn)在本 Unity 版本可行性不确定且技术最重、收益不抵风险。
  - items.yaml 对应条目标 `status: dropped`(不删,留存理由);§1/§2/§3/§5/§6/§7 同步去除 execute_csharp 相关表述。
  - "免重编译扩展"诉求改由 `extension-manager` roadmap 的 handler 安装路径承接。
  - 受影响:无已实现代码(该 feature 从未启动);无其它子 feature 依赖它。本 roadmap 保留子 feature 至此全部 done。
- **2026-06-24**:新增命令发现机制(落地 decision `command-discovery-mechanism`)。
  - 接口契约变化:4.1 响应信封加 `commandsVersion`;4.3 `[Command]` 加 description、新增可选 `ICommandSchema`、`CommandRegistry.Version` hash;新增 4.7 命令发现机制节。
  - 新增子 feature `cmd-introspection`(排第 2)。
  - 依赖调整:`agent-protocol-doc`/`cmd-inspection`/`cmd-assets`/`cmd-csharp` 增加对 `cmd-introspection` 的依赖(需自描述契约);`cmd-mutation` 经 `cmd-inspection` 传递依赖。
  - 受影响的已完成 feature:`bridge-core`(done)——见观察项「存量影响」,契约变更向后兼容,由 `cmd-introspection` 追加实现。
