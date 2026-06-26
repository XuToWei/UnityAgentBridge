---
doc_type: feature-acceptance
feature: 2026-06-26-command-test-suite
status: accepted
summary: command-test-suite 验收闭环——全量 EditMode 测试(AI↔Unity 真实 update 往返 + 框架 + 15 命令 + 管理器);零生产改动;架构归并测试体系 + 新命令必带测试规约;AI 无法跑测试,绿由用户在宿主 Test Runner 验
tags: [unity, agent, testing, editmode, acceptance]
---

# command-test-suite 验收报告

> 阶段:阶段 3(验收闭环)
> 验收日期:2026-06-26
> 关联方案 doc:`.codestable/features/2026-06-26-command-test-suite/command-test-suite-design.md`

验收方式:本 feature 的交付物**就是测试代码**。AI **无法 headless 编译/运行 Unity 测试** → 本验收以**代码评审 + API 一致性 grep + design 对照**确认测试已写齐、调用真实生产 API、覆盖 design 场景;**测试是否全绿由用户在宿主工程 `G:\GitHub\X\Unity` 的 Test Runner(EditMode)实跑确认**(见第 9 节)。

## 1. 接口契约核对

对照 design 第 2.1 节:

**测试侧名词逐项**:
- [x] `AgentBridge.Editor.Tests.asmdef`:Editor-only,引用 `AgentBridge.Editor`+`UnityEngine/UnityEditor.TestRunner`+`nunit.framework.dll`,`UNITY_INCLUDE_TESTS` 约束 → **一致**(首跑可能需按宿主 Test Framework 版本微调引用,见第 9 节)。
- [x] `BridgeTestBase`:NewGo/EnsureTempAssetDir/Dispatch/NewTempRoot/WriteRequestFile/WaitForResponse + TearDown 清理 → **一致**。
- [x] `__TestCommands`(__test_echo/__test_throw/__test_cmdex)+ `TestSettings`(SO)→ **一致**。
- [x] 7 测试类(RoundTrip/Dispatch/Inspection/Mutation/Asset/CommandManager/SceneInfra)→ **一致**。

**调用真实 API 核对**(grep 抽样,全部命中真实生产符号):`CommandDispatcher.Dispatch`、`CommandRegistry.IsDisabled/SetDisabledCommands/Version/Commands/TryGet`、`CommandToggle.SetEnabled/Reapply/Disabled`、`CommandCatalog.All/BuiltinAssembly`、`AgentBridgeHost.Start/Stop`、`BridgeSettings.RootDir/PollIntervalMs`、`FileChannel.RequestSuffix/ResponseSuffix`、`ErrorCodes.*`/`RefErrorCodes.*`/`MutationErrorCodes.*`/`AssetErrorCodes.*`、`SceneObjectResolver.FindByPath/ResolveObject/FindType`、`PropertySerializer.SerializeTopLevel`、`PropertyDeserializer.Apply`、`ObjectRef.Path/InstanceId`、`CommandException.Code`、`Response.Status/Result/Error/CommandsVersion`、`Request.Id/Command/Params`。→ 无虚构 API。

**流程图核对**(往返三子图):写 request 文件→`update`→`Tick`→读 response;`RoundTripTests` 经真实 update 驱动落点齐(SetUp Start/Stop + WaitForResponse yield)。

**偏差**:无。

## 2. 行为与决策核对

**关键决策落地**:
- [x] D1 EditMode/NUnit:全 `[Test]`/`[UnityTest]`/`[SetUp]`/`[TearDown]`。
- [x] D2 测试落 `Unity/Tests/Editor/` + test asmdef。
- [x] D3 命令经 `Dispatch` 测、框架/管理器/共享层直测:Dispatch* / CommandManager* / SceneInfra* 落地。
- [x] D4 `BridgeTestBase` 临时场景/资产/root 自建自清 + WaitForResponse。
- [x] D5 测试用命令 `__test_*`(禁用/异常/错误码测试不污染真命令)。
- [x] D6 requirement: null。
- [x] D7 新命令必带测试规约:已识别,本报告退出后建议 cs-decide。
- [x] D8 往返经**真实 update**(`[UnityTest]` 驱动真 Host,SetUp 设临时 root+Poll=0+Start,TearDown 还原)——**零生产改动**(无 DrainOnce seam)。

**明确不做逐项核对**:
- [x] **零生产改动**:本 feature diff 全在 `Unity/Tests/`;`git status Unity/Editor/` 的 M/D 均为**之前 feature 历史未提交**(接口合并/ext-enable-disable/cmd-*),非本次。
- [x] Editor-only 测试程序集(asmdef `includePlatforms:[Editor]`);`[UnityTest]` 仅驱往返、仍 EditMode。
- [x] 不测节流/失焦(Poll=0 旁路)。
- [x] 测试暴露的生产 bug 记 issue 不在本 feature 改(见第 9 节遗留)。

**流程级约束**:
- [x] host 全局态复原:`RoundTripTests.TearDown` 还原 RootDir/Poll + 重启 host 回真实 root + 删临时 root。
- [x] EditorPrefs 复原:动 `CommandToggle` 的测试 `finally`/`TearDown` 复原 `__test_echo` enabled。
- [x] 隔离自清:`BridgeTestBase.TearDown` 销毁临时 GO + 删临时资产目录。

**挂载点反向核对(可卸载性)**——对照第 2.3 节:
- [x] 挂载点(asmdef / 基类+fixtures / 各域 *Tests)均有文件落点(`ls Unity/Tests/Editor/` 11 文件)。
- [x] **反向核查**:测试只读引用生产 + 公开 API;无生产代码引用测试程序集(`autoReferenced:false` + 单向)。
- [x] **拔除沙盘推演**:删 `Unity/Tests/` → 测试全消失,生产零影响(无 seam 残留)。

## 3. 验收场景核对

对照 design 第 3 节。**证据 = 代码评审(测试已写齐且 API 正确);全绿待用户实跑**:

- [~] **S0 往返(真实 update)**:`RoundTripTests` 覆盖 ok/坏JSON(INTERNAL_ERROR)/未知(UNKNOWN_COMMAND)/单次认领(requests+processing 清空)/孤儿 INTERRUPTED。证据:代码评审;**真机待跑**。
- [~] **S1 框架**:`DispatchTests` 覆盖 5 错误码 + Version 确定+禁用变化 + list_commands 剔除禁用。证据:代码评审;**真机待跑**。
- [~] **S2 inspection**:`InspectionCommandTests` get_hierarchy/get_object/get_selection(空[])/list_assets + 3 错误码。**真机待跑**。
- [~] **S3 mutation**:`MutationCommandTests` set_property(嵌套+dirty+2错误码)/create_object(empty/primitive/parent/prefab+未知)/delete(+重复)/invoke_menu(+未知)。**真机待跑**。
- [~] **S4 assets**:`AssetCommandTests` create(folder/text/SO+未知)/import(+source缺)/move/delete(+不存在)/refresh/路径守卫。**真机待跑**。
- [~] **S5 命令管理器**:`CommandManagerTests` CommandToggle/Reapply/CommandCatalog/LocalRegistry。**真机待跑**。
- [~] **S6 共享层**:`SceneInfraTests` Resolver/PropertySerializer/PropertyDeserializer。**真机待跑**。
- [~] **S7 隔离**:每测自建自清 + host/EditorPrefs 复原。代码评审确认清理路径齐;两遍一致性**真机待跑**。

全 `[~]`:测试代码完成、API 正确、覆盖齐;**绿由用户在宿主 Test Runner 实跑确认**(本 feature 性质即此——AI 产出测试,用户跑)。

## 4. 术语一致性

- 测试类型名(BridgeTestBase/RoundTripTests/__test_echo/TestSettings 等)与 design 第 0 节一致 ✓
- 调用的生产符号名 grep 全部对上真实 API(第 1 节)✓
- 防冲突:`__` 前缀测试命令/类不与生产命令名冲突 ✓

## 5. 架构归并

已**实际写入** `.codestable/architecture/ARCHITECTURE.md`:
- [x] §3 新增"测试(`Unity/Tests/Editor/`,EditMode)"小节:测试程序集 + AI↔Unity 真实 update 往返(RoundTripTests/[UnityTest])+ 命令经 Dispatch 测 + BridgeTestBase 自清 + 零生产改动 + 宿主 Test Runner 跑 ✓
- [x] §5 新增约束"**新命令必带测试**(规约,建议 cs-decide 归档)" ✓
- [x] 头部最近更新 → 2026-06-26(command-test-suite 验收归并)✓

判据自检:未读 design 的人打开 ARCHITECTURE 现可知"有 EditMode 测试体系、往返经真实 update 测、新命令必须带测试、在宿主工程跑"。归并到位。

## 6. requirement 回写

frontmatter `requirement: null`。测试是质量保障、非新用户能力 → **无 requirement 回写**(符合"纯技术/质量留空")。

## 7. roadmap 回写

frontmatter 无 `roadmap`/`roadmap_item` → **非 roadmap 起头,跳过**。(本 feature 是横切质量 feature,不属任何 roadmap。)

## 8. attention.md 候选盘点

- [x] **有候选**:**跑桥接测试需在宿主 Unity 工程 `G:\GitHub\X\Unity`(含 `Assets/` + 装 Unity Test Framework 包)打开 Test Runner(EditMode)**;本仓库 `Unity/` 是纯 UPM 包不能直接跑。下个加命令的 feature 也要照此跑测试。登记不擅写。

## 9. 遗留

- **测试全绿待用户实跑**:AI 无法 headless 跑 Unity;S0–S7 以代码评审为证据,用户在宿主 Test Runner 确认。**首跑可能要微调**:① test asmdef 的 `UnityEngine/UnityEditor.TestRunner` 引用 + `nunit.framework.dll`(若宿主用 GUID 引用或 Test Framework 版本不同,Inspector 里重选);② 个别真机相关断言(`scene.isDirty`、菜单 `Assets/Refresh`、`FindByPath` 活动场景、`MoveAssetToTrash`)。
- **重复命令名拒绝未单测**:`CommandRegistry` 的 duplicate 拒绝是日志行为,直接单测需噪声全局 fixture(两个同名 [Command]),本期未覆盖——记遗留。
- 实现阶段无"顺手发现"(未碰生产代码)。
