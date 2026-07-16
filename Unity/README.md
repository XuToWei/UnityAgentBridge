# Unity Agent Bridge

让 AI Agent 通过**文件**驱动 Unity 编辑器执行命令。请求/响应 JSON 文件 + `EditorApplication.update` 轮询 + 可扩展的 `ICommandHandler` 框架。

包内已包含协议/文件通道、命令发现与管理器，以及 scenes、inspection、mutation、prefab、assets、PlayMode、capture、console、compilation、testing 等内置命令。驱动桥接见 [`AGENT.md`](AGENT.md)；实际可用命令始终以运行时 `list_commands` 为准。

## 安装

UPM 通过 git URL 安装(本包在仓库子目录 `Unity/`):

```
https://github.com/XuToWei/UnityAgentBridge.git?path=Unity
```

依赖 `com.unity.nuget.newtonsoft-json` 与 `com.unity.test-framework`(已声明在 package.json,UPM 自动拉取)。

## 启动

首次安装后,打开 `Window/Agent Bridge` 并点击**启用桥接**；此时才会创建 `.agentbridge` 并启动宿主。Domain Reload 仅在该目录已经存在时自动恢复宿主；目录不存在时,**启用桥接**按钮保持关闭。顶部工具条也可停止桥接 / 切失焦不节流。

默认文件根目录:`<UnityProject>/.agentbridge/`。协议直接使用固定槽位 `request.json`、`processing.json`、`response.json`；这些文件只在 exchange 对应阶段存在。

## 驱动协议(面向 AI Agent)

请求/响应 JSON schema、错误码、`list_commands` 发现机制与 `commandsVersion` 缓存刷新规矩,以及可粘贴进项目 `CLAUDE.md` 的元知识片段,见 **[`AGENT.md`](AGENT.md)**(本包内,面向驱动桥接的 AI)。

要点:写请求务必原子(先写 `request.json.tmp` 再 rename 为 `request.json`)；Unity 用原子 move 将其认领为 `processing.json`；响应通过 `response.json.tmp` 原子发布为 `response.json`。Agent 完整读取响应后必须先等待 `processing.json` 消失，再删除 `response.json` 作为 ack，之后才能发送下一条。id 只存在于 envelope，必须非空、最长 64 字符且每次使用全新值；每条响应带 `commandsVersion`；AI 启动应先调 `list_commands` 并按 version 变化刷新。
`list_commands` 的每项还会返回 `batchAllowed` 与 `supportsUndoCollapse`，不要靠命令名猜 batch 能力。

请求上限为 1 MiB，`params` 必须是 object，并会在执行 handler 前按该命令的 `paramsSchema` 校验。
响应按 UTF-8 计算固定上限为 1 MiB；超限结果会改为紧凑的 `RESPONSE_TOO_LARGE` 错误响应。

场景命令返回的 ObjectRef / ComponentRef 应原样回传；新 ComponentRef 的 `exactType=true` 表示索引按精确 runtime type 计算。`set_game_view_resolution` 会返回 `restore` 令牌，临时截图或验证完成后应把令牌原样回传以恢复 Game View 并删除本次新增的自定义尺寸。

## 扩展(写新命令)

```csharp
using AgentBridge;
using Newtonsoft.Json.Linq;

public sealed class MyHandler : ICommandHandler
{
    public string Command => "my_cmd";
    public string Description => "这个命令做什么";        // 供 list_commands 展示给 AI
    public string Group => "Custom";                      // 管理器窗口里的功能分组
    public bool CanDisable => true;                       // 是否允许在命令管理器禁用
    public CommandBatchMode BatchMode => CommandBatchMode.Allowed;
    public async CommandTask<object> ExecuteAsync(JObject @params) => new { ok = true };
    public JObject ParamsSchema { get; } = JObject.Parse(@"{ ""type"":""object"" }"); // 必选:参数 schema,无参返回 new JObject()(空 {})
    // 抛 CommandException(code, msg) 产生自定义错误码;抛其他异常 → HANDLER_EXCEPTION。
}
```
放进任意被编译的程序集即自动注册,无需改框架。每个 handler 通过 `CanDisable` 自行声明是否允许在命令管理器禁用；协议必需的 `ping` 与 `list_commands` 返回 `false`。当前包不维护 `extension.json` 本地安装/卸载协议;扩展代码通过 UPM 或工程程序集添加、移除。
每个 handler 必须显式声明 `BatchMode`：`NotAllowed` 禁止作为 batch 子命令，`Allowed` 允许进入 batch，
`AllowedWithUndoCollapse` 还允许并入 batch 的单一 Undo 组，并要求 handler 自身完整遵守 Unity Undo 契约。

## 自动验收

保持 Unity 编辑器打开，在仓库根目录运行：

```powershell
./scripts/Test-AgentBridge.ps1 -ProjectPath "G:\path\to\UnityProject" -Suite Baseline
./scripts/Test-AgentBridge.ps1 -ProjectPath "G:\path\to\UnityProject" -Suite Mutating
./scripts/Test-AgentBridge.ps1 -ProjectPath "G:\path\to\UnityProject" -Suite Full
```

报告写入目标工程 `.agentbridge/test-results/`。`Full` 会保存/刷新资源，使用包内 `Tests/AgentBridge.Tests.asmdef` 唯一的 Editor 测试程序集验证测试命令，然后请求真实重编译；协议只允许单通讯，运行时不要并发写请求。

嵌入式开发包会自动启用该测试程序集；Git/registry 安装版本需要先把 `me.xw.unityagentbridge` 加入目标工程 `Packages/manifest.json` 的 `testables` 数组。

## 运行时验收(在 Unity 中执行)

对应 design 第 3 节关键场景:

1. **正常往返**:`Start` 后,原子发布 `request.json`(command=ping)→ `response.json` 出现,`status=ok`、`result.message="pong"`、响应 `id` 与请求一致；完整读取并等待 `processing.json` 消失后删除 `response.json`。
2. **非法 id**:请求 JSON 的 `id` 为空、类型错误或超过 64 字符 → 不执行命令；`response.json` 返回 `error.code=INVALID_REQUEST`、`id=""`。
3. **未知命令**:command=`nope` → 响应 `error.code=UNKNOWN_COMMAND`。
4. **handler 异常**:临时加一个 `ExecuteAsync` 抛 `new System.Exception("boom")` 的测试 handler(验完删除,勿提交)→ 响应 `error.code=HANDLER_EXCEPTION`,message 含堆栈摘要。
5. **半截文件**:只写 `request.json.tmp` 不 rename → 无任何响应；rename 为 `request.json` 后才处理。
6. **认领单次**:单个请求只产生一份响应，提交后 `processing.json` 不残留。
7. **单通讯约束**:Agent 必须完整读取当前 `response.json`、等待 `processing.json` 消失、再删除响应，之后才可发送下一请求；临时请求不会被认领。
8. **domain reload 中断**:请求进入 `processing.json` 后触发重编译(改任意脚本)→ 重启后该 id 收到 `error.code=INTERRUPTED`,不重复执行。
