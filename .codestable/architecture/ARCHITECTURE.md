# UnityAgentBridge 架构总入口

> 状态：演进中
> 创建日期：2026-06-24
> 最近更新：2026-06-26（cmd-compile-check 验收归并:加编译自检子系统 recompile/get_compile_result)

## 1. 项目简介

让 AI Agent 通过桥接控制 Unity 编辑器 / 运行时。Agent 与 Unity 之间通过**文件**通讯,而非 HTTP,以规避 HTTP 方案带来的问题（端口占用、防火墙、连接生命周期、编辑器域重载导致的连接中断等）。

仓库布局:
```
UnityAgentBridge/
├── Unity/        纯 UPM 包(桥接本体),package.json + Editor/
└── Features/     可下载的远程特性/扩展(extension-manager 数据源,尚未实现)
```

## 2. 核心概念 / 术语表

| 术语 | 含义 |
|---|---|
| AgentBridge | 桥接系统统称,C# 命名空间 `AgentBridge` |
| Request / Response | Agent↔Unity 的 JSON 信封。Request `{v,id,command,params,timestamp}`;Response `{v,id,status,result,error,timestamp}`,status=ok 时 error=null、status=error 时 result=null |
| ErrorInfo | 响应错误体 `{code,message}` |
| Command / Handler | 一条可执行操作的名字 / 其处理器(实现 `ICommandHandler`) |
| Claim(认领) | Unity 把 `requests/` 文件原子 rename 到 `processing/` 以独占处理 |
| Domain reload | Unity 重编译后重置 AppDomain、丢失静态状态的事件 |
| ObjectRef / ComponentRef | 命令参数里引用 GameObject / Component 的统一 DTO(roadmap 4.5):`ObjectRef={path,instanceId}`、`ComponentRef={object,type,index}`。inspection 与 mutation 共享,由 `SceneObjectResolver` 解析(instanceId 优先,否则 path 跨已加载场景) |
| Hierarchy 节点 | 场景层级树的一个节点 `{name,path,instanceId,active,children}`,`get_hierarchy` 的返回单元 |

## 3. 子系统 / 模块索引

桥接本体(`Unity/` 包,Editor-only 程序集 `AgentBridge.Editor`)分六模块:

```
UnityAgentBridge
├── M1 协议消息模型   Request/Response/ErrorInfo/ErrorCodes(纯契约)
├── M2 文件通道       FileChannel:目录布局、原子写(tmp→rename)、认领、写响应
├── M3 命令处理器框架 ICommandHandler(Command/Description)反射自动注册(实现接口即注册,无特性)+ CommandDispatcher(扩展核心)
├── M4 Editor 宿主    AgentBridgeHost([InitializeOnLoad]+轮询)、BridgeSettings、AgentBridgeWindow 顶部工具条启停/失焦节流
├── M5 内置命令集     ping、list_commands(自省);get_*/list_assets(只读查询);set_property/invoke_menu/create_object/delete_object(场景写);import/create/move/delete_asset+refresh(资源操作);recompile/get_compile_result(编译自检)
│                     handler 按命令域分子目录:Commands/{Inspection,Mutation,Assets,Compilation}/(decision commands-category-subdirectory);元命令(ping/list_commands)留根
└── M6 Agent 侧约定   Unity/AGENT.md(面向 AI 的驱动协议 + 可粘贴 CLAUDE.md 片段)
```

**主循环**(M4 驱动):编辑器加载 / domain reload → `[InitializeOnLoad]` 启动宿主 → 扫 `processing/` 孤儿补 INTERRUPTED → 订阅 `EditorApplication.update` → 按 `PollIntervalMs` 节流扫 `requests/` → 原子认领到 `processing/` → 主线程内 `CommandDispatcher.Dispatch` → tmp→rename 写 `responses/` → 删 processing 文件。

**文件目录**(默认 `<UnityProject>/AgentBridge/`):`requests/`(Agent 写) `processing/`(认领中) `responses/`(Unity 写)。

### 命令管理器子系统(`Unity/Editor/Extensions/`,roadmap `extension-manager`)

建在 M3 框架之上的**用户工具**(非 bridge 命令、非 AI 调用):统一浏览/启停**所有命令**(内置+扩展),并卸载本地扩展。命令由 Unity `TypeCache` 发现;**扩展** = 用户放进 `Assets/AgentBridgeExtensions/{id}/` 的本地源码包(含 `extension.json`,须自带 Editor asmdef 才能引用 `AgentBridge.Editor`)。

```
Extensions/
├── CommandEntry / CommandCatalog   TypeCache.GetTypesDerivedFrom<ICommandHandler> 列所有命令 + 实例化取 Command/Description + Type.Assembly 来源 + 启停态 + 扩展归属
├── CommandToggle                   全局禁用名单(EditorPrefs,工程标识 key):SetEnabled/Reapply,推给 file-bridge CommandRegistry.SetDisabledCommands
├── CommandToggleBootstrap          [InitializeOnLoad] domain reload 后 Reapply 重建禁用名单
├── ExtensionManifest / InstalledMeta / LocalRegistry   manifest 模型 + 扫描(供命令→扩展归属与卸载;不再驱动启停)
├── ExtensionInstaller              Uninstall(删扩展目录 + Refresh)
└── AgentBridgeWindow              Window/Agent Bridge Window 菜单:顶部工具条(桥接启停 + 失焦不节流)+ 按来源(内置/各扩展/其它)分组列所有命令 + 逐命令启停 + 扩展项卸载 + 概览/过滤/滚动
```

**命令发现 + 启停**:`CommandCatalog.All()` 用 `TypeCache` 列出所有 `ICommandHandler`(含已禁用的),按 `Type.Assembly` 区分内置(`AgentBridge.Editor`)/扩展,交叉 `LocalRegistry`(manifest.commands)归属到扩展 id。启停 = `CommandToggle.SetEnabled(命令名, bool)` 改**全局禁用名单**(EditorPrefs,覆盖内置+扩展任意命令)→ `CommandRegistry.SetDisabledCommands` → 命令从 `list_commands`/`commandsVersion` 剔除、dispatch 返 `COMMAND_DISABLED`(file-bridge 过滤复用,**不重编译不删码**)。禁用名单进程内状态,domain reload 后 `CommandToggleBootstrap` 重应用。命令注册仍归 M3(反射自动)。

### 测试(`Unity/Tests/Editor/`,EditMode)

随包发布的 EditMode 测试程序集 `AgentBridge.Editor.Tests`(Editor-only,引用 `AgentBridge.Editor` + Unity Test Framework)。覆盖:
- **AI↔Unity 文件往返端到端**:`RoundTripTests` 用 `[UnityTest]` 协程驱动**真实** `EditorApplication.update`→`AgentBridgeHost.Tick`(临时 root + `PollIntervalMs=0`),测请求文件→响应文件全链路(ok/坏JSON/未知命令/单次认领/孤儿 INTERRUPTED)。
- **框架/命令/管理器**:命令经 `CommandDispatcher.Dispatch` 测,框架/管理器/共享层直测 API。`BridgeTestBase` 提供临时场景/资产/root 的自建自清。
- **零生产改动**:测试只读引用 + 公开 API(往返借真 Host,无可测性 seam)。
- 跑测试在含 `Assets/` + Unity Test Framework 包的宿主工程开 Test Runner(EditMode)。

### 编译自检子系统(`Editor/Compilation/` + `Commands/Compilation/`,cmd-compile-check)

让 AI 写完脚本后自检编译错误,形成"写代码→自检→修复"闭环。因**编译触发 domain reload 会打断在途请求**,做不到一条命令同步返回结果,故拆**异步两步**:
- `recompile` → `CompilationPipeline.RequestScriptCompilation()`,立即返回 `{requested:true}`(响应在 reload 前写出)。
- `get_compile_result` → 读最近编译结果 `{compiling, compiledAt, errorCount, warningCount, errors[], warnings[]}`(按 type 拆两数组)。
- `CompileMonitor`(`[InitializeOnLoad]`)订阅 `CompilationPipeline` 三事件,把 error/warning 收进 **`SessionState`**(跨 reload 存活、重启清);命令侧只读。
- AI 流程:写脚本 → `recompile` → 轮询 `get_compile_result` 到 `compiling:false` → 看错→修。编译报错不 reload(旧域保留);编译成功才 reload。

## 4. 关键架构决定

- **文件 IPC 而非 HTTP**:规避端口/防火墙/连接生命周期/domain reload 断连。
- **轮询(`EditorApplication.update`)而非 FileSystemWatcher**:跨平台稳定、不受 domain reload 影响、实现简单。
- **原子写 + 认领**:写方 tmp→rename、Unity 认领靠 requests→processing 原子 rename,杜绝半截文件与重复处理。
- **反射自动注册(`ICommandHandler`)**:命令零侵入扩展——写一个 `ICommandHandler` 实现(含 `Command`/`Description`)即生效,无需特性、无需改框架。这是 extension-manager 的对接点。
- **Editor-only 程序集**:asmdef `includePlatforms:[Editor]`,v1 仅编辑器,不含运行时/Play mode。
- **序列化用 Newtonsoft Json**:协议 params/result 为任意 JSON(`JObject`/`JToken`),`JsonUtility` 不支持。
- **对象引用共享解析(`ObjectRef`/`SceneObjectResolver`)**:引用 GameObject/Component 的 DTO 与解析逻辑放共享层(`Editor/Scene/`),inspection 与后续 mutation 复用,而非每命令各写一套。解析规则统一:instanceId 优先、否则 path 跨已加载场景查;失败抛 handler 自有错误码(`INVALID_OBJECT_REF`/`OBJECT_NOT_FOUND`/`COMPONENT_NOT_FOUND`)。组件属性读写经一对共享器:`PropertySerializer`(读,顶层、引用渲染为 ObjectRef/资源路径)与 `PropertyDeserializer`(写,JSON 值→`SerializedProperty`,支持嵌套路径)。读由 `cmd-inspection` 首次落地,写由 `cmd-mutation` 补全。
- **命令发现机制 = CLAUDE.md 入口 + `list_commands` 动态清单 + `commandsVersion`(内容 hash)失效信号**:AI 启动调一次 `list_commands` 缓存,响应里 `commandsVersion` 变了才刷新;命令清单不写进静态文档(避免腐烂、覆盖运行时装的扩展)。详见 decision `command-discovery-mechanism`。已由 `cmd-introspection` 实现(`list_commands` 元命令 + 每条响应盖 hash)。

## 5. 已知约束 / 硬边界

- **原子读写**:写方一律 `*.tmp` 写完再 rename;读方只认最终名。
- **单次认领**:`requests→processing` 原子 rename,rename 失败即跳过,轮询重入只处理一次。
- **主线程执行**:handler 在 `EditorApplication.update` 回调内同步执行,可直接用 Unity API。
- **domain reload 中断**:被打断的请求(processing/ 孤儿)在下次启动补 `INTERRUPTED` 响应,at-most-once 不重试。
- **命令名唯一**:重复注册被拒绝并记错误日志(不静默覆盖)。
- **错误语义**:`CommandDispatcher.Dispatch` 永不抛,每个被认领请求必有一份响应;错误码 UNKNOWN_COMMAND / INVALID_PARAMS / HANDLER_EXCEPTION / INTERRUPTED / INTERNAL_ERROR / COMMAND_DISABLED(命令被扩展管理器禁用)。
- **失焦不工作**:Unity 编辑器失焦时不重编译、`EditorApplication.update` 不跑——驱动桥接需 Unity 窗口在前台。
- **新命令必带测试**(规约,见 decision `new-command-requires-test`):每个新增命令(`ICommandHandler`)须在 `Unity/Tests/Editor/` 有对应 EditMode 测试(经 `Dispatch` 覆盖正常+边界+错误);reload 类不可测部分走活体 + 可测单测。测试样例与 `BridgeTestBase` 已就绪。
- **命令发现**:每条响应带 `commandsVersion`(命令集内容 hash,确定性、跨重启稳定);AI 启动调一次 `list_commands` 缓存,version 变即刷新。命令 feature 的每个命令**必须**带描述(`ICommandHandler.Description`)。**禁用命令(extension-manager 启停)从 `list_commands` 和 `commandsVersion` 双双剔除**——禁用即触发 version 变化,AI 重拉清单后看到命令消失(`CommandRegistry.GetAll`/`Version` 均基于可见集)。
- **只读查询命令(inspection)纪律**:`get_hierarchy`/`get_object`/`get_selection`/`list_assets` 不得改变场景/资产(不置 dirty、不 Create/Save/Destroy/Instantiate)。返回有界——`get_object` 只序列化组件顶层属性(引用不深入,渲染为 ObjectRef/资源路径),`list_assets` 无 filter 时限数(默认 1000)并带 `truncated` 标记,避免大工程一次倒出全部。
- **写操作命令(mutation)纪律**:`set_property`/`create_object`/`delete_object` 改完一律标 dirty(`MarkSceneDirty`/`SetDirty`)但**不自动保存**(何时落盘交用户),且记录 Undo(一命令一条撤销,编辑器可 Ctrl-Z)。写命令只动场景对象/组件属性,**不动资源本身**(资源级操作归 cmd-assets)。`invoke_menu` 是逃生舱例外——仅转发 `EditorApplication.ExecuteMenuItem`,副作用(含可能的保存/资源改动)由被调菜单项决定,不受 dirty/save/Undo 纪律约束。
- **命令管理器(extension-manager)纪律**:命令由 `TypeCache` 发现(内置=`AgentBridge.Editor` 程序集,扩展=自带 asmdef 程序集);扩展是放在 `Assets/AgentBridgeExtensions/{id}/` 的本地源码包(用户自放,不做远程)。**启停为隐藏式、按命令名、覆盖内置+扩展**:禁用 = 命令仍编译注册但进**全局禁用名单**(EditorPrefs,工程标识 key)→ 从 `list_commands`/`commandsVersion` 剔除、dispatch 返 `COMMAND_DISABLED`,**不重编译、不删码**;domain reload 后 `CommandToggle.Reapply()` 重建。卸载(仅扩展)= 删目录 + Refresh。启停态**不再用 per-extension meta**(已废)。
- **资源操作命令(assets)纪律**:`import_asset`/`create_asset`/`move_asset`/`delete_asset`/`refresh` 经 `AssetDatabase`,与场景写**语义不同**——资产操作**即时落盘**(非 dirty-only,无法延迟保存),且**不进 Ctrl-Z 撤销栈**(`delete_asset` 用 `MoveAssetToTrash` 进系统回收站作为恢复手段)。写目标路径**强制落在 `Assets/` 下**(拒 `..` 穿越、拒 `Packages/`/工程外),仅 `import_asset` 的 source 可为任意磁盘路径(只读)。资产以路径寻址,不走 ObjectRef。
- **编译自检(compilation)纪律**:编译触发 **domain reload** 会打断在途请求,故 `recompile`(触发)与 `get_compile_result`(读结果)是**异步两步**,不存在同步"编译并返回结果"。`recompile` 仅 `RequestScriptCompilation()` 后立即返回(响应先于 reload 写出);结果只经 `SessionState`(跨 reload 存活)由 `CompileMonitor` 单向写入、命令只读;编译错误是**数据**(`get_compile_result.errors`)非命令错误(命令仍 `status:ok`)。

## 6. 关联规划

- roadmap `file-bridge`:本桥接的分步规划(bridge-core 已完成,inspection/mutation/assets/csharp/agent-protocol-doc 待做)。
- roadmap `extension-manager`:基于 M3 框架的扩展安装/搜索/管理(依赖 file-bridge,待做)。
- requirement `agent-editor-control`:能力愿景层。
- 驱动协议:`Unity/AGENT.md`(面向 AI 的完整协议 + 发现流程)。集成方需把 AGENT.md 中的「CLAUDE.md 片段」粘进自己项目的 `CLAUDE.md`,AI 才知道怎么驱动。
