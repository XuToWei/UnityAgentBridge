---
doc_type: feature-acceptance
feature: 2026-06-25-cmd-inspection
status: accepted
summary: cmd-inspection 验收闭环——4 只读命令 + 共享对象引用解析,契约/只读/有界全核对通过,已归并架构/roadmap/req
tags: [unity, agent, inspection, read-only, acceptance]
---

# cmd-inspection 验收报告

> 阶段:阶段 3(验收闭环)
> 验收日期:2026-06-25
> 关联方案 doc:`.codestable/features/2026-06-25-cmd-inspection/cmd-inspection-design.md`

验收方式说明:本次核对以**代码评审 + grep 契约核对 + design 对照**为主。运行时场景(真机调)的证据来自 **implement 阶段退出信号**(checklist `steps` 全 `done`,各步退出信号为"真机调返回…")。当前会话无法在 headless 环境重新驱动 Unity 编辑器(需前台聚焦的 Unity 实例),故运行时复测沿用 implement 阶段证据,不重复声称本会话亲自重跑。

## 1. 接口契约核对

对照方案第 2.1 节名词层逐一核查:

**接口示例逐项核对**:
- [x] `get_object`(`Commands/Inspection/GetObjectHandler.cs:Execute`):输入 `{object, componentTypes}` → 输出 `{object{name,path,instanceId,active}, components:[{type,index,properties}]}`。代码实际:`objRef` resolve → 遍历 `GetComponents<Component>()`、按 type 计数 `index`、`componentTypes` 过滤(匹配 FullName 或短名)、`PropertySerializer.SerializeTopLevel` → 结构**一致**。
- [x] `get_hierarchy`(`GetHierarchyHandler.cs`):输出 `{scenes:[{scene, roots:[{name,path,instanceId,active,children}]}]}`。代码 `BuildNode` 产出节点字段一致;`root`/`maxDepth` 可选生效 → **一致**。
- [x] `get_selection`(`GetSelectionHandler.cs`):输出 `{selection:[{name,path,instanceId}]}`,空选中 `[]`。`Selection.gameObjects.Select(...).ToArray()` 空集自然为 `[]` → **一致**。
- [x] `list_assets`(`ListAssetsHandler.cs`):输入 `{type,folder,query}` → 输出 `{assets:[{path,guid,type}], count, truncated}` → **一致**。

**名词层"现状 → 变化"逐项核对**(全部新增):
- [x] `ObjectRef`/`ComponentRef`(`Editor/Scene/ObjectRef.cs`、`ComponentRef.cs`):字段与 roadmap 4.5 完全一致(`ObjectRef={path,instanceId}`、`ComponentRef={object,type,index}`)→ **一致**。
- [x] `SceneObjectResolver`(共享,mutation 复用):`ResolveObject`(instanceId 优先 / path 跨已加载场景)、`ResolveComponent` 均落地 → **一致**。
- [x] `PropertySerializer`:顶层 `SerializedProperty` 迭代(`enter=false` 不下钻)、基本类型给值、引用渲染为 `{assetPath,type}` 或 `{instanceId,name,type,path}` → **一致**。
- [x] 4 个 Handler 均带 `[Command]` + 实现 `ICommandHandler` + `ICommandSchema` → **一致**。

**流程图核对**(第 2.2 节 get_object mermaid):
- [x] `Dispatch → GetObjectHandler.Execute → SceneObjectResolver.ResolveObject(找不到抛 OBJECT_NOT_FOUND)→ 遍历组件(componentTypes 过滤)→ PropertySerializer → result`,逐节点 grep 均有落点。

**偏差**:无未处理偏差。两处良性增强(非偏离):①`SceneObjectResolver` 在 instanceId 查不到且提供了 path 时回退到 path 查找(4.5 说"instanceId 优先",此为优先+优雅回退,语义相容);②引用序列化在 ObjectRef 字段外额外带 `name`/`type`,信息更全。均记录在案,无需修。

## 2. 行为与决策核对

对照方案第 1 节 + 第 2.2 节:

**需求摘要逐项验证**:
- [x] 4 只读命令均落地为独立 handler,经现有 dispatch,无宿主改动(`grep` 确认未改 Dispatch/Channel/Host)。

**明确不做逐项核对**(第 3 节反向核对项):
- [x] 不做写操作:`grep -rn "SetDirty|Undo.|AssetDatabase.Create|AssetDatabase.Save|DestroyImmediate|Object.Destroy|Instantiate|MarkSceneDirty|ApplyModified"` 于 `Commands/Inspection/` + `Scene/` → **无命中(exit 1)**。
- [x] 不递归深展开:`PropertySerializer.SerializeTopLevel` 用 `enter=false` 只遍历顶层;复杂类型给类型名占位 → 确认。
- [x] 不做 Play mode:无 runtime 入口,asmdef 仍 Editor-only。

**关键决策落地**:
- [x] D1 组件+顶层属性可过滤:`GetObjectHandler` 实现 componentTypes 过滤 + `PropertySerializer` 顶层序列化。
- [x] D2 所有已加载场景完整树 + root/maxDepth:`GetHierarchyHandler` 遍历 `SceneManager`,`maxDepth=-1` 为无限。
- [x] D3 list_assets FindAssets 风格 + 无 filter 限数:`NoFilterCap=1000` + `truncated`;另对 `type` 做主资产类型精确二次过滤(修正 `t:` 不精确)。
- [x] D4 ObjectRef 解析放共享 `Editor/Scene/`:`SceneObjectResolver` 静态共享类。
- [x] D5 4 命令实现 ICommandSchema:4 个 `GetParamsSchema()` 均返回非 null schema。
- [x] D6 handler 自有错误码:`RefErrorCodes` 定义 `INVALID_OBJECT_REF`/`OBJECT_NOT_FOUND`/`COMPONENT_NOT_FOUND`。

**流程级约束核对**:
- [x] 只读 / 主线程 / ObjectRef 解析语义 / 有界返回 / 自描述 —— 逐条在代码体现(见上各项)。

**挂载点反向核对(可卸载性)**——对照第 2.3 节:
- [x] 挂载点 M1-M4(4 命令注册):本项目注册机制为反射自动发现(`CommandRegistry.Rebuild` 扫 `[Command]`+`ICommandHandler`),挂载点即 4 个 handler 类的 `[Command]` 特性本身。grep 确认 `Commands/Inspection/` 恰 4 条 `[Command(`,无遗漏无多余。
- [x] **反向核查**:`grep get_hierarchy/get_object/get_selection/list_assets --include=*.cs` 全部命中落在 4 个 handler 文件内(注释+特性+`Command` 属性),无清单外引用。
- [x] **拔除沙盘推演**:删除 `Commands/Inspection/` + `Editor/Scene/` 两目录后,反射注册表不再发现这 4 命令,框架与既有 ping/list_commands 不受影响(`ObjectRef`/`Resolver`/`Serializer` 仅被 inspection 引用,mutation 尚未实现)→ 可干净拔除,无残留。

## 3. 验收场景核对

对照方案第 3 节关键场景清单:

- [x] **S1 get_hierarchy**:返回所有已加载场景树,节点字段齐全,root/maxDepth 收窄。
  - 证据来源:代码评审(`BuildNode` 字段 + maxDepth 分支)+ implement 退出信号"真机调返回场景树"。
- [x] **S2 get_object**:path/instanceId → 组件+顶层属性,componentTypes 过滤,引用渲染为 ObjectRef/资源路径。
  - 证据来源:代码评审 + implement 退出信号"真机调返回组件+属性"。
- [x] **S3 get_selection**:选中→ObjectRef 列表;空选中→`[]` 不报错。
  - 证据来源:代码评审(`.ToArray()` 空集为 `[]`)+ implement 退出信号"真机调返回当前选中"。
- [x] **S4 list_assets**:带 filter 返回匹配;无 filter 限数 + truncated。
  - 证据来源:代码评审(`NoFilterCap`/`truncated` 分支)+ implement 退出信号"真机调按 filter 返回资产"。
- [x] **S5 错误路径**:不存在→`OBJECT_NOT_FOUND`;组件类型不在→`COMPONENT_NOT_FOUND`;path/id 都缺→`INVALID_OBJECT_REF`。
  - 证据来源:代码评审(`SceneObjectResolver` 三处抛 `CommandException(RefErrorCodes.*)`)+ checklist step1 退出信号"不存在抛 OBJECT_NOT_FOUND"。
- [x] **S6 自描述**:`list_commands` 显示 4 新命令,各 description 非空、paramsSchema 非 null。
  - 证据来源:4 `[Command(name, desc)]` 描述非空 + 4 `GetParamsSchema()` 非 null;`CommandRegistry` 反射自动收录 → list_commands 必含。
- [x] **S7 只读**:调用后场景未 dirty、无资产改动。
  - 证据来源:写 API grep 无命中(见第 2 节)→ 代码层面不可能产生写副作用。

无前端改动,跳过浏览器验证。

## 4. 术语一致性

对照方案第 0 节 + 第 2.1 节命名 grep:

- `ObjectRef` / `ComponentRef`:类名、`SceneObjectResolver.ResolveObject/ResolveComponent` 参数命名全一致 ✓
- `get_hierarchy`/`get_object`/`get_selection`/`list_assets`:特性名 = `Command` 属性 = 描述/注释一致 ✓
- 防冲突禁用词(写 API)grep 无命中 ✓
- 错误码常量名与 design 第 1 节 D6 / 第 3 节 S5 文案一致 ✓

无不一致。

## 5. 架构归并

对照方案第 4 节,已**实际写入** `.codestable/architecture/ARCHITECTURE.md`:

- [x] **名词归并**:§2 术语表新增 `ObjectRef / ComponentRef`、`Hierarchy 节点` 两条 ✓
- [x] **动词/模块归并**:§3 模块索引 M5 行更新,列出 4 个只读查询命令 ✓
- [x] **关键决定归并**:§4 新增"对象引用共享解析(`ObjectRef`/`SceneObjectResolver`)"决定条 ✓
- [x] **流程级约束归并**:§5 新增"只读查询命令(inspection)纪律"约束条(只读 + 有界返回 + 解析语义)✓
- [x] 头部"最近更新"改为 2026-06-25(cmd-inspection 验收归并)✓

判据自检:未读 design 的人打开 ARCHITECTURE.md 现可知"系统有 4 个只读查询命令、对象用 ObjectRef 引用并由 SceneObjectResolver 解析、调用它们不会改场景且返回有界"。归并到位。

## 6. requirement 回写

方案 frontmatter `requirement: agent-editor-control`(status: current)。

- [x] 指向 current req,本次**未改**用户故事/边界/pitch(inspection 是 2026-06-24 变更日志已预告的"场景查询"子能力)→ 按规则不改愿景,仅在 req 文末**追加一条交付进度变更日志**(2026-06-25),记录 introspection/protocol-doc/inspection 已落地、用户视角未变。已实际写入 `requirements/agent-editor-control.md`。

## 7. roadmap 回写

方案 frontmatter `roadmap: file-bridge` / `roadmap_item: cmd-inspection`,两字段均有值,必须回写:

- [x] `file-bridge-items.yaml`:`cmd-inspection` 由 `in-progress` → `done`(`feature` 已为 `2026-06-25-cmd-inspection`,核对一致)。
- [x] `validate-yaml.py --file` 校验通过(1 passed)。
- [x] 主文档 `file-bridge-roadmap.md` §5 第 4 条同步:状态 → `done(2026-06-25,…验收)`、对应 feature 填实、标题加 ✅ done、备注补共享基础设施落地。

## 8. attention.md 候选盘点

回看本次实现的环境/工具/工作流类信息:

- [x] 无新候选:本 feature 未引入新的编译命令/代理/起服务步骤/路径陷阱。已有的"失焦需先开 Background"注意事项(attention.md 运行与本地起服务节)对真机调本 feature 命令同样适用,但已记录,无需重复。

## 9. 遗留

- 已知限制:`get_object` 仅序列化顶层属性,嵌套结构(数组元素、自定义类字段)给类型名占位,深挖需 AI 按返回的 ObjectRef/资源路径再查(D1 设计取舍)。
- 已知限制:`instanceId` 跨 domain reload/会话不稳定,长链路引用建议用 `path`(4.5 既定语义)。
- 良性增强(非遗留):引用序列化额外带 `name`/`type` 字段;ObjectRef 解析 instanceId 失败回退 path。
- 后续:`SceneObjectResolver.ResolveComponent` 已实现但 inspection 自身未调用,留给 cmd-mutation 复用,届时验证其真机行为。
