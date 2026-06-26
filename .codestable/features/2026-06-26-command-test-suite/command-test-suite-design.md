---
doc_type: feature-design
feature: 2026-06-26-command-test-suite
requirement: null
status: approved
summary: 全量 Unity EditMode 测试——AI↔Unity 文件往返(真实 update 轮询,[UnityTest]驱动真 Host)+ file-bridge 框架 + 15 内置命令 + 命令管理器;包内 Tests/Editor/;零生产改动;立"新命令必带测试"规约
tags: [unity, agent, testing, editmode, quality]
---

# command-test-suite design

## 0. 术语约定

| 术语 | 定义 | 防冲突 |
|---|---|---|
| EditMode 测试 | Unity Test Framework 的编辑器态 NUnit 测试(可用 UnityEditor/AssetDatabase/场景 API) | Unity 既有概念 |
| test asmdef | `AgentBridge.Editor.Tests`(Editor-only,引用 AgentBridge.Editor + 测试框架) | 全新 |
| 命令测试基类 | 提供临时场景/资产 setup-teardown 的测试基类(`BridgeTestBase`) | 全新 |
| 测试用命令 | 测试程序集内的一个 `[Command]` handler,用于注册/目录/启停测试不污染真命令 | 全新 |
| 宿主工程 | `G:\GitHub\X\Unity`,本地引用本包、用于打开 Test Runner 跑测试 | — |

grep:`AgentBridge.Editor.Tests`/`BridgeTestBase` 未在代码出现;`Unity/Tests/` 目录不存在。

## 1. 决策与约束

### 需求摘要
- **做什么**:为已交付的全部能力写**全量 EditMode 测试**——① **AI↔Unity 文件往返端到端,经真实 update 轮询**(`[UnityTest]` 协程驱动真 `AgentBridgeHost`:写 request 文件 → `EditorApplication.update`→`Tick` 真实 claim/dispatch/盖 version/release → 读 response 文件断言);② file-bridge 框架(注册/分发/错误码/commandsVersion/禁用名单);③ 15 内置命令(inspection/mutation/assets + ping/list_commands);④ 命令管理器(CommandToggle/CommandCatalog/LocalRegistry)+ 共享层(SceneObjectResolver/PropertySerializer/PropertyDeserializer)。
- **为谁**:维护者(回归保障)+ 未来命令作者(照样例写测试)。
- **成功标准**:在宿主工程 `G:\GitHub\X\Unity` 的 Test Runner(EditMode)里全套可跑;覆盖每条命令的正常+边界+错误路径;新增命令有可照搬的测试样例。
- **明确不做**:
  - 不写 PlayMode 测试(纯编辑器能力;往返用 `[UnityTest]` 但测试程序集仍是 Editor-only)。
  - 不在宿主工程 `G:\GitHub\X\Unity` 写文件(本仓库外);测试落本包 `Unity/Tests/Editor/`。
  - 不引入第三方测试库(只用 Unity Test Framework / NUnit)。
  - **零生产改动**:不改任何 `Unity/Editor/` 生产代码(往返经真实 Host 公开 API Start/Stop + update 驱动,无需可测性 seam);测试暴露的 bug 记 issue 不顺手修。
  - **AI 不跑测试**——产出代码,用户在宿主 Test Runner 跑。

### 复杂度档位
走默认档位。面广(覆盖全部命令)但每条测试是标准 NUnit;无对外 SDK/高并发偏离。

### 关键决策
- **D1 EditMode + Unity Test Framework**:全部 EditMode(需 UnityEditor/AssetDatabase/场景 API);NUnit `[Test]`/`[SetUp]`/`[TearDown]`。
- **D2 测试落包内 `Unity/Tests/Editor/`**:新建 `AgentBridge.Editor.Tests` asmdef(Editor-only,引用 `AgentBridge.Editor` + `UnityEngine.TestRunner`/`UnityEditor.TestRunner` + nunit,`defineConstraints: UNITY_INCLUDE_TESTS`)。随包发布,宿主工程导入即可在 Test Runner 见到。
- **D3 命令经 `CommandDispatcher.Dispatch` 测**(集成视角):构造 `Request{command,params(JObject)}` → 断言 `Response.status/result/error.code/commandsVersion`。覆盖错误码映射 + version。**框架内部件**(CommandRegistry/CommandToggle/CommandCatalog/共享层)直接调 API 单测。
- **D4 临时场景/资产隔离 + 清理**:测试基类 `BridgeTestBase` 在 `[SetUp]` 建临时场景(`EditorSceneManager.NewScene` 或临时 GameObject)+ 临时资产目录 `Assets/__AgentBridgeTests__/`,`[TearDown]` 清干净(删对象/`MoveAssetToTrash`/删临时目录),保证测试无残留、可重入。
- **D5 测试用命令 handler**:测试程序集内放一两个唯一命名(如 `__test_echo`)的 `[Command]` handler,用于注册/目录/启停测试,避免拿真命令(如 ping)做禁用测试污染 EditorPrefs;真禁用态测试用后即 `[TearDown]` 复原。
- **D6 不设 requirement**:测试是质量保障,非新用户能力 → frontmatter `requirement: null`。
- **D7 立"新命令必带测试"规约**:每个新增 `[Command]` 必须在 `Tests/Editor/` 有对应测试(经 Dispatch 覆盖正常+边界+错误)。design 识别,实现跑通后 `cs-decide` 归档为 convention。
- **D8 往返经真实 update 轮询(零生产改动)**:往返测试用 `[UnityTest]` 协程——`[SetUp]` 设 `BridgeSettings.RootDir=临时` + `PollIntervalMs=0`(免节流)+ `AgentBridgeHost.Stop()/Start()` 让真 host 指向临时 root;`[UnityTest]` 体内写 request 文件后 `yield return null` 反复(带超时)让 `EditorApplication.update`→`Tick` 真实跑,直到 response 文件出现;`[TearDown]` 还原 RootDir/Poll + 重启 host 回真实 root。全用公开 API(Start/Stop/IsRunning + BridgeSettings),**不改生产代码**。

### 前置依赖
被测代码全部 done(file-bridge / inspection / mutation / assets / 命令管理器)。宿主工程 `G:\GitHub\X\Unity` 需引用本包 + 装 Unity Test Framework 包(用户侧前提)。

## 2. 名词与编排

### 2.1 名词层

**现状**:
- 被测代码就绪:`Dispatch/`(CommandRegistry/CommandDispatcher/CommandException/ErrorCodes)、`Commands/**`(15 handler)、`Scene/`(SceneObjectResolver/PropertySerializer/PropertyDeserializer/RefErrorCodes/MutationErrorCodes)、`Extensions/`(CommandToggle/CommandCatalog/LocalRegistry/...)。
- **无任何测试**:无 `Tests/` 目录、无 test asmdef。

**变化**(全部新增,落 `Unity/Tests/Editor/`;**生产代码零改动**):

| 名词 | 角色 |
|---|---|
| `AgentBridge.Editor.Tests.asmdef` | Editor-only 测试程序集,引用 AgentBridge.Editor + 测试框架 |
| `BridgeTestBase` | 测试基类:临时场景/资产/临时 root + host 重指向 + setup-teardown 清理 + `WaitForResponse` 协程助手 |
| `__TestCommands.cs` | 测试用 `[Command]` handler(`__test_echo` 等) |
| `RoundTripTests` | **AI↔Unity 文件往返端到端(真实 update)**:`[UnityTest]` 协程——设临时 root + Poll=0 + 真 Host Start;写 request 文件 → yield 让 update→Tick 真跑 → 读 response 断言(含 INTERRUPTED 孤儿/原子写/单次认领) |
| `DispatchTests` | CommandDispatcher/CommandRegistry/错误码/commandsVersion/禁用名单 |
| `InspectionCommandTests` | get_hierarchy/get_object/get_selection/list_assets |
| `MutationCommandTests` | set_property/invoke_menu/create_object/delete_object |
| `AssetCommandTests` | import/create/move/delete_asset/refresh + 路径守卫 |
| `SceneInfraTests` | SceneObjectResolver/PropertySerializer/PropertyDeserializer |
| `CommandManagerTests` | CommandToggle/CommandCatalog/LocalRegistry |

**接口示例**(测试如何调,非生产 API):
```csharp
// 经 Dispatch 测命令(D3)
var resp = CommandDispatcher.Dispatch(new Request { Command="ping", Params=new JObject() });
Assert.AreEqual("ok", resp.Status);
Assert.AreEqual("pong", ((JObject)JObject.FromObject(resp.Result))["message"].Value<string>());

// 错误路径
var r2 = CommandDispatcher.Dispatch(new Request { Command="get_object",
    Params= new JObject{["object"]=new JObject{["path"]="不存在"}} });
Assert.AreEqual("error", r2.Status); Assert.AreEqual("OBJECT_NOT_FOUND", r2.Error.Code);

// 框架件直测
CommandToggle.SetEnabled("__test_echo", false);
Assert.IsTrue(CommandRegistry.IsDisabled("__test_echo"));

// AI↔Unity 文件往返,真实 update(RoundTripTests,[UnityTest] 协程)
// [SetUp]: BridgeSettings.RootDir=tempRoot; PollIntervalMs=0; Host.Stop(); Host.Start();
File.WriteAllText($"{tempRoot}/requests/abc.request.json",
    @"{""v"":1,""id"":""abc"",""command"":""ping"",""params"":{}}");   // AI 侧写请求(原子:tmp→rename)
yield return WaitForResponse("abc", timeoutSec: 5);  // 让 EditorApplication.update→Tick 真实跑直到响应出现
var respJson = File.ReadAllText($"{tempRoot}/responses/abc.response.json"); // AI 侧读响应
// 断言 status=ok / result.message=pong / commandsVersion 非空
// [TearDown]: 还原 RootDir/Poll; Host.Stop(); Host.Start()(回真实 root)
```

### 2.2 编排层

无生产编排改动——纯新增测试。测试结构:
```
每个测试类 : BridgeTestBase
  [SetUp]    建临时场景/资产目录
  [Test]     构造 Request/调 API → Assert(正常/边界/错误)
  [TearDown] 清临时对象/资产/EditorPrefs 复原
```
(无跨模块流程,不画 mermaid。)

**现状 → 变化**:被测代码**零改动**(往返用真 Host 公开 API + 真实 update 驱动);只读引用;新增测试程序集 + 若干测试类。

**流程级约束**:
- **零生产改动**:**不改任何 `Unity/Editor/` 生产代码**(往返测试用 `[UnityTest]` 驱动真 Host 的公开 API + `EditorApplication.update`)。若测试暴露生产 bug → 记新 issue,不在本 feature 顺手修。
- **真实 update + 超时**:往返靠真实 update tick(Poll=0 免节流);`WaitForResponse` 协程 yield 到响应出现或超时失败(避免死等)。
- **host 全局态复原**:往返测试改了 `BridgeSettings.RootDir`/`PollIntervalMs` 并重启了 host → `[TearDown]` 必须还原并重启 host 回真实 root(否则污染开发者 host 指向已删临时目录)。
- **测试隔离 + 可重入**:每个测试自建自清(场景/资产/EditorPrefs);跑完仓库无残留;顺序无关。
- **禁用名单复原**:动 `CommandToggle`/EditorPrefs 的测试 `[TearDown]` 必须恢复原值(避免污染开发者本机 EditorPrefs)。
- **覆盖三类路径**:每条命令至少覆盖 正常 + 边界(空/截断/缺省)+ 错误(对应错误码)。

### 2.3 挂载点清单

| 挂载位置 | 文件 | 动作 |
|---|---|---|
| 测试程序集 | `Tests/Editor/AgentBridge.Editor.Tests.asmdef` | 新增 |
| 测试基类 + fixtures | `Tests/Editor/BridgeTestBase.cs`、`__TestCommands.cs` | 新增 |
| 各域测试类(含 RoundTripTests) | `Tests/Editor/*Tests.cs` | 新增 |

**拔除**:删 `Unity/Tests/` 目录 → 测试全消失,生产代码零影响(测试只读引用 + 用公开 API)。

### 2.4 推进策略
```
1. 脚手架:Tests/Editor/ + AgentBridge.Editor.Tests.asmdef + BridgeTestBase(临时场景/资产/root + host 重指向 + WaitForResponse 协程 + setup-teardown)+ __TestCommands(__test_echo)
   退出:一个 sanity [Test](Dispatch ping→pong)结构成立、可被 Test Runner 发现;无生产改动
2. AI↔Unity 往返(RoundTripTests,真实 update):[UnityTest] 设临时 root+Poll=0+真 Host Start;写 request 文件 → yield 等真实 update→Tick → 读 response;覆盖 ok/error 响应、坏 JSON、单次认领(响应只一次)、孤儿 INTERRUPTED(host 启动 ReclaimOrphans)
   退出:往返响应文件内容正确(status/result/error.code/commandsVersion);真实 update 路径跑通;TearDown 还原 host
3. file-bridge 框架测试(DispatchTests):注册/重复拒绝/Version 确定性+增删稳定/未知命令/缺command/handler异常→HANDLER_EXCEPTION/CommandException→code/禁用→COMMAND_DISABLED/list_commands 含禁用剔除+version 变
   退出:框架各错误码与 version 行为有断言覆盖
4. inspection 测试:临时场景建对象 → get_hierarchy(树/maxDepth/root)/get_object(path+instanceId+componentTypes过滤+引用渲染)/get_selection(选中+空[])/list_assets(filter+无filter截断);错误码 OBJECT/COMPONENT/INVALID_OBJECT_REF
   退出:4 只读命令正常+边界+错误有断言
5. mutation 测试:set_property(嵌套路径+类型不符PROPERTY_TYPE_MISMATCH+路径无PROPERTY_NOT_FOUND+dirty)/create_object(empty/primitive/prefab/parent+返回ObjectRef)/delete_object(删+重复OBJECT_NOT_FOUND)/invoke_menu(已知true+未知MENU_NOT_FOUND)
   退出:4 写命令正常+边界+错误有断言;场景 dirty 验证
6. assets 测试:create_asset(folder/text/SO+UNKNOWN_ASSET_TYPE)/import_asset(临时磁盘源→Assets+ASSET_SOURCE_NOT_FOUND)/move/delete(回收站+ASSET_NOT_FOUND)/refresh/路径守卫(越界INVALID_ASSET_PATH)
   退出:5 资源命令 + 路径守卫有断言;临时资产清理干净
7. 命令管理器 + 共享层测试:CommandToggle(EditorPrefs 往返+SetEnabled 影响 CommandRegistry+Reapply)/CommandCatalog(列举+来源+Enabled+归属)/LocalRegistry(扫临时扩展)/SceneObjectResolver/PropertySerializer/PropertyDeserializer
   退出:管理器 + 共享层有断言;EditorPrefs 复原
```

### 2.5 结构健康度与微重构

##### 评估
- compound 检索(目录组织):无测试组织 convention(暂无)。`commands-category-subdirectory` 只约束 `Commands/` 生产代码,不约束测试。
- 文件级:全新增,**不改生产文件**(往返用真 Host 公开 API + 真实 update)。
- 目录级:新建 `Unity/Tests/Editor/`(~9 文件:asmdef + 基类 + fixtures + 7 测试类)。新目录不挤。

##### 结论:不做(微重构)
全新增、独立目录,生产代码零触碰。

##### 建议沉淀的 convention(implement 跑通后 cs-decide)
**"每个新增 `[Command]` 必须在 `Tests/Editor/` 有对应 EditMode 测试(经 Dispatch 覆盖正常+边界+错误)"**——本 feature 建立测试样例与基类后,该规约即可执行。implement 跑通后建议 `cs-decide` 归档为 convention(约束所有未来命令 feature)。

##### 超出范围的观察
- 真实 update 的 **claim/dispatch/响应** 路径已测(`[UnityTest]` 驱动);但**节流时序**(Poll=0 旁路)与**失焦节流行为**(batch 测试无法复现失焦)不覆盖——非本期目标,留后续。

## 3. 验收契约

### 关键场景清单(每条 = 一组测试)
0. **AI↔Unity 往返(端到端,真实 update)**:临时 root + Poll=0 + 真 Host;写 `requests/{id}.request.json`(AI 侧)→ `yield` 等 `EditorApplication.update`→`Tick` 真实处理 → 读 `responses/{id}.response.json`(AI 侧):ok 请求得 status=ok/result 正确/commandsVersion 非空;坏 JSON 得 INTERNAL_ERROR;未知命令得 UNKNOWN_COMMAND;**单次认领**(同一请求只产一份响应、processing/ 清空);**孤儿 INTERRUPTED**(预置 processing/ 残留 → Host.Start 的 ReclaimOrphans 补 INTERRUPTED);响应文件经原子写(只见最终名)。
1. **框架**:`Dispatch` 对 未知命令→UNKNOWN_COMMAND、缺 command→INVALID_PARAMS、handler 抛普通异常→HANDLER_EXCEPTION、抛 CommandException→其 code、禁用命令→COMMAND_DISABLED;`CommandRegistry` 重复命令名拒绝;`Version` 对同命令集确定、增删命令变化;`list_commands` 结果剔除禁用 + version 随禁用变。
2. **inspection**:get_hierarchy 返回临时场景树(maxDepth/root 生效);get_object 按 path/instanceId 返回组件+顶层属性,componentTypes 过滤,引用渲染为 ObjectRef/资源路径;get_selection 选中→列表、空→`[]`;list_assets 带 filter 命中、无 filter 截断+truncated;错误 OBJECT_NOT_FOUND/COMPONENT_NOT_FOUND/INVALID_OBJECT_REF。
3. **mutation**:set_property 改嵌套属性生效 + 场景 dirty + 类型不符 PROPERTY_TYPE_MISMATCH + 路径无 PROPERTY_NOT_FOUND;create_object empty/primitive/prefab + parent + 返回 ObjectRef;delete_object 删除 + 重复 OBJECT_NOT_FOUND;invoke_menu 已知项 executed + 未知 MENU_NOT_FOUND。
4. **assets**:create_asset folder/text/SO(UNKNOWN_ASSET_TYPE);import_asset 外部文件→Assets(ASSET_SOURCE_NOT_FOUND);move_asset;delete_asset 进回收站(ASSET_NOT_FOUND);refresh;路径越界 INVALID_ASSET_PATH。
5. **命令管理器**:CommandToggle SetEnabled 写 EditorPrefs + 令 CommandRegistry.IsDisabled 真 + Reapply 重建;CommandCatalog.All 含测试命令、IsBuiltin/Assembly/Enabled 正确;LocalRegistry 扫临时扩展目录归属。
6. **共享层**:SceneObjectResolver(instanceId 优先/path 跨场景/FindType);PropertySerializer 顶层序列化各类型;PropertyDeserializer 写值 + 类型不符抛错。
7. **隔离**:整套跑两遍结果一致;跑完仓库无临时资产/场景残留、EditorPrefs 未被污染。

### 明确不做的反向核对项
- **零生产改动**:本 feature 的 diff 全在 `Unity/Tests/`(grep:`Unity/Editor/` 无改动)。
- Editor-only 测试程序集(asmdef `includePlatforms:[Editor]`,无 PlayMode 程序集);`[UnityTest]` 仅用于往返驱动真实 update,仍是 EditMode。
- 不测节流时序/失焦(Poll=0 旁路;batch 无法复现失焦)。
- 测试暴露的生产 bug 记 issue 不在本 feature 改。

## 4. 与项目级架构文档的关系

acceptance 提炼回 `architecture/ARCHITECTURE.md`:
- 新增"测试"一节/条目:`Unity/Tests/Editor/`(EditMode,`AgentBridge.Editor.Tests` asmdef),覆盖 AI↔Unity 文件往返(`[UnityTest]` 经真实 update 驱动真 Host)+ 框架 + 命令(经 `Dispatch`)+ 管理器;**零生产改动**。
- **convention**:跑通后经 cs-decide 归档"新命令必带测试",architecture/attention 可引用。
- requirement:无(测试为质量保障,非用户能力)。
- attention.md 候选:跑测试需在宿主工程 `G:\GitHub\X\Unity`(含 Assets/ + Test Framework 包)打开 Test Runner——下个 feature 也会用到。

关联:被测 = file-bridge(roadmap)全模块 + extension-manager;无新 decision(convention 留 acceptance 后 cs-decide)。
