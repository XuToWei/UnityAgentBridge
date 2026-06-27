# Unity Agent Bridge

让 AI Agent 通过**文件**驱动 Unity 编辑器执行命令。请求/响应 JSON 文件 + `EditorApplication.update` 轮询 + 可扩展的 `ICommandHandler` 框架。

> file-bridge roadmap 的产物:协议 + 文件通道 + handler 框架 + 宿主 + 内置 `ping` / `list_commands`(命令发现)。inspection / mutation / assets / csharp 命令与扩展管理器是后续 feature。驱动桥接见 [`AGENT.md`](AGENT.md)。

## 安装

UPM 通过 git URL 安装(本包在仓库子目录 `Unity/`):

```
https://github.com/<owner>/UnityAgentBridge.git?path=/Unity
```

依赖 `com.unity.nuget.newtonsoft-json`(已声明在 package.json,UPM 自动拉取)。

## 启动

加载后宿主经 `[InitializeOnLoad]` 自动启动,也可在 `Window/Agent Bridge Window` 顶部工具条启停 / 切失焦不节流。

默认文件根目录:`<UnityProject>/AgentBridge/`,含 `requests/` `processing/` `responses/`。
可经 `AgentBridge.BridgeSettings.RootDir` / `PollIntervalMs`(EditorPrefs)调整。

## 驱动协议(面向 AI Agent)

请求/响应 JSON schema、错误码、`list_commands` 发现机制与 `commandsVersion` 缓存刷新规矩,以及可粘贴进项目 `CLAUDE.md` 的元知识片段,见 **[`AGENT.md`](AGENT.md)**(本包内,面向驱动桥接的 AI)。

要点:写请求务必原子(先写 `{id}.request.json.tmp` 再 rename);每条响应带 `commandsVersion`;AI 启动应先调 `list_commands` 并按 version 变化刷新。

## 扩展(写新命令)

```csharp
using AgentBridge;
using Newtonsoft.Json.Linq;

public sealed class MyHandler : ICommandHandler
{
    public string Command => "my_cmd";
    public string Description => "这个命令做什么";        // 供 list_commands 展示给 AI
    public string Group => "Custom";                      // 管理器窗口里的功能分组
    public bool CanDisable => true;                       // false = 锁定常开(协议刚需命令)
    public object Execute(JObject @params) => new { ok = true };
    public JObject GetParamsSchema() => JObject.Parse(@"{ ""type"":""object"" }"); // 必选:参数 schema,无参返回 new JObject()(空 {})
    // 抛 CommandException(code, msg) 产生自定义错误码;抛其他异常 → HANDLER_EXCEPTION。
}
```
放进任意被编译的程序集即自动注册,无需改框架。

## 运行时验收(在 Unity 中执行)

对应 design 第 3 节关键场景:

1. **正常往返**:`Start` 后,向 `requests/` 写 `x1.request.json`(command=ping)→ `responses/x1.response.json` 出现,`status=ok`、`result.message="pong"`、`id="x1"`。
2. **未知命令**:command=`nope` → 响应 `error.code=UNKNOWN_COMMAND`。
3. **handler 异常**:临时加一个 `Execute` 抛 `new System.Exception("boom")` 的测试 handler(验完删除,勿提交)→ 响应 `error.code=HANDLER_EXCEPTION`,message 含堆栈摘要。
4. **半截文件**:只写 `x2.request.json.tmp` 不 rename → 无任何响应;rename 后才处理。
5. **认领单次**:单个请求只产生一份响应,`processing/` 不残留。
6. **domain reload 中断**:请求进入 `processing/` 后触发重编译(改任意脚本)→ 重启后该 id 收到 `error.code=INTERRUPTED`,不重复执行。
7. **配置生效**:改 `RootDir` / `PollIntervalMs` 后行为随之变化。
