---
doc_type: issue-report
issue: 2026-06-26-test-scene-isolation
status: confirmed
severity: P3
summary: EditMode 测试在当前活动场景建临时对象,中途失败时残留污染开发者场景
tags: [testing, editmode, scene-isolation, command-test-suite]
---

# 测试在活动场景建临时对象、失败时残留 Issue Report

## 1. 问题现象

`AgentBridge.Editor.Tests` 的 EditMode 测试通过 `BridgeTestBase.NewGo(name)` 用 `new GameObject(name)` 在**当前打开的活动场景**里建临时对象。正常路径靠 `[TearDown]` 销毁;但当某测试在销毁前**中途抛错/断言失败**时,临时对象会**残留在开发者的活动场景**中。

实测残留:一次 `command-test-suite` 运行后,`get_hierarchy` 在活动场景 `Bake Demo.unity` 根列表里出现一个名为 `Doomed` 的对象(`MutationCommandTests.DeleteObject_ThenRepeatNotFound` 的临时对象名),非用户自建。

## 2. 复现步骤

1. 在宿主工程的全局禁用名单里禁用 `delete_object`(命令管理器窗口,或 EditorPrefs)。
2. Test Runner 跑 `MutationCommandTests.DeleteObject_ThenRepeatNotFound`:
   - 测试 `NewGo("Doomed")` 在活动场景建对象;
   - `Dispatch("delete_object", …)` 因禁用返回 `COMMAND_DISABLED`;
   - `Assert.AreEqual("ok", r.Status)` 失败 → 测试中途中断。
3. 观察:活动场景里残留 `Doomed`(后经桥接 `delete_object` 手动删除)。

复现频率:稳定(凡测试在 TearDown 销毁前抛错即残留;`delete_object` 禁用只是触发该失败的一个具体诱因)。

> 注:`delete_object` 禁用导致测试失败这一诱因,已由后续在 `BridgeTestBase.SetUp` 隔离禁用名单修复;但"活动场景建对象、失败即残留"的**隔离局限本身仍在**——任何测试中途失败都可能残留。

## 3. 期望 vs 实际

**期望行为**:测试在一个**抛弃式/隔离场景**里建临时对象,无论测试通过还是中途失败,跑完都**不污染开发者的活动场景**;且不与活动场景里的同名对象互相干扰。

**实际行为**:测试在**当前活动场景**直接建对象,依赖 `[TearDown]` 清理;一旦测试在 TearDown 前抛错,对象残留进活动场景。

## 4. 环境信息

- 涉及模块 / 功能:command-test-suite(`Unity/Tests/Editor/`)测试基建。
- 相关文件 / 函数:`Unity/Tests/Editor/BridgeTestBase.cs`(`NewGo` / `TearDown`);受影响测试 `InspectionCommandTests` / `MutationCommandTests` / `SceneInfraTests`(用 NewGo 建活动场景对象)。
- 运行环境:Unity 6000.3.12f1 编辑器 EditMode 测试,宿主工程 `G:\GitHub\X\Unity`。
- 其他上下文:来源 command-test-suite 活体实跑发现;残留实例 `Doomed` 已删除。

## 5. 严重程度

**P3** — 仅影响测试基建与开发者场景整洁,不影响生产/桥接运行;有手动清理绕过;但属测试可靠性债,值得后续修。

## 备注

- 用户倾向的修复方向(留给 analyze/fix 确认):测试用 `EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single/Additive)` 建抛弃式场景,`[TearDown]` 卸载并还原原活动场景;NewGo 建在该隔离场景。
- 本轮只记录不修(测试暴露的问题记 issue,不在 command-test-suite feature 里顺手改)。
