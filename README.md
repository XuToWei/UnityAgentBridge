# Unity Agent Bridge

> Let an AI agent drive the Unity Editor over plain JSON files — request/response, polling host, extensible command framework.

**English** | [简体中文](README_CN.md)

---

Unity Agent Bridge is an Editor-only Unity package that exposes the Unity Editor to an external AI agent through **file-based IPC**. The agent writes a request JSON file; a polling host inside the Editor claims the latest final request, discards older pending requests, runs the matching command on the main thread, and writes a response JSON file back. No sockets, no native plugins — just files.

## Why file-IPC

- **Zero networking** — works across any process that can read/write a folder (CLI agent, script, another app).
- **Crash-safe** — atomic write (`*.tmp` → rename) + single latest-claim (`requests → processing` atomic rename); stale pending requests are discarded and a claimed request is processed at most once.
- **Main-thread execution** — handlers run inside `EditorApplication.update`, so they can call any Unity API directly.

## How it works

```
agent ──> .agentbridge/requests/{id}.request.json
                      │  (latest final request only; older pending requests discarded)
                      │  (atomic claim: rename to processing/)
        Editor host (EditorApplication.update polling)
                      │  dispatch → handler on main thread
agent <── .agentbridge/responses/{id}.response.json
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

## Built-in commands

| Command | Purpose |
|---|---|
| `ping` | Connectivity check; returns `pong` + Unity version |
| `list_commands` | List available commands with description + params schema (+ `commandsVersion`) |
| `get_hierarchy` | Scene hierarchy tree (`maxDepth` / root filter) |
| `get_object` | A GameObject's components and their top-level properties |
| `get_selection` | Currently selected GameObjects (`[]` when none) |
| `list_assets` | Query project assets by `type` / `folder` / `query` |
| `capture_game_view` | Capture the current Game view as PNG under `.agentbridge/screenshots`; returns file path + metadata |
| `create_object` | Create empty / primitive / prefab instance (optional parent) |
| `set_property` | Set a component property by nested path (records Undo, marks scene dirty) |
| `delete_object` | Delete a GameObject |
| `invoke_menu` | Execute an editor menu item (escape hatch) |
| `create_asset` | Create folder / text / ScriptableObject asset |
| `import_asset` | Copy an external disk file into the project and import it |
| `move_asset` | Move / rename an asset within the project |
| `delete_asset` | Delete an asset (to trash) |
| `refresh` | `AssetDatabase.Refresh()` |
| `recompile` | Trigger a script recompile (returns immediately; result via `get_compile_result`) |
| `get_compile_result` | Read the last compile result (`errors[]` / `warnings[]` + counts) |
| `search_logs` | Search Console logs by query (substring or regex), type filter, limit; returns matched entries with message/type/file/line |

Discover the live set at runtime via `list_commands` — it reflects exactly what is registered and enabled, and `commandsVersion` (a content hash) changes whenever the command set does, so an agent can cache and invalidate cheaply.

## Install

This repo's package lives in the `Unity/` subfolder (`me.xw.unityagentbridge`, requires **Unity 2021.3+** and `com.unity.nuget.newtonsoft-json`).

- **Git (UPM)**: add via Package Manager → *Add package from git URL*:
  ```
  https://github.com/XuToWei/UnityAgentBridge.git?path=Unity
  ```

The host auto-starts on load. The bridge root defaults to `<project>/.agentbridge/` (`requests/` `processing/` `responses/`).

## Command Manager

`Window ▸ Agent Bridge` lists every command (built-in + extension) discovered via Unity `TypeCache`, grouped by **function** (`ICommandHandler.Group`), with click-to-sort headers, per-group filter, and bulk enable/disable. A top toolbar starts/stops the bridge host and toggles background (no-throttling) polling. Toggle any command on/off — a disabled command is **hidden from `list_commands`** and returns `COMMAND_DISABLED` on dispatch (the disable list is persisted in `EditorPrefs`, namespaced per project). Essential commands (`CanDisable == false`, e.g. `ping` / `list_commands`) are locked on.

## Add your own command

```csharp
using AgentBridge;
using Newtonsoft.Json.Linq;

public sealed class SayHelloHandler : ICommandHandler
{
    public string Command => "say_hello";
    public string Description => "returns a greeting";
    public string Group => "Custom";        // function group shown in the window
    public bool CanDisable => true;          // false = locked on (protocol-essential)
    public object Execute(JObject @params) => new { greeting = "hi " + @params?["name"]?.Value<string>() };
    public JObject GetParamsSchema() => new JObject(); // {} when no params
}
```

`ICommandHandler` implementations are auto-registered via reflection / `TypeCache` — no manual wiring, no attribute. Members: `Command` (unique name), `Description`, `Group` (window grouping), `CanDisable` (false locks it on), `Execute`, `GetParamsSchema`. Throw `CommandException(code, message)` to return a typed error.

---

## License

See repository.
