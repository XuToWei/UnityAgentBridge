# Unity Agent Bridge

> 让 AI Agent 通过 JSON 文件驱动 Unity 编辑器 —— 请求/响应、轮询主机、可扩展命令框架。

[English](README.md) | **简体中文**

---

Unity Agent Bridge 是一个**仅编辑器**的 Unity 包,通过**文件通讯(file-IPC)**把 Unity 编辑器暴露给外部 AI Agent。Agent 写一个请求 JSON 文件;编辑器内的轮询主机认领它、在主线程执行对应命令、再写回一个响应 JSON 文件。无 socket、无原生插件——只用文件。

## 为什么用文件通讯

- **零网络** —— 任何能读写文件夹的进程都能接(命令行 Agent、脚本、其他程序)。
- **抗中断** —— 原子写(`*.tmp` → rename)+ 单次认领(`requests → processing` 原子 rename),每个请求至多处理一次。
- **主线程执行** —— handler 跑在 `EditorApplication.update` 回调里,可直接调任意 Unity API。

## 工作原理

```
agent ──> .agentbridge/requests/{id}.request.json
                      │  (原子认领:rename 到 processing/)
        编辑器主机(EditorApplication.update 轮询)
                      │  分发 → 主线程上的 handler
agent <── .agentbridge/responses/{id}.response.json
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

## 内置命令

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
| `recompile` | 触发脚本重编译(立即返回,结果经 `get_compile_result` 读) |
| `get_compile_result` | 读最近一次编译结果(`errors[]` / `warnings[]` + 计数) |
| `search_logs` | 搜索 Console 日志(子串或正则),按类型过滤、限制数量;返回匹配条目及 message/type/file/line |

运行时用 `list_commands` 取实时命令集——它精确反映已注册且启用的命令;`commandsVersion`(内容 hash)在命令集变化时改变,Agent 可据此缓存与失效。

## 安装

本仓库的包在 `Unity/` 子目录(`me.xw.unityagentbridge`,需 **Unity 2021.3+** 与 `com.unity.nuget.newtonsoft-json`)。

- **Git(UPM)**:Package Manager → *Add package from git URL*:
  ```
  https://github.com/XuToWei/UnityAgentBridge.git?path=Unity
  ```

主机随加载自启。桥接根目录默认 `<工程>/.agentbridge/`(`requests/` `processing/` `responses/`)。

## 命令管理器

`Window ▸ Agent Bridge` 用 Unity `TypeCache` 列出所有命令(内置 + 扩展),按**功能分组**(`ICommandHandler.Group`),表头点击排序、分组筛选、批量启停;顶部工具条启停桥接主机、切换后台运行。任意命令可打勾启停——被禁用的命令**从 `list_commands` 隐藏**、分发时返回 `COMMAND_DISABLED`(禁用名单存 `EditorPrefs`,按工程命名空间隔离)。必须命令(`CanDisable == false`,如 `ping` / `list_commands`)锁定为常开。

## 添加自定义命令

```csharp
using AgentBridge;
using Newtonsoft.Json.Linq;

public sealed class SayHelloHandler : ICommandHandler
{
    public string Command => "say_hello";
    public string Description => "returns a greeting";
    public string Group => "Custom";        // 窗口里的功能分组
    public bool CanDisable => true;          // false = 锁定常开(协议刚需命令)
    public object Execute(JObject @params) => new { greeting = "hi " + @params?["name"]?.Value<string>() };
    public JObject GetParamsSchema() => new JObject(); // 无参返回空 {}
}
```

`ICommandHandler` 实现经反射 / `TypeCache` 自动注册,无需手动接线、无需特性。成员:`Command`(唯一名)、`Description`、`Group`(窗口分组)、`CanDisable`(false 则锁定常开)、`Execute`、`GetParamsSchema`。抛 `CommandException(code, message)` 返回带类型的错误。

---

## License

See repository.
