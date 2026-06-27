# Unity Agent Bridge

> Let an AI agent drive the Unity Editor over plain JSON files — request/response, polling host, extensible command framework.
>
> 让 AI Agent 通过 JSON 文件驱动 Unity 编辑器 —— 请求/响应、轮询主机、可扩展命令框架。

**English** | [简体中文](#简体中文)

---

## English

Unity Agent Bridge is an Editor-only Unity package that exposes the Unity Editor to an external AI agent through **file-based IPC**. The agent writes a request JSON file; a polling host inside the Editor claims it, runs the matching command on the main thread, and writes a response JSON file back. No sockets, no native plugins — just files.

### Why file-IPC

- **Zero networking** — works across any process that can read/write a folder (CLI agent, script, another app).
- **Crash-safe** — atomic write (`*.tmp` → rename) + single-claim (`requests → processing` atomic rename); a request is processed at most once.
- **Main-thread execution** — handlers run inside `EditorApplication.update`, so they can call any Unity API directly.

### How it works

```
agent ──> AgentBridge/requests/{id}.request.json
                          │  (atomic claim: rename to processing/)
            Editor host (EditorApplication.update polling)
                          │  dispatch → handler on main thread
agent <── AgentBridge/responses/{id}.response.json
```

**Request envelope**

```json
{ "v": 1, "id": "abc", "command": "ping", "params": {}, "timestamp": "..." }
```

**Response envelope** (`status: ok` → `result`; `status: error` → `error`; `commandsVersion` is stamped on every response)

```json
{ "v": 1, "id": "abc", "status": "ok", "result": { "message": "pong", "unityVersion": "6000.3.12f1" },
  "error": null, "commandsVersion": "4bd2f89c8d94a01b", "timestamp": "..." }
```

### Built-in commands

| Command | Purpose |
|---|---|
| `ping` | Connectivity check; returns `pong` + Unity version |
| `list_commands` | List available commands with description + params schema (+ `commandsVersion`) |
| `get_hierarchy` | Scene hierarchy tree (`maxDepth` / root filter) |
| `get_object` | A GameObject's components and their top-level properties |
| `get_selection` | Currently selected GameObjects (`[]` when none) |
| `list_assets` | Query project assets by `type` / `folder` / `query` |
| `create_object` | Create empty / primitive / prefab instance (optional parent) |
| `set_property` | Set a component property by nested path (records Undo, marks scene dirty) |
| `delete_object` | Delete a GameObject |
| `invoke_menu` | Execute an editor menu item (escape hatch) |
| `create_asset` | Create folder / text / ScriptableObject asset |
| `import_asset` | Copy an external disk file into the project and import it |
| `move_asset` | Move / rename an asset within the project |
| `delete_asset` | Delete an asset (to trash) |
| `refresh` | `AssetDatabase.Refresh()` |

Discover the live set at runtime via `list_commands` — it reflects exactly what is registered and enabled, and `commandsVersion` (a content hash) changes whenever the command set does, so an agent can cache and invalidate cheaply.

### Install

This repo's package lives in the `Unity/` subfolder (`com.unityagentbridge.core`, requires **Unity 2021.3+** and `com.unity.nuget.newtonsoft-json`).

- **Embed locally**: copy `Unity/` into your project's `Packages/UnityAgentBridge/`, or add to `Packages/manifest.json`:
  ```json
  "com.unityagentbridge.core": "file:../../UnityAgentBridge/Unity"
  ```
- **Git (UPM)**: add via Package Manager → *Add package from git URL* using this repo URL with `?path=Unity`.

The host auto-starts on load. The bridge root defaults to `<project>/AgentBridge/` (`requests/` `processing/` `responses/`).

### Command Manager

`Window ▸ Agent Bridge Window` lists every command (built-in + extension) discovered via Unity `TypeCache`, grouped by source, with a top toolbar to start/stop the bridge host and toggle background (no-throttling) polling. Toggle any command on/off — a disabled command is **hidden from `list_commands`** and returns `COMMAND_DISABLED` on dispatch (the disable list is persisted in `EditorPrefs`, namespaced per project).

### Add your own command

```csharp
using AgentBridge;
using Newtonsoft.Json.Linq;

public sealed class SayHelloHandler : ICommandHandler
{
    public string Command => "say_hello";
    public string Description => "returns a greeting";
    public object Execute(JObject @params) => new { greeting = "hi " + @params?["name"]?.Value<string>() };
    public JObject GetParamsSchema() => new JObject(); // {} when no params
}
```

`ICommandHandler` implementations are auto-registered via reflection / `TypeCache` — no manual wiring, no attribute. Throw `CommandException(code, message)` to return a typed error.

### Testing

EditMode tests live in `Unity/Tests/Editor/` (`AgentBridge.Editor.Tests` asmdef). They cover the file round-trip end-to-end (driving the real host via `[UnityTest]` + `EditorApplication.update`), the dispatch framework, every built-in command, and the command manager. Run them from a host project's **Test Runner ▸ EditMode**.

> Convention: every new command (`ICommandHandler`) ships with an EditMode test.

---

## 简体中文

Unity Agent Bridge 是一个**仅编辑器**的 Unity 包,通过**文件通讯(file-IPC)**把 Unity 编辑器暴露给外部 AI Agent。Agent 写一个请求 JSON 文件;编辑器内的轮询主机认领它、在主线程执行对应命令、再写回一个响应 JSON 文件。无 socket、无原生插件——只用文件。

### 为什么用文件通讯

- **零网络** —— 任何能读写文件夹的进程都能接(命令行 Agent、脚本、其他程序)。
- **抗中断** —— 原子写(`*.tmp` → rename)+ 单次认领(`requests → processing` 原子 rename),每个请求至多处理一次。
- **主线程执行** —— handler 跑在 `EditorApplication.update` 回调里,可直接调任意 Unity API。

### 工作原理

```
agent ──> AgentBridge/requests/{id}.request.json
                          │  (原子认领:rename 到 processing/)
            编辑器主机(EditorApplication.update 轮询)
                          │  分发 → 主线程上的 handler
agent <── AgentBridge/responses/{id}.response.json
```

**请求信封**

```json
{ "v": 1, "id": "abc", "command": "ping", "params": {}, "timestamp": "..." }
```

**响应信封**(`status: ok` → `result`;`status: error` → `error`;每条响应都盖 `commandsVersion`)

```json
{ "v": 1, "id": "abc", "status": "ok", "result": { "message": "pong", "unityVersion": "6000.3.12f1" },
  "error": null, "commandsVersion": "4bd2f89c8d94a01b", "timestamp": "..." }
```

### 内置命令

| 命令 | 用途 |
|---|---|
| `ping` | 连通性测试,返回 `pong` + Unity 版本 |
| `list_commands` | 列出所有命令及描述/参数 schema(+ `commandsVersion`) |
| `get_hierarchy` | 场景层级树(`maxDepth` / 根过滤) |
| `get_object` | 某 GameObject 的组件及其顶层属性 |
| `get_selection` | 当前选中的 GameObject(无选中返回 `[]`) |
| `list_assets` | 按 `type` / `folder` / `query` 查工程资产 |
| `create_object` | 创建 empty / primitive / prefab 实例(可指定父级) |
| `set_property` | 按嵌套路径改组件属性(记录 Undo、标脏场景) |
| `delete_object` | 删除 GameObject |
| `invoke_menu` | 执行编辑器菜单项(逃生舱) |
| `create_asset` | 创建 folder / text / ScriptableObject 资产 |
| `import_asset` | 把外部磁盘文件复制进工程并导入 |
| `move_asset` | 工程内移动/重命名资产 |
| `delete_asset` | 删除资产(进回收站) |
| `refresh` | `AssetDatabase.Refresh()` |

运行时用 `list_commands` 取实时命令集——它精确反映已注册且启用的命令;`commandsVersion`(内容 hash)在命令集变化时改变,Agent 可据此缓存与失效。

### 安装

本仓库的包在 `Unity/` 子目录(`com.unityagentbridge.core`,需 **Unity 2021.3+** 与 `com.unity.nuget.newtonsoft-json`)。

- **本地内嵌**:把 `Unity/` 拷进工程的 `Packages/UnityAgentBridge/`,或在 `Packages/manifest.json` 加:
  ```json
  "com.unityagentbridge.core": "file:../../UnityAgentBridge/Unity"
  ```
- **Git(UPM)**:Package Manager → *Add package from git URL*,用本仓库 URL 加 `?path=Unity`。

主机随加载自启。桥接根目录默认 `<工程>/AgentBridge/`(`requests/` `processing/` `responses/`)。

### 命令管理器

`Window ▸ Agent Bridge Window` 用 Unity `TypeCache` 列出所有命令(内置 + 扩展),按来源分组,顶部工具条可启停桥接主机、切换失焦不节流。任意命令可启停——被禁用的命令**从 `list_commands` 隐藏**、分发时返回 `COMMAND_DISABLED`(禁用名单存 `EditorPrefs`,按工程命名空间隔离)。

### 添加自定义命令

```csharp
using AgentBridge;
using Newtonsoft.Json.Linq;

public sealed class SayHelloHandler : ICommandHandler
{
    public string Command => "say_hello";
    public string Description => "returns a greeting";
    public object Execute(JObject @params) => new { greeting = "hi " + @params?["name"]?.Value<string>() };
    public JObject GetParamsSchema() => new JObject(); // 无参返回空 {}
}
```

`ICommandHandler` 实现经反射 / `TypeCache` 自动注册,无需手动接线、无需特性。抛 `CommandException(code, message)` 返回带类型的错误。

### 测试

EditMode 测试在 `Unity/Tests/Editor/`(`AgentBridge.Editor.Tests` asmdef),覆盖文件往返端到端(用 `[UnityTest]` + `EditorApplication.update` 驱动真实主机)、分发框架、全部内置命令、命令管理器。在宿主工程的 **Test Runner ▸ EditMode** 里跑。

> 规约:每个新增命令(`ICommandHandler`)都随附一份 EditMode 测试。

---

## License

See repository.
