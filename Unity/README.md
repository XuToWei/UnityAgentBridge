# Unity Agent Bridge

让 AI Agent 通过**文件**驱动 Unity 编辑器执行命令。请求/响应 JSON 文件 + `EditorApplication.update` 轮询 + 可扩展的 `ICommandHandler` 框架。

包内已包含协议/文件通道、命令发现与管理器，以及 scenes、inspection、mutation、prefab、assets、PlayMode、capture、console、compilation、profiling、testing 等内置命令。驱动桥接见 [`AGENT.md`](AGENT.md)；实际可用命令始终以运行时 `list_commands` 为准。

## 安装

UPM 通过 git URL 安装(本包在仓库子目录 `Unity/`):

```
https://github.com/XuToWei/UnityAgentBridge.git?path=Unity
```

依赖 `com.unity.nuget.newtonsoft-json` 与 `com.unity.test-framework`(已声明在 package.json,UPM 自动拉取)。

## 启动

首次安装后,打开 `Window/Agent Bridge` 并点击**启用桥接**；此时才会创建 `.agentbridge` 并启动宿主。启用状态会按工程持久化：Domain Reload 仅在此前已启用且 Bridge root 仍存在时恢复宿主；手动停止后，后续 reload 会保持停止。顶部工具条也可停止桥接 / 切失焦不节流；Exchange 执行期间停止开关会禁用，终态响应发布后恢复。

窗口的 `AgentCallable` 页签显示有效方法的完整 ID、描述和建议超时，支持按 ID/描述搜索并通过**执行**按钮直接调用。页签会显示执行中、成功或带错误码的失败信息，并阻止多个方法并发执行。

默认文件根目录:`<UnityProject>/.agentbridge/`。协议直接使用固定槽位 `request.json`、`processing.json`、`response.json`；这些文件只在 exchange 对应阶段存在。

## 驱动协议(面向 AI Agent)

请求/响应 JSON schema、错误码、`list_commands` 发现机制与 `commandsVersion` 缓存刷新规矩,以及可粘贴进项目 `CLAUDE.md` 的元知识片段,见 **[`AGENT.md`](AGENT.md)**(本包内,面向驱动桥接的 AI)。

要点:写请求务必原子(先写 `request.json.tmp` 再 rename 为 `request.json`)；Unity 用原子 move 将其认领为 `processing.json`；响应通过 `response.json.tmp` 原子发布为 `response.json`。Agent 完整读取响应后必须先等待 `processing.json` 消失，再删除 `response.json` 作为 ack，之后才能发送下一条。id 只存在于 envelope，必须非空、最长 64 字符且每次使用全新值；每条响应带 `commandsVersion`；AI 启动应先调 `list_commands` 并按 version 变化刷新。
`list_commands` 的每项还会返回 `batchAllowed` 与 `supportsUndoCollapse`，不要靠命令名猜 batch 能力。

请求上限为 1 MiB，`params` 必须是 object，并会在执行 handler 前按该命令的 `paramsSchema` 校验。
响应按 UTF-8 计算固定上限为 1 MiB；超限结果会改为紧凑的 `RESPONSE_TOO_LARGE` 错误响应。

Profiler 工作流包含 `capture_profiler`、`get_profiler_overview`、`get_profiler_data` 与 `compare_profiler_windows`：自动录制并保存稳定 `captureId`（响应区分 observed/retained，只有最终快照保留足量帧时 complete=true），快速定位 P50/P95/P99、慢帧和 spike，再按线程/Category/阈值深入热点，或比较当前/已保存 capture 的 baseline/candidate 窗口。读命令临时加载 `captureId` 后会恢复原 Profiler 缓冲。`frameCount=1` 用于单帧深入分析；精确参数与 schema 以运行时 `list_commands` 为准。这些命令不等同于 Memory Profiler 或 Profile Analyzer。

截图命令使用可配置 `quality`（默认 85）的 JPG 编码，并在开始捕获前清理 `.agentbridge/screenshots/` 中的旧截图和截图临时文件；连续截图只在整批开始时清理一次。

场景命令返回的 ObjectRef / ComponentRef 应原样回传；新 ComponentRef 的 `exactType=true` 表示索引按精确 runtime type 计算。`set_game_view_resolution` 会返回 `restore` 令牌，临时截图或验证完成后应把令牌原样回传以恢复 Game View 并删除本次新增的自定义尺寸。

## 扩展(写新命令)

只需暴露无参静态方法时，可使用轻量特性。

`AgentCallable` 适用于以下场景：

- **项目专用的一键操作**：例如重建导航数据、烘焙光照、生成项目资源或执行自定义发布准备流程。
- **Agent 驱动的流程验证**：把 Arrange、Act 和 Assert 串成一次确定的场景级验证、集成检查或 smoke test。
- **已有工具的轻量入口**：为现有 Editor 工具或 Runtime 逻辑提供无参静态包装，无需实现完整命令。
- **无需返回数据的异步任务**：可返回 `Task`、`ValueTask`、`UniTask` 或其他 Awaitable，由 Bridge 等待完成并报告异常。

调用需要参数、结构化返回值、Schema、Batch 或 Undo 策略时，应实现 `ICommandHandler`；需要参数矩阵、标准测试报告或长期 CI 回归时，应使用 Unity Test Framework。

流程测试、场景级验证和 smoke test 是推荐用法：在用户要求编写测试代码时，Agent 可在目标工程
已有的 Editor 程序集中生成自包含的 `AgentCallable` 方法，把 Arrange、Act、Assert 连成一个
确定流程，等待编译后通过 `list_agent_methods` 发现并调用。正常结束表示通过；检查失败应抛出
包含步骤、期望值和实际值的异常。测试使用隔离的临时状态，并在 `finally` 中恢复和清理现场。
参数矩阵、标准测试报告和长期 CI 回归仍使用 Unity Test Framework；结构化输入或结果使用完整
handler。

```csharp
using AgentBridge;

public static class ProjectAgentMethods
{
    [AgentCallable("重新生成当前场景的导航数据", 300)]
    private static void RebuildNavigation()
    {
        // Unity Editor 操作
    }
}
```

`list_agent_methods` 返回说明、自动 ID `DeclaringType.FullName::MethodName` 和 `timeoutSeconds`
（默认 30，范围 1..3600），`invoke_agent_method` 按完整 ID 调用。Agent 应按该值等待响应；
它不会取消 Unity 侧 Awaitable。方法必须是无参、非泛型 `static`；同步返回值会被忽略，
公开实例 `GetAwaiter()` 的返回类型会通过 `dynamic` 等待完成后忽略结果，因此无需包依赖即可支持
`Task`、`ValueTask`、`UniTask` 及其泛型形式。`async void` 不注册。该调用不允许进入 batch，
方法自己负责 Undo、dirty/save 和资源路径安全。
特性属于 Editor 程序集；调用 Runtime 逻辑时，在 Editor 程序集中添加一层静态包装。

需要参数、结构化结果或 batch/Undo 策略时，实现完整 handler：

```csharp
using AgentBridge;
using Newtonsoft.Json.Linq;
using System.Threading.Tasks;

public sealed class MyHandler : ICommandHandler
{
    public string Command => "my_cmd";
    public string Description => "这个命令做什么";        // 供 list_commands 展示给 AI
    public string Group => "Custom";                      // 管理器窗口里的功能分组
    public bool CanDisable => true;                       // 是否允许在命令管理器禁用
    public CommandBatchMode BatchMode => CommandBatchMode.Allowed;
    public Task<object> ExecuteAsync(JObject @params)
    {
        return Task.FromResult<object>(new { ok = true });
    }
    public JObject ParamsSchema { get; } = JObject.Parse(@"{ ""type"":""object"" }"); // 必选:参数 schema,无参返回 new JObject()(空 {})
    // 抛 CommandException(code, msg) 产生自定义错误码;抛其他异常 → HANDLER_EXCEPTION。
}
```
放进任意被编译的程序集即自动注册,无需改框架。每个 handler 通过 `CanDisable` 自行声明是否允许在命令管理器禁用；协议必需的 `ping` 与 `list_commands` 返回 `false`。命令管理器中的启用/禁用控制已注册命令的运行时可用性。
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
