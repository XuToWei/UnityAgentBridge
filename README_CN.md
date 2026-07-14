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

包提供以下能力组：

- **发现与检查**——连通性、命令 metadata、层级、对象、选择、场景、资产、依赖、Console、编译和测试结果。
- **场景与 PlayMode 控制**——打开/保存/关闭/激活场景，运行/停止，暂停/恢复/单帧，Game View 分辨率与截图。
- **场景修改**——创建/更新/删除对象、组件和序列化属性，选择、框选、Prefab、Undo/Redo、菜单与非原子 batch。
- **资产修改**——创建/导入/移动/删除资产，修改 Importer，刷新并请求重编译。
- **测试**——启动带过滤条件的 EditMode/PlayMode 测试并轮询限量结果。

`list_commands` 是命令集的 canonical interface。它返回当前启用的命令、描述、参数 schema、batch policy 与 `commandsVersion`；Agent 提示词和集成代码不应复制这些 metadata。常用入口包括 `ping`、`list_commands`、`get_hierarchy`、`create_object`、`batch`、`run_tests` 与 `get_test_result`。

源码导航：`Channel/` 负责文件 exchange，`Dispatch/` 负责命令发现与调用，`Commands/` 负责 Unity 操作，`Scene/` 负责可往返引用和序列化属性，`Testing/` 负责异步测试运行。

## 安装

本仓库的包在 `Unity/` 子目录(`me.xw.unityagentbridge`,需 **Unity 2021.3+**、`com.unity.nuget.newtonsoft-json` 与 `com.unity.test-framework`)。

- **Git(UPM)**:Package Manager → *Add package from git URL*:
  ```
  https://github.com/XuToWei/UnityAgentBridge.git?path=Unity
  ```

主机随加载自启。桥接根目录默认 `<工程>/.agentbridge/`；临时协议槽位为 `request.json`、`processing.json`、`response.json`。

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

`Window ▸ Agent Bridge` 用 Unity `TypeCache` 列出所有命令(内置 + 扩展),按**功能分组**(`ICommandHandler.Group`),表头点击排序、分组筛选、批量启停;顶部工具条启停桥接主机、切换后台运行。任意命令可打勾启停——被禁用的命令**从 `list_commands` 隐藏**、分发时返回 `COMMAND_DISABLED`(禁用名单存 `EditorPrefs`,按工程命名空间隔离)。每个 handler 通过 `CanDisable` 自行声明策略；协议必需的 `ping` 与 `list_commands` 返回 `false`。

## 添加自定义命令

```csharp
using AgentBridge;
using Newtonsoft.Json.Linq;

public sealed class SayHelloHandler : ICommandHandler
{
    public string Command => "say_hello";
    public string Description => "returns a greeting";
    public string Group => "Custom";        // 窗口里的功能分组
    public bool CanDisable => true;
    public CommandBatchMode BatchMode => CommandBatchMode.Allowed;
    public object Execute(JObject @params) => new { greeting = "hi " + @params?["name"]?.Value<string>() };
    public JObject ParamsSchema { get; } = new JObject(); // 无参返回空 {}
}
```

`ICommandHandler` 实现经反射 / `TypeCache` 自动注册,无需手动接线或注册特性。成员:`Command`(唯一名)、`Description`、`Group`(窗口分组)、`CanDisable`、`BatchMode`、`Execute`、`ParamsSchema`。`BatchMode` 可选 `NotAllowed`、`Allowed` 或 `AllowedWithUndoCollapse`。抛 `CommandException(code, message)` 返回带类型的错误。

当前扩展 Seam 只有 `ICommandHandler`;包不维护 `extension.json` 本地安装/卸载协议。请通过 UPM 或工程程序集添加、移除扩展代码。

---

## License

See repository.
