# Unity Agent Bridge

> Let an AI agent drive the Unity Editor over plain JSON files — request/response, polling host, extensible command framework.

**English** | [简体中文](README_CN.md)

---

Unity Agent Bridge is an Editor-only Unity package that exposes the Unity Editor to an external AI agent through **file-based IPC**. The agent publishes one request JSON at a time; the Editor claims it, runs the matching command on the main thread, and publishes one response JSON back. After reading the complete response, the agent waits for `processing.json` to disappear, then deletes `response.json` as an explicit acknowledgement. No sockets, no native plugins — just files.

## Why file-IPC

- **Zero networking** — works across any process that can read/write a folder (CLI agent, script, another app).
- **Crash-safe** — atomic publish (`request.json.tmp` → `request.json`) + atomic Claim (`request.json` → `processing.json`); a claimed request is processed at most once.
- **Main-thread execution** — handlers run inside `EditorApplication.update`, so they can call any Unity API directly.

## How it works

```
agent ──> .agentbridge/request.json.tmp ──rename──> request.json
                                                        │
                                      atomic claim: move to processing.json
                                                        │
                                  Editor host dispatches on the main thread
                                                        │
agent <── .agentbridge/response.json <──rename── response.json.tmp
   │
   └── read completely → wait for processing.json to disappear → delete response.json
```

The protocol is strictly single-flight: the agent publishes one request, waits for and reads the complete response, waits until Unity has removed `processing.json`, deletes `response.json` to acknowledge it, and only then publishes the next request. Keeping the response until claim cleanup completes prevents a reload between response publication and cleanup from being mistaken for an interrupted command. Temporary files are ignored. The fixed slot names are the protocol; the previous per-id directory layout is not supported.

**Request envelope**

```json
{ "v": 1, "id": "abc", "command": "ping", "params": {} }
```

The `id` exists only in the JSON envelope; filenames are fixed. Use a fresh non-empty string of at
most 64 characters for every request, and require the response `id` to match it. Missing or invalid
`v`, `id`, or `command` and malformed JSON are rejected with `INVALID_REQUEST`. When an invalid request
has no usable id, its response uses `"id": ""`; the fixed `response.json` slot still identifies the exchange.
Each request is capped at 1 MiB and `params` must be an object. Before execution, parameters are
validated against the command's live `paramsSchema`, including types, required fields, enums, and bounds.

**Response envelope** (`status: ok` → `result`; `status: error` → `error`; `commandsVersion` is stamped on every response)

```json
{ "v": 1, "id": "abc", "status": "ok", "result": { "message": "pong", "unityVersion": "6000.3.12f1" },
  "error": null, "commandsVersion": "4bd2f89c8d94a01b", "timestamp": "..." }
```

Responses are capped at a fixed 1 MiB of UTF-8. An oversized command result is replaced by a compact
`RESPONSE_TOO_LARGE` error; narrow the query (`root`, `maxDepth`, `limit`, and similar fields) and retry with a new id.

## Built-in commands

The package currently includes these built-in commands, grouped by `ICommandHandler.Group`:

- **Meta** — `ping`, `list_commands`
- **Inspection** — `get_hierarchy`, `get_object`, `get_selection`, `get_asset`, `get_asset_dependencies`, `list_assets`
- **Scenes** — `list_scenes`, `open_scene`, `save_scene`, `close_scene`, `set_active_scene`
- **Mutation** — `create_object`, `update_object`, `delete_object`, `add_component`, `remove_component`, `set_property`, `set_selection`, `frame_object`, `set_game_view_resolution`, `invoke_menu`, `undo`, `redo`, `batch`
- **Prefab** — `prefab`
- **Assets** — `create_asset`, `import_asset`, `move_asset`, `delete_asset`, `set_importer_property`, `refresh`
- **PlayMode** — `play_scene`, `pause`, `resume`, `step`
- **Capture** — `capture_game_view`, `capture_scene_view`
- **Console** — `search_logs`, `clear_logs`
- **Compilation** — `recompile`, `get_compile_result`
- **Testing** — `run_tests`, `get_test_result`

This list is a package overview. `list_commands` remains the canonical command interface: it returns the live enabled command set, descriptions, parameter schemas, batch policies, and `commandsVersion`; do not copy that metadata into an agent prompt or integration.

Source map: `Channel/` owns the file exchange, `Dispatch/` owns command discovery and invocation, `Commands/` owns Unity operations, `Scene/` owns round-trippable references and serialized properties, and `Testing/` owns asynchronous test runs.

## Install

This repo's package lives in the `Unity/` subfolder (`me.xw.unityagentbridge`, requires **Unity 2021.3+**, `com.unity.nuget.newtonsoft-json`, and `com.unity.test-framework`).

- **Git (UPM)**: add via Package Manager → *Add package from git URL*:
  ```
  https://github.com/XuToWei/UnityAgentBridge.git?path=Unity
  ```

After installing the package for the first time, open `Window ▸ Agent Bridge` and click **Enable Bridge**. This creates the bridge root at `<project>/.agentbridge/` and starts the host. A domain reload only restores the host automatically when that directory already exists; when it does not exist, the **Enable Bridge** toggle remains off. The transient protocol slots are `request.json`, `processing.json`, and `response.json`.

## Automated validation

With the Unity Editor open and its bridge directory present, run from the repository root:

```powershell
./scripts/Test-AgentBridge.ps1 -ProjectPath "G:\path\to\UnityProject" -Suite Baseline
./scripts/Test-AgentBridge.ps1 -ProjectPath "G:\path\to\UnityProject" -Suite Mutating
./scripts/Test-AgentBridge.ps1 -ProjectPath "G:\path\to\UnityProject" -Suite Full
```

`Baseline` covers read-only behavior and pre-mutation failures. `Mutating` tests writes in Play Mode and unique temporary scenes/assets, then cleans up. `Full` additionally runs `refresh`, exercises `run_tests` / `get_test_result` against the single Editor test assembly `AgentBridge.Tests` under `Unity/Tests`, and requests a real script recompile. JSON reports are written under `.agentbridge/test-results/`. The channel is single-flight, so do not run another writer against `.agentbridge/request.json` at the same time.

Unity enables package tests automatically for an embedded development package. When validating a Git/registry-installed copy, add `me.xw.unityagentbridge` to the target project's `Packages/manifest.json` `testables` array before running `Full`, so Unity compiles the package's Editor test assembly.

Scene-object responses use canonical round-trippable paths. Each GameObject name segment encodes `~` as `~0`, `/` as `~1`, and an empty name as `~2`. Reuse the complete returned ObjectRef (`path + instanceId + scenePath`) instead of constructing one; the resolver cross-checks hints and rejects stale instance IDs.

## Command Manager

`Window ▸ Agent Bridge` lists every command (built-in + extension) discovered via Unity `TypeCache`, grouped by **function** (`ICommandHandler.Group`), with click-to-sort headers, per-group filter, and bulk enable/disable. A top toolbar starts/stops the bridge host and toggles background (no-throttling) polling. Toggle any command on/off — a disabled command is **hidden from `list_commands`** and returns `COMMAND_DISABLED` on dispatch (the disable list is persisted in `EditorPrefs`, namespaced per project). Each handler declares this policy through `CanDisable`; protocol-required commands (`ping` and `list_commands`) return `false`.

## Add your own command

```csharp
using AgentBridge;
using Newtonsoft.Json.Linq;

public sealed class SayHelloHandler : ICommandHandler
{
    public string Command => "say_hello";
    public string Description => "returns a greeting";
    public string Group => "Custom";        // function group shown in the window
    public bool CanDisable => true;
    public CommandBatchMode BatchMode => CommandBatchMode.Allowed;
    public async CommandTask<object> ExecuteAsync(JObject @params) => new { greeting = "hi " + @params?["name"]?.Value<string>() };
    public JObject ParamsSchema { get; } = new JObject(); // {} when no params
}
```

`ICommandHandler` implementations are auto-registered via reflection / `TypeCache` — no manual wiring and no registration attribute. Members: `Command` (unique name), `Description`, `Group` (window grouping), `CanDisable`, `BatchMode`, `ExecuteAsync`, and `ParamsSchema`. `ExecuteAsync` returns `CommandTask<object>` and may use normal `async`/`await`. Choose `NotAllowed`, `Allowed`, or `AllowedWithUndoCollapse` for `BatchMode`. Throw `CommandException(code, message)` to return a typed error.

`ICommandHandler` is the only extension seam. The package does not maintain a local `extension.json` install/uninstall protocol; add or remove extension code through UPM or project assemblies.

---

## License

See repository.
