---
doc_type: feature-acceptance
feature: 2026-06-25-cmd-mutation
status: accepted
summary: cmd-mutation 验收闭环——4 写命令 + PropertyDeserializer,契约/写语义/范围守护全核对通过;真机测试经用户决定跳过,运行时以代码评审为证据
tags: [unity, agent, mutation, write, acceptance]
---

# cmd-mutation 验收报告

> 阶段:阶段 3(验收闭环)
> 验收日期:2026-06-25
> 关联方案 doc:`.codestable/features/2026-06-25-cmd-mutation/cmd-mutation-design.md`

验收方式说明:本次核对以**代码评审 + grep 契约核对 + design 对照**为主。**用户明确决定跳过真机测试**(对话:"不用测试 继续"),故第 3 节运行时场景(改值生效、dirty、Ctrl-Z 撤销、prefab 实例化、菜单执行)以**代码层证据**为准,标注"真机未测";编译验证亦未在本会话进行(Unity Editor-only 程序集,无 headless 编译)。该取舍由用户承担,记录在案。

## 1. 接口契约核对

对照方案第 2.1 节名词层逐一核查:

**接口示例逐项核对**:
- [x] `set_property`(`Commands/Mutation/SetPropertyHandler.cs:Execute`):输入 `{component, propertyPath, value}` → 输出 `{object, component, propertyPath, applied:true}`。代码:`ResolveComponent` → `SerializedObject.FindProperty(propertyPath)` → `PropertyDeserializer.Apply` → `ApplyModifiedProperties` → `SetDirty`+`MarkSceneDirty` → 返回结构**一致**。
- [x] `create_object`(`CreateObjectHandler.cs`):输入 `{kind, primitive?, prefabPath?, name?, parent?}` → 输出 `{object{name,path,instanceId,active}}`(新对象 ObjectRef)。三 kind 分支齐全 → **一致**。
- [x] `delete_object`(`DeleteObjectHandler.cs`):输入 `{object}` → 输出 `{deleted:true}` → **一致**。
- [x] `invoke_menu`(`InvokeMenuHandler.cs`):输入 `{path}` → 输出 `{executed:true}` → **一致**。

**名词层"现状 → 变化"逐项核对**(全部新增):
- [x] 4 个 Handler:均带 `[Command]` + `ICommandHandler` + `ICommandSchema` → **一致**。
- [x] `PropertyDeserializer`(`Editor/Scene/`):`PropertySerializer` 对偶,覆盖 Integer/Boolean/Float/String/Enum/Vector2-4/Quaternion/Color/Rect/Bounds/ObjectReference;不支持类型抛 `PROPERTY_TYPE_MISMATCH` → **一致**。
- [x] `MutationErrorCodes`(`Editor/Scene/`):`PROPERTY_NOT_FOUND`/`PROPERTY_TYPE_MISMATCH`/`MENU_NOT_FOUND`/`CREATE_FAILED` → **一致**。
- [x] 复用 inspection 共享层(`SceneObjectResolver`/`ObjectRef`/`ComponentRef`/`PropertySerializer`/`RefErrorCodes`):grep 确认只读引用、未改动 → **一致**。

**流程图核对**(第 2.2 节 set_property mermaid):
- [x] `Dispatch → SetPropertyHandler.Execute → ResolveComponent(找不到抛 OBJECT/COMPONENT_NOT_FOUND)→ FindProperty(路径不存在 PROPERTY_NOT_FOUND)→ PropertyDeserializer.Apply(类型不符 PROPERTY_TYPE_MISMATCH)→ ApplyModifiedProperties → MarkSceneDirty → result`,逐节点 grep 均有落点。

**偏差**:一处**机制级偏差,已透明上报并保留**(非语义偏离):design D1/mermaid 写 `Undo.RecordObject`,实现改用 `SerializedObject.ApplyModifiedProperties()` 的**内建 Undo**。原因:`ApplyModifiedProperties` 本身注册 Undo,叠加 `Undo.RecordObject` 会产生双重撤销记录;改用内建机制留一条干净撤销。D1 意图("可撤销")达成,可观察结果一致。用户在 impl 汇报时已知悉未要求改回。**create_object 用 `Undo.RegisterCreatedObjectUndo`、delete_object 用 `Undo.DestroyObjectImmediate`,与 design 字面一致。**

## 2. 行为与决策核对

对照方案第 1 节 + 第 2.2 节:

**需求摘要逐项验证**:
- [x] 4 写命令均落地为独立 handler,经现有 dispatch,无宿主改动(grep 确认未改 Dispatch/Channel/Host/Protocol)。

**明确不做逐项核对**(第 3 节反向核对项):
- [x] 不做资源级操作:set/create/delete 路径 grep 无 `CreateAsset|ImportAsset|MoveAsset|DeleteAsset|AssetDatabase.Save`(exit 1)。`create_object` 的 prefab 走 `LoadAssetAtPath`+`InstantiatePrefab`(读资源建场景对象,不写资源);`delete_object` 只删场景对象。
- [x] 不自动保存:set/create/delete 路径 grep 无 `SaveScene|SaveAssets|SaveOpenScenes`(exit 1)。
- [x] 不做 execute_csharp:无 runtime 编译入口。
- [x] 不做 Play mode:无 runtime 入口。

**关键决策落地**:
- [x] D1 记录 Undo:set 内建 Undo / create `RegisterCreatedObjectUndo` / delete `DestroyObjectImmediate`(均 grep 确认)。
- [x] D2 仅标 dirty 不自动 save:3 写命令 `MarkSceneDirty`/`SetDirty`,无 save 调用;`invoke_menu` 例外(无 dirty/save,逃生舱)。
- [x] D3 嵌套路径:`SerializedObject.FindProperty(propertyPath)` 直接支持 Unity 嵌套路径串。
- [x] D4 create 三 kind:empty(`new GameObject`)/primitive(`GameObject.CreatePrimitive` + `Enum.TryParse<PrimitiveType>`)/prefab(`PrefabUtility.InstantiatePrefab`)。
- [x] D5 复用基础设施:解析全走 `SceneObjectResolver`,引用值解析对偶 `PropertySerializer`。
- [x] D6 自有错误码:`MutationErrorCodes` 4 个 + 复用 `RefErrorCodes` 3 个。
- [x] D7 4 命令实现 ICommandSchema:4 个 `GetParamsSchema()` 均非 null。

**流程级约束核对**:
- [x] 写语义(标 dirty 不 save)/ 可撤销(Undo)/ 主线程 / 对象引用复用 / 自描述 / invoke_menu 逃生舱边界 —— 逐条在代码体现。
- [x] 幂等性:`set_property` 幂等(同值重复写一致);`create_object` 非幂等(每次 new);`delete_object` 重复删 → `ResolveObject` 抛 `OBJECT_NOT_FOUND`。

**挂载点反向核对(可卸载性)**——对照第 2.3 节:
- [x] 挂载点 M1-M4(4 命令注册):反射自动发现机制,挂载点即 4 个 handler 的 `[Command]` 特性;grep 确认 `Commands/Mutation/` 恰 4 条 `[Command(`。
- [x] **反向核查**:`grep set_property|invoke_menu|create_object|delete_object` 命中全部落在 4 个 handler 文件内,无清单外引用。新类型 `PropertyDeserializer`/`MutationErrorCodes` 仅被 mutation handler 引用。
- [x] **拔除沙盘推演**:删除 `Commands/Mutation/` + `Editor/Scene/PropertyDeserializer.cs` + `MutationErrorCodes.cs` 后,反射注册表不再发现这 4 命令;inspection 共享层(只读引用)与框架不受影响 → 可干净拔除,无残留。

## 3. 验收场景核对

对照方案第 3 节关键场景清单。**运行时证据 = 代码评审(真机经用户决定跳过)**:

- [~] **S1 set_property 基本**:嵌套路径写值 + dirty + Ctrl-Z。代码路径完整(`FindProperty`+`Apply`+内建 Undo+`MarkSceneDirty`)。证据:代码评审;**真机未测**。
- [~] **S2 set_property 引用类型**:`PropertyDeserializer.ResolveRef` 处理 `{assetPath}`/`{instanceId}`/`{path}`/null。证据:代码评审;**真机未测**。
- [~] **S3 create_object empty**:`new GameObject`+SetParent+RegisterCreatedObjectUndo+返回 ObjectRef。证据:代码评审;**真机未测**。
- [~] **S4 create_object primitive**:`CreatePrimitive` + `Enum.TryParse`。证据:代码评审;**真机未测**。
- [~] **S5 create_object prefab**:有效路径 `InstantiatePrefab`;无效路径 → `CREATE_FAILED`(LoadAsset null / 实例化 null 两处兜底)。证据:代码评审;**真机未测**。
- [~] **S6 delete_object**:`DestroyObjectImmediate`+dirty;重复删 → `ResolveObject` 抛 `OBJECT_NOT_FOUND`。证据:代码评审;**真机未测**。
- [~] **S7 invoke_menu**:`ExecuteMenuItem` true→`executed:true`,false→`MENU_NOT_FOUND`。证据:代码评审;**真机未测**。
- [x] **S8 错误路径**:`COMPONENT_NOT_FOUND`(ResolveComponent)/`PROPERTY_NOT_FOUND`(FindProperty null)/`PROPERTY_TYPE_MISMATCH`(Apply 类型校验)/`INVALID_OBJECT_REF`(ResolveObject)各有抛点。证据:代码评审(错误码落点 grep 确认)。
- [x] **S9 自描述**:4 `[Command]` 描述非空 + 4 `GetParamsSchema()` 非 null → `CommandRegistry` 反射必收录。证据:代码评审。
- [x] **S10 写副作用方向**:set/create/delete 均置 dirty(grep 确认),无 save(grep exit 1)。证据:grep。

说明:S1–S7 标 `[~]`(代码完成、真机未测,用户已同意跳过);S8–S10 为代码层可静态确证项,标 `[x]`。无前端改动。

## 4. 术语一致性

对照方案第 0 节 + 第 2.1 节命名 grep:

- 4 命令名:`[Command]` = `Command` 属性 = 注释一致 ✓
- 新类型 `PropertyDeserializer`/`MutationErrorCodes`/4 Handler 命名与 design 一致 ✓
- 复用类型名(`SceneObjectResolver`/`ObjectRef`/`ComponentRef`/`PropertySerializer`/`RefErrorCodes`)无改写、无同名重定义 ✓
- 防冲突:命令名/类型名 grep 无既有冲突 ✓

无不一致。

## 5. 架构归并

对照方案第 4 节,已**实际写入** `.codestable/architecture/ARCHITECTURE.md`:

- [x] **名词归并**:§3 M5 行更新,列出 4 个写命令;§4 决定条补 `PropertyDeserializer`(写,与 `PropertySerializer` 配对)✓
- [x] **流程级约束归并**:§5 新增"写操作命令(mutation)纪律"约束条(标 dirty 不自动 save + Undo + 不动资源 + invoke_menu 逃生舱例外)✓
- [x] 头部"最近更新"改为 2026-06-25(cmd-mutation 验收归并)✓

判据自检:未读 design 的人打开 ARCHITECTURE.md 现可知"系统有 4 个写命令、写会标 dirty 但不自动保存且可撤销、invoke_menu 是不受约束的逃生舱、属性读写由 Serializer/Deserializer 一对器承担"。归并到位。

## 6. requirement 回写

方案 frontmatter `requirement: agent-editor-control`(status: current)。

- [x] 指向 current req,本次**未改**用户故事/边界/pitch(pitch「指挥编辑器干活」本就含写操作)→ 不动愿景,在 req 文末**追加一条交付进度变更日志**(2026-06-25,记 cmd-mutation 落地、"读+改"闭环成形、真机测试跳过)。已实际写入 `requirements/agent-editor-control.md`。

## 7. roadmap 回写

方案 frontmatter `roadmap: file-bridge` / `roadmap_item: cmd-mutation`,两字段均有值,必须回写:

- [x] `file-bridge-items.yaml`:`cmd-mutation` 由 `in-progress` → `done`(`feature` 核对一致)。
- [x] `validate-yaml.py --file` 校验通过(1 passed)。
- [x] 主文档 `file-bridge-roadmap.md` §5 第 5 条同步:状态 → `done(2026-06-25,…验收;真机测试经用户决定跳过)`、对应 feature 填实、标题加 ✅ done、备注补 PropertyDeserializer/写语义。

## 8. attention.md 候选盘点

- [x] 无新候选:本 feature 未引入新的编译命令/代理/起服务步骤/路径陷阱。已有"失焦需先开 Background"注意事项对真机调本 feature 命令同样适用,已记录无需重复。

## 9. 遗留

- **真机验证待补**:S1–S7 运行时行为(改值/dirty/撤销/prefab/菜单)经用户决定跳过测试,以代码评审为证据。建议日后在 Unity 中实跑一轮补证(失焦先开 `Tools/AgentBridge/Enable Background`)。
- 已知限制:`set_property` 对象引用写值用 `LoadMainAssetAtPath`,不区分字段期望的具体子资产类型(Unity 会按字段类型自行校验/拒绝);极端情况下设了类型不符的引用可能被 Unity 静默忽略。
- 已知限制:`set_property` 仍只覆盖 `PropertySerializer` 对偶的类型集;数组元素、自定义结构等复杂类型不支持写(抛 `PROPERTY_TYPE_MISMATCH`),与读侧"只到顶层"对称。
- 机制说明(非遗留):set_property 用 `ApplyModifiedProperties` 内建 Undo 替代显式 `Undo.RecordObject`(见第 1 节)。
- 待沉淀:`Commands/{Category}/` 子目录布局已第二次出现(Inspection、Mutation),design 2.5 建议跑通后走 `cs-decide` 归档为 convention(留 accept 收尾定)。
- 实现阶段无"顺手发现"。
