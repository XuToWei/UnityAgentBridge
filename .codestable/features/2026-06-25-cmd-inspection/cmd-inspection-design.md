---
doc_type: feature-design
feature: 2026-06-25-cmd-inspection
requirement: agent-editor-control
roadmap: file-bridge
roadmap_item: cmd-inspection
status: approved
summary: 只读查询命令 get_hierarchy/get_object/get_selection/list_assets + 共享 ObjectRef 解析
tags: [unity, agent, inspection, read-only, scene, assets]
---

# cmd-inspection design

## 0. 术语约定

| 术语 | 定义 | 防冲突 |
|---|---|---|
| `ObjectRef` | 引用 GameObject 的方式 `{path, instanceId}`(roadmap 4.5) | 全新类型,grep 无 |
| `ComponentRef` | 引用组件 `{object, type, index}`(roadmap 4.5) | 全新 |
| `SceneObjectResolver` | 把 ObjectRef/ComponentRef 解析成 GameObject/Component 的共享工具 | 全新 |
| Hierarchy 节点 | 场景树一个节点 `{name, path, instanceId, active, children}` | 全新 |
| 顶层属性 | 组件经 `SerializedObject` 迭代出的顶层可序列化字段(不递归深入) | — |

grep 防冲突:`get_hierarchy`/`get_object`/`get_selection`/`list_assets`/`ObjectRef` 均未在代码出现。

## 1. 决策与约束

### 需求摘要
- **做什么**:给桥接加 4 个**只读**查询命令,让 AI 读 Unity 编辑器状态:
  - `get_hierarchy` — 已加载场景的层级树
  - `get_object` — 某对象的组件 + 顶层属性
  - `get_selection` — 当前编辑器选中对象
  - `list_assets` — 按条件查工程资产
- **为谁**:驱动桥接的 AI(读现场再决定下一步操作)。
- **成功标准**:4 命令真机可调,返回结构正确;`get_object` 能定位对象并列出组件+属性;`list_assets` 按 filter 返回;均出现在 `list_commands` 且带描述+参数 schema;调用**不改变**场景/资产。
- **明确不做**:
  - 不做任何写操作(改属性/建删对象/改资产归 cmd-mutation/cmd-assets)
  - 不递归深展开嵌套属性(只到顶层;深挖由 AI 按 ObjectRef 再查)
  - 不做运行时/Play mode(纯编辑器)

### 复杂度档位
走默认档位,无偏离。

### 关键决策
- **D1 get_object 返回组件+顶层属性,可过滤**(你选):组件列表,每组件顶层 `SerializedProperty` 序列化;基本类型给值,对象/资产引用渲染为 `ObjectRef`/资源路径;`params.componentTypes` 可选过滤。只到顶层 → 避免复杂 prefab 数据爆/循环。
- **D2 get_hierarchy 所有已加载场景完整树**(你选):遍历所有 loaded scene;`params.root`(ObjectRef,可选)/`params.maxDepth`(可选)收窄。
- **D3 list_assets AssetDatabase 搜索风格**(你选):`params` 可带 `type`/`folder`/`query`(底层 `AssetDatabase.FindAssets`),返回 `path+guid+type`;无 filter 时限数(默认上限,如 1000)并带 `truncated` 标记。
- **D4 ObjectRef 解析放共享位置**:`SceneObjectResolver` 不属 inspection 私有——cmd-mutation 也会用(4.5 是 M5 内多 feature 共享)。解析规则:`instanceId` 优先,否则按 `path` 跨已加载场景查;找不到抛 handler 自有错误码。
- **D5 4 命令均实现 `ICommandSchema`**:遵守 4.7「命令必带描述 + 尽量给参数 schema」硬约束。
- **D6 新增 handler 自有错误码**(4.1 允许):`OBJECT_NOT_FOUND` / `COMPONENT_NOT_FOUND` / `INVALID_OBJECT_REF`。

### 前置依赖
bridge-core + cmd-introspection(均 done)。

## 2. 名词与编排

### 2.1 名词层

**现状**:
- handler 框架(`ICommandHandler`/`[Command]`/`ICommandSchema`/`CommandRegistry`)已就绪(bridge-core + cmd-introspection)。
- roadmap 4.5 定义了 `ObjectRef`/`ComponentRef` 契约,但**代码里还没有**(此前无命令用到)——本 feature 首次落地。

**变化**(全部新增):

| 名词 | 角色 |
|---|---|
| `ObjectRef` / `ComponentRef` | 4.5 引用 DTO(共享) |
| `SceneObjectResolver` | ObjectRef→GameObject、ComponentRef→Component(共享,mutation 复用) |
| `PropertySerializer` | 组件顶层 `SerializedProperty` → JSON 值(基本类型/引用渲染) |
| `GetHierarchyHandler` | `[Command("get_hierarchy", ...)]` + `ICommandSchema` |
| `GetObjectHandler` | `[Command("get_object", ...)]` + `ICommandSchema` |
| `GetSelectionHandler` | `[Command("get_selection", ...)]` + `ICommandSchema` |
| `ListAssetsHandler` | `[Command("list_assets", ...)]` + `ICommandSchema` |

**接口示例**(输入→输出):
```jsonc
// get_object —— 输入
{ "object": { "path": "Player/Body", "instanceId": null }, "componentTypes": ["Transform"] }
// 输出 result
{ "object": { "name":"Body", "path":"Player/Body", "instanceId":12345, "active":true },
  "components": [
    { "type":"UnityEngine.Transform", "index":0,
      "properties": { "m_LocalPosition": {"x":0,"y":1,"z":0}, "m_LocalScale": {"x":1,"y":1,"z":1} } }
  ] }

// get_hierarchy —— 输出 result
{ "scenes": [ { "scene":"Assets/Scenes/Main.unity",
    "roots": [ { "name":"Player","path":"Player","instanceId":111,"active":true,
                 "children":[ { "name":"Body","path":"Player/Body","instanceId":12345,"active":true,"children":[] } ] } ] } ] }

// get_selection —— 输出 result
{ "selection": [ { "name":"Body","path":"Player/Body","instanceId":12345 } ] }   // 空选中 → []

// list_assets —— 输入 { "type":"Material", "folder":"Assets/Art", "query":"rock" }
// 输出 result
{ "assets": [ { "path":"Assets/Art/rock.mat","guid":"ab12...","type":"UnityEngine.Material" } ],
  "count": 1, "truncated": false }
```

### 2.2 编排层

**主流程图**(4 命令都经现有 dispatch,无宿主改动;以 get_object 为例):
```mermaid
flowchart TD
  Req[get_object 请求] --> D[CommandDispatcher.Dispatch]
  D --> H[GetObjectHandler.Execute]
  H --> R[SceneObjectResolver 解析 ObjectRef]
  R -- 找不到 --> E[抛 CommandException OBJECT_NOT_FOUND]
  R -- GameObject --> C[遍历组件(可按 componentTypes 过滤)]
  C --> P[PropertySerializer 序列化每组件顶层属性]
  P --> Out[构造 result]
```

**现状**:dispatch 循环已就绪(bridge-core);无 inspection 命令、无 ObjectRef 解析。

**变化**:新增 4 handler + 共享解析/序列化;**不改宿主/分发器/通道**(只挂新命令)。

**流程级约束**:
- **只读**:所有命令不修改场景/对象/资产(不置 dirty、不 Create/Save/Destroy)。
- **主线程**:handler 在 update 回调内执行,直接用 `EditorSceneManager`/`Selection`/`AssetDatabase`。
- **ObjectRef 解析**:`instanceId` 优先,否则 `path` 跨已加载场景;两者皆无/无效 → `INVALID_OBJECT_REF`;找不到 → `OBJECT_NOT_FOUND`;组件类型不存在 → `COMPONENT_NOT_FOUND`。
- **有界返回**:`get_object` 只到顶层属性(引用不深入,渲染为 ObjectRef/资源路径);`list_assets` 无 filter 时限数 + `truncated`。
- **自描述**:4 命令均带 `[Command]` 描述 + `ICommandSchema` 参数 schema(4.7)。

### 2.3 挂载点清单

| 挂载位置 | 文件 | 动作 |
|---|---|---|
| `get_hierarchy` 命令注册 | `GetHierarchyHandler`(`[Command]`) | 新增 |
| `get_object` 命令注册 | `GetObjectHandler` | 新增 |
| `get_selection` 命令注册 | `GetSelectionHandler` | 新增 |
| `list_assets` 命令注册 | `ListAssetsHandler` | 新增 |

`SceneObjectResolver`/`PropertySerializer`/`ObjectRef` 为内部共享基础设施(非注册类挂入点),归 implement 改动计划。

### 2.4 推进策略
```
1. 共享对象引用:ObjectRef/ComponentRef DTO + SceneObjectResolver(instanceId 优先/path 跨场景)+ 错误码
   退出:手测 resolve 已知对象成功、不存在抛 OBJECT_NOT_FOUND
2. get_selection + get_hierarchy:Selection→ObjectRefs;遍历已加载场景建树(可选 root/maxDepth)
   退出:真机调返回当前选中 / 场景树
3. get_object + PropertySerializer:resolve→组件→顶层属性序列化(基本类型/引用渲染),componentTypes 过滤
   退出:真机调返回组件+属性
4. list_assets:FindAssets(type/folder/query)→path+guid+type,无 filter 限数+truncated
   退出:真机调按 filter 返回资产
5. 自描述 + 端到端边界:4 handler 加描述 + ICommandSchema;list_commands 见 4 命令带 schema;
   边界(not found / 空选中 / 无 filter 截断)
   退出:第 3 节验收场景有证据
```

### 2.5 结构健康度与微重构

##### 评估
- compound 检索(目录组织/命名):无文件组织 convention 命中(仅内容类 decision)。
- 文件级(要改):本 feature 全新增,无既有文件被实质改动。
- 目录级:`Commands/`(现 ping/list_commands 2 文件)将增 4 个 inspection handler → 6 文件;另在共享位置新增 ObjectRef/Resolver/Serializer。`Commands/` 6 文件未超阈值(<8)。

##### 结论:不做(微重构)
全新增、目录未拥挤。

##### 建议(非微重构,implement 指引)
- 4 个 inspection handler 可放 `Commands/Inspection/` 子目录;共享 `ObjectRef`/`SceneObjectResolver`/`PropertySerializer` 放新 `Editor/Scene/`(供 mutation 复用)。具体由 implement 自决。
- **观察**:随 mutation/assets/csharp 陆续加命令,`Commands/` 会增长;若未来按类别分子目录成稳定模式,可届时 `cs-decide` 归约。**本次不归档**(才 6 文件,言之过早)。

##### 超出范围的观察
无。

## 3. 验收契约

### 关键场景清单
1. **get_hierarchy**:调 → 返回所有已加载场景树,节点含 `name/path/instanceId/active/children`;`params.root`/`maxDepth` 生效(收窄)。
2. **get_object**:给 ObjectRef(path 或 instanceId)→ 返回 `object` + `components`(每组件顶层属性);`componentTypes` 过滤生效;对象/资产引用属性渲染为 ObjectRef/资源路径。
3. **get_selection**:编辑器选中对象 → 返回对应 ObjectRef 列表;**空选中 → `[]`,不报错**。
4. **list_assets**:带 `type`/`folder`/`query` → 返回匹配 `path+guid+type`;无 filter → 限数 + `truncated:true`。
5. **错误路径**:ObjectRef 不存在 → `error.code=OBJECT_NOT_FOUND`;`ComponentRef` 类型不在该对象 → `COMPONENT_NOT_FOUND`;path/instanceId 都缺 → `INVALID_OBJECT_REF`。
6. **自描述**:`list_commands` 显示 4 个新命令,各 `description` 非空、`paramsSchema` 非 null。
7. **只读**:调用任一命令后,场景未被标记 dirty、无资产新增/修改(肉眼 + 必要时 grep 写 API)。

### 明确不做的反向核对项
- 代码**不出现**写操作:grep 无 `SetDirty`/`Undo.`/`AssetDatabase.Create`/`AssetDatabase.Save`/`DestroyImmediate`/`Instantiate`(inspection 范围内)。
- 仅注册 `get_hierarchy`/`get_object`/`get_selection`/`list_assets` 四命令(不混入写命令)。
- 4 命令都带 `[Command]` 描述(grep 4 处 `[Command("get_`/`list_assets` 均含第二参数)。

## 4. 与项目级架构文档的关系

acceptance 提炼回 `architecture/ARCHITECTURE.md`:
- **名词**:`ObjectRef`/`ComponentRef` 共享引用方案(系统级,mutation 也用)→ 术语表 + 模块索引;4 个只读命令 → M5 命令列表。
- **流程级约束**:ObjectRef 解析规则(instanceId 优先/path)、只读约束、有界返回 → 已知约束。

关联:roadmap `file-bridge` 4.3/4.5/4.7;requirement `agent-editor-control`;decision `command-discovery-mechanism`(命令带描述/schema)。
