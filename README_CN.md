# Unity Agent Bridge

> 让 AI Agent 通过 JSON 文件驱动 Unity 编辑器 —— 请求/响应、轮询主机、可扩展命令框架。

[English](README.md) | **简体中文**

---

Unity Agent Bridge 是一个**仅编辑器**的 Unity 包,通过**文件通讯(file-IPC)**把 Unity 编辑器暴露给外部 AI Agent。Agent 一次发布一个请求 JSON；编辑器认领后在主线程执行对应命令，再发布一个响应 JSON。Agent 完整读取响应后先等待 `processing.json` 消失，再显式删除 `response.json` 作为确认。无 socket、无原生插件——只用文件。

## 为什么用文件通讯

- **零网络** —— 任何能读写文件夹的进程都能接(命令行 Agent、脚本、其他程序)。
- **抗中断** —— 原子发布(`request.json.tmp` → `request.json`)+ 原子 Claim(`request.json` → `processing.json`)；已认领请求至多处理一次。
- **主线程执行** —— handler 跑在 `EditorApplication.update` 回调里,可直接调任意 Unity API。

## 工作原理

```
agent ──> .agentbridge/request.json.tmp ──rename──> request.json
                                                        │
                                         原子认领：move 到 processing.json
                                                        │
                                      编辑器主机在主线程分发 handler
                                                        │
agent <── .agentbridge/response.json <──rename── response.json.tmp
   │
   └── 完整读取 → 等待 processing.json 消失 → 删除 response.json 确认
```

协议是严格单通讯：Agent 发布一条请求，等待并完整读入响应，等待 Unity 删除 `processing.json`，再删除 `response.json` 确认已消费，然后才能发布下一条请求。响应必须保留到 claim 清理完成，避免在响应发布与清理之间发生 reload 时把已完成命令误判为中断。临时文件会被忽略。固定槽位文件名就是协议，不兼容旧的按 id 分目录布局。

**请求信封**

```json
{ "v": 1, "id": "abc", "command": "ping", "params": {} }
```

`id` 只存在于 JSON 信封中，文件名固定。每条请求都使用一个从未用过的非空字符串 id，最长
64 个字符，并校验响应 id 与请求一致。缺少或非法的 `v`、`id`、`command` 以及 JSON 格式错误
都会返回 `INVALID_REQUEST`。如果非法请求没有可用 id，响应使用 `"id":""`；固定的
`response.json` 槽位仍能关联本次 exchange。单个请求上限为 1 MiB，`params` 必须是 object。
命令执行前会按 `list_commands` 返回的 `paramsSchema` 严格校验类型、必填项、枚举和边界。

**响应信封**(`status: ok` → `result`;`status: error` → `error`;每条响应都盖 `commandsVersion`)

```json
{ "v": 1, "id": "abc", "status": "ok", "result": { "message": "pong", "unityVersion": "6000.3.12f1" },
  "error": null, "commandsVersion": "4bd2f89c8d94a01b", "timestamp": "..." }
```

响应按 UTF-8 计算固定上限为 1 MiB。命令结果超限时会改为紧凑的 `RESPONSE_TOO_LARGE` 错误；
请缩小 `root`、`maxDepth`、`limit` 等查询范围，并使用新 id 重试。

## 内置命令

包当前包含以下内置命令，按 `ICommandHandler.Group` 分组：

- **Meta**——`ping`、`list_commands`、`list_agent_methods`
- **Inspection**——`get_hierarchy`、`get_object`、`get_selection`、`get_asset`、`get_asset_dependencies`、`list_assets`
- **Scenes**——`list_scenes`、`open_scene`、`save_scene`、`close_scene`、`set_active_scene`
- **Mutation**——`create_object`、`update_object`、`delete_object`、`add_component`、`remove_component`、`set_property`、`set_selection`、`frame_object`、`set_game_view_resolution`、`invoke_menu`、`invoke_agent_method`、`undo`、`redo`、`batch`
- **Prefab**——`prefab`
- **Assets**——`create_asset`、`import_asset`、`move_asset`、`delete_asset`、`set_importer_property`、`refresh`
- **PlayMode**——`play_scene`、`pause`、`resume`、`step`
- **Capture**——`capture_game_view`、`capture_scene_view`
- **Console**——`search_logs`、`clear_logs`
- **Compilation**——`recompile`、`get_compile_result`
- **Profiling**——`get_profiler_overview`、`get_profiler_data`、`compare_profiler_windows`、`capture_profiler`
- **Testing**——`run_tests`、`get_test_result`

Capture 命令使用可配置 `quality`（默认 85）的 JPG 编码，并在开始捕获前清理 `.agentbridge/screenshots/` 中的旧截图和截图临时文件；连续截图只在整批开始时清理一次。

Profiler 工作流分为四步：`capture_profiler` 自动录制实际帧并保存稳定 `captureId`，结果会区分录制期间观察到的 `observed` 与最终快照仍保留的 `retained`，只有后者达到请求数量时 `complete=true`；`get_profiler_overview` 只读帧级 P50/P95/P99、最慢帧与自动 spike，不扫描调用树；`get_profiler_data` 按单线程或分页多线程聚合 CPU 热点，支持 Category、阈值及按需分布/趋势；`compare_profiler_windows` 比较当前或两个已保存 capture 的 baseline/candidate 窗口。读命令临时加载 `captureId` 后会恢复原 Profiler 缓冲。`frameCount=1` 仍用于单帧深入分析，精确参数与 schema 以运行时 `list_commands` 为准。这些命令不包含 Memory Profiler、GPU Timeline 或自动改代码。

以上列表仅用于包能力概览。`list_commands` 仍是命令集的 canonical interface：它返回当前启用的命令、描述、参数 schema、batch policy 与 `commandsVersion`；Agent 提示词和集成代码不应复制这些 metadata。

源码导航：`Channel/` 负责文件 exchange，`Dispatch/` 负责命令发现与调用，`Commands/` 负责 Unity 操作；AgentCallable 特性、目录和 Handler 位于 `Commands/Mutation/`。`Scene/` 负责可往返引用和序列化属性，`Testing/` 负责异步测试运行。

## 安装

本仓库的包在 `Unity/` 子目录(`me.xw.unityagentbridge`,需 **Unity 2021.3+**、`com.unity.nuget.newtonsoft-json` 与 `com.unity.test-framework`)。

- **Git(UPM)**:Package Manager → *Add package from git URL*:
  ```
  https://github.com/XuToWei/UnityAgentBridge.git?path=Unity
  ```

首次安装后，打开 `Window ▸ Agent Bridge` 并点击**启用桥接**；此时才会创建桥接根目录 `<工程>/.agentbridge/` 并启动主机。启用状态会按工程持久化：Domain Reload 仅在此前已启用且 Bridge root 仍存在时恢复主机；手动停止后，后续 reload 会保持停止。临时协议槽位为 `request.json`、`processing.json`、`response.json`。

## 自动验收

编辑器已经打开且桥接目录存在时，可从仓库根目录运行：

```powershell
./scripts/Test-AgentBridge.ps1 -ProjectPath "G:\path\to\UnityProject" -Suite Baseline
./scripts/Test-AgentBridge.ps1 -ProjectPath "G:\path\to\UnityProject" -Suite Mutating
./scripts/Test-AgentBridge.ps1 -ProjectPath "G:\path\to\UnityProject" -Suite Full
```

`Baseline` 覆盖只读与失败前置校验；`Mutating` 在 PlayMode 和唯一临时场景/资产目录中验证写命令并清理；`Full` 还会执行 `refresh`，使用 `Unity/Tests` 下唯一的 Editor 测试程序集 `AgentBridge.Tests` 验证 `run_tests` / `get_test_result`，然后请求真实重编译。JSON 报告写入工程的 `.agentbridge/test-results/`。协议是单通讯，测试期间不要让其它进程或 Agent 同时写 `.agentbridge/request.json`。

嵌入式开发包会被 Unity 自动启用包测试；若验证的是 Git/registry 安装版本，请先把 `me.xw.unityagentbridge` 加入目标工程 `Packages/manifest.json` 的 `testables` 数组，以便 Unity 编译该 Editor 测试程序集，再运行 `Full`。

场景对象响应中的 `path` 是可往返的规范路径：每个 GameObject 名称段先把 `~` 编码为 `~0`、`/` 编码为 `~1`，空名称编码为 `~2`。优先复用命令返回的完整 ObjectRef（`path + instanceId + scenePath`），不要手拼；解析器会交叉校验提示并拒绝陈旧 instanceId。

## 命令管理器

`Window ▸ Agent Bridge` 用 Unity `TypeCache` 列出所有命令(内置 + 扩展),按**功能分组**(`ICommandHandler.Group`),表头点击排序、分组筛选、批量启停;顶部工具条启停桥接主机、切换后台运行。Exchange 执行期间不能停止桥接；终态响应发布后，停止开关会重新可用。任意命令可打勾启停——被禁用的命令**从 `list_commands` 隐藏**、分发时返回 `COMMAND_DISABLED`(禁用名单存 `EditorPrefs`,按工程命名空间隔离)。每个 handler 通过 `CanDisable` 自行声明策略；协议必需的 `ping` 与 `list_commands` 返回 `false`。

窗口的 `AgentCallable` 页签会列出每个有效方法的完整 ID、描述和建议超时。可以搜索 ID 或描述，并用每项的**执行**按钮直接调用；窗口会显示执行中、成功或包含错误码的失败信息，执行期间不会并发启动另一个方法。

## 扩展 Bridge

选择能够满足操作需求的最小扩展接口：无参操作使用 `AgentCallable`，需要完整命令契约时实现 `ICommandHandler`。

### 暴露无参方法

项目专用的 Editor 操作如果不需要参数或结构化结果，可以使用 `AgentCallable`。添加该特性即表示明确授权 Agent 调用这个方法。

**适用场景：**

- **项目专用的一键操作**：例如重建导航数据、烘焙光照、生成项目资源或执行自定义发布准备流程。
- **Agent 驱动的流程验证**：把 Arrange、Act 和 Assert 串成一次确定的场景级验证、集成检查或 smoke test。
- **已有工具的轻量入口**：为现有 Editor 工具或 Runtime 逻辑提供一个无参静态包装，让 Agent 无需新增完整命令即可触发。
- **无需返回数据的异步任务**：方法可返回 `Task`、`ValueTask`、`UniTask` 或其他 Awaitable，Bridge 会等待其完成并报告异常。

如果每次调用需要不同参数、需要把结构化数据返回给 Agent，或需要 Schema、Batch、Undo 等命令策略，应实现 `ICommandHandler`。需要参数矩阵、标准测试报告或长期 CI 回归时，应使用 Unity Test Framework。

Agent 驱动的流程测试、场景级验证和 smoke test 是一个推荐场景。用户要求编写这类测试代码时，Agent 可以在目标工程现有的 Editor 程序集中创建一个自包含的 `AgentCallable` 方法，把 Arrange、Act 和 Assert 连成一个确定流程，等待 Unity 编译后再通过发现结果调用它。方法正常结束表示通过；检查失败必须抛出包含步骤、期望值和实际值的异常。测试应使用隔离的临时状态，并在 `finally` 中恢复和清理现场。需要参数矩阵、标准测试报告或长期 CI 回归时，仍应使用 Unity Test Framework；需要结构化输入或结果时则实现 `ICommandHandler`。

```csharp
using AgentBridge;
using System.Threading.Tasks;

public static class ProjectAgentMethods
{
    [AgentCallable("重新生成当前场景的导航数据", 300)]
    private static Task RebuildNavigation()
    {
        return ProjectNavigation.RebuildAsync();
    }
}
```

构造函数第一个参数是展示给 Agent 的方法说明。第二个参数 `timeoutSeconds` 可选，默认 30，取值范围为 1..3600。

Agent 先调用 `list_agent_methods`，再把返回的 `DeclaringType.FullName::MethodName` ID 传给 `invoke_agent_method`。

`timeoutSeconds` 只决定 Agent 等待 Exchange 的时长，不会取消 Unity 任务或它返回的 Awaitable。等待超时后，Agent 必须继续监控同一个 Exchange，不能发布新请求。

可调用方法必须遵守以下规则：

- 方法必须是无参、非泛型 `static`；允许 public 或 private。
- 同步返回值会被忽略；公开实例 `GetAwaiter()` 的返回类型会通过 `dynamic` 等待，其结果随后被忽略。无需增加特定包依赖即可支持 `Task`、`ValueTask`、`UniTask` 及其泛型形式。
- `async void` 无法可靠观察完成状态和异常，因此不会注册。
- 调用不会进入 Batch，也不提供自动 Undo、dirty、save 或资源路径处理。
- 该特性仅用于 Editor。调用 Runtime 逻辑时，应在 Editor 程序集中添加一层静态包装。

### 实现完整命令

操作需要参数、结构化结果、Schema 校验、命令级启停行为或明确的 Batch/Undo 策略时，实现 `ICommandHandler`。

```csharp
using AgentBridge;
using Newtonsoft.Json.Linq;
using System.Threading.Tasks;

public sealed class SayHelloHandler : ICommandHandler
{
    public string Command => "say_hello";
    public string Description => "returns a greeting";
    public string Group => "Custom";        // 窗口里的功能分组
    public bool CanDisable => true;
    public CommandBatchMode BatchMode => CommandBatchMode.Allowed;
    public Task<object> ExecuteAsync(JObject @params)
    {
        return Task.FromResult<object>(new { greeting = "hi " + @params?["name"]?.Value<string>() });
    }
    public JObject ParamsSchema { get; } = new JObject(); // 无参返回空 {}
}
```

`TypeCache` 会自动发现 `ICommandHandler` 实现，无需注册特性或手动接线。

Handler 需要定义唯一的 `Command`，以及 `Description`、`Group`、`CanDisable`、`BatchMode`、`ExecuteAsync` 和 `ParamsSchema`。

`ExecuteAsync` 返回 `Task<object>`，支持普通 `async`/`await`。抛出 `CommandException(code, message)` 可返回带类型的错误。

`BatchMode` 可选 `NotAllowed`、`Allowed` 或 `AllowedWithUndoCollapse`。最后一种要求 Handler 完整遵守 Unity Undo 契约。

命令管理器中的启用/禁用控制已注册命令的运行时可用性。

---

## License

See repository.
