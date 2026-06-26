---
doc_type: feature-acceptance
feature: 2026-06-25-cmd-assets
status: accepted
summary: cmd-assets 验收闭环——5 资源命令 + 路径守卫/SO 解析,契约/路径安全/范围守护全核对通过;真机测试经用户决定跳过,运行时以代码评审为证据
tags: [unity, agent, assets, assetdatabase, acceptance]
---

# cmd-assets 验收报告

> 阶段:阶段 3(验收闭环)
> 验收日期:2026-06-25
> 关联方案 doc:`.codestable/features/2026-06-25-cmd-assets/cmd-assets-design.md`

验收方式说明:本次核对以**代码评审 + grep 契约核对 + design 对照**为主。**用户明确决定跳过真机测试**(对话:"不用测试 继续"沿用至本轮),第 3 节运行时场景以**代码层证据**为准,标注"真机未测";编译验证亦未在本会话进行(Unity Editor-only,无 headless 编译)。取舍由用户承担,记录在案。

## 1. 接口契约核对

对照方案第 2.1 节名词层逐一核查:

**接口示例逐项核对**:
- [x] `import_asset`(`Commands/Assets/ImportAssetHandler.cs:Execute`):`{source, destination}` → `{path, guid, type}`。代码:source 校验存在 → destination 过路径守卫 → 目标目录校验 → `File.Copy` → `ImportAsset` → 返回 path+guid+type → **一致**。
- [x] `create_asset`(`CreateAssetHandler.cs`):`{kind, path, content?, type?}` → folder `{path}` / text `{path,guid}` / SO `{path,guid,type}`。三私有方法分支齐全 → **一致**。
- [x] `move_asset`(`MoveAssetHandler.cs`):`{from, to}` → `{from, to}`;`MoveAsset` 非空错误串 → `ASSET_MOVE_FAILED` → **一致**。
- [x] `delete_asset`(`DeleteAssetHandler.cs`):`{path}` → `{deleted:true}`;`MoveAssetToTrash` → **一致**。
- [x] `refresh`(`RefreshHandler.cs`):`{}` → `{refreshed:true}` → **一致**。

**名词层"现状 → 变化"逐项核对**(全部新增,落 `Commands/Assets/`):
- [x] 5 个 Handler:均带 `[Command]` + `ICommandHandler` + `ICommandSchema` → **一致**。
- [x] `AssetErrorCodes`:6 错误码,域内私有放 `Commands/Assets/` → **一致**(与 design D8/2.5 一致)。
- [x] `AssetSupport`(`internal`):`RequireProjectPath`(路径守卫)+ `ResolveScriptableObjectType`(SO 子类解析)→ **一致**(2.3「内部实现」预告)。

**流程图核对**(第 2.2 节 import_asset mermaid):
- [x] `Dispatch → ImportAssetHandler.Execute → 校验 destination 在 Assets/ 下(INVALID_ASSET_PATH)→ 校验 source 存在(ASSET_SOURCE_NOT_FOUND)→ File.Copy → ImportAsset → result`,逐节点 grep 均有落点。

**偏差**:无。`AssetSupport` 命名为 design 2.3 预告的"内部实现",非新概念。

## 2. 行为与决策核对

对照方案第 1 节 + 第 2.2 节:

**需求摘要逐项验证**:
- [x] 5 资源命令均落地为独立 handler,经现有 dispatch,无宿主改动(grep 未碰 Dispatch/Channel/Host)。

**明确不做逐项核对**(第 3 节反向核对项):
- [x] 不碰场景/组件:assets handler grep 无 `SceneObjectResolver|SetDirty|MarkSceneDirty|Undo.`(exit 1)。
- [x] 不做 execute_csharp:无运行时编译入口。
- [x] 写路径不越 Assets/:`RequireProjectPath` 强制 `Assets/` 前缀 + 拒 `..`;4 写命令(create/delete/import-dest/move from+to)全过守卫(grep 6 处调用)。
- [x] 不做资产内容深编辑:仅 create/import/move/delete/refresh,无属性级改写。

**关键决策落地**:
- [x] D1 import=外部文件进工程:`File.Copy(source→destination)` + `ImportAsset`,source 只读不守卫、destination 守卫。
- [x] D2 create 三 kind:folder(`CreateFolder`,幂等判 `IsValidFolder`)/ text(`WriteAllText`+`ImportAsset`)/ SO(`ResolveScriptableObjectType`+`CreateInstance`+`CreateAsset`)。
- [x] D3 move=移动/改名:`AssetDatabase.MoveAsset` 错误串 → `ASSET_MOVE_FAILED`。
- [x] D4 delete=回收站:`MoveAssetToTrash`(grep 无 `DeleteAsset`)。
- [x] D5 即时持久化:create SO 后 `SaveAssets`;无 dirty-only 路径。
- [x] D6 写路径守卫:`RequireProjectPath` 限 Assets/、拒 `..`。
- [x] D7 5 命令 ICommandSchema:5 个 `GetParamsSchema()` 非 null。
- [x] D8 自有错误码:`AssetErrorCodes` 6 个。

**流程级约束核对**:
- [x] 写路径守卫 / 即时持久化 / 不可 Ctrl-Z(回收站)/ 主线程 / 不碰场景组件 / 自描述 —— 逐条代码体现。
- [x] 幂等性:`refresh` 幂等;`create folder` 已存在返回原 path;`move`/`delete` 二次 → `ASSET_MOVE_FAILED`/`ASSET_NOT_FOUND`。

**挂载点反向核对(可卸载性)**——对照第 2.3 节:
- [x] 挂载点 M1-M5(5 命令注册):反射自动发现,挂载点即 5 handler 的 `[Command]`;grep 确认 `Commands/Assets/` 恰 5 条 `[Command(`。
- [x] **反向核查**:`grep import_asset|create_asset|move_asset|delete_asset|refresh` 命中全部落在 `Commands/Assets/` 内;`AssetErrorCodes`/`AssetSupport` 仅被本目录引用。
- [x] **拔除沙盘推演**:删除 `Commands/Assets/` 整目录后,反射注册表不再发现这 5 命令,框架与 inspection/mutation 不受影响(assets 不被任何其他模块引用)→ 可干净拔除,无残留。

## 3. 验收场景核对

对照方案第 3 节关键场景清单。**运行时证据 = 代码评审(真机经用户决定跳过)**:

- [~] **S1 import_asset**:source 校验 + destination 守卫 + File.Copy + ImportAsset + 返回 path/guid/type;source 缺→`ASSET_SOURCE_NOT_FOUND`、destination 越界→`INVALID_ASSET_PATH`。证据:代码评审;**真机未测**。
- [~] **S2 create folder**:`IsValidFolder` 幂等 + `CreateFolder`。证据:代码评审;**真机未测**。
- [~] **S3 create text**:`WriteAllText`+`ImportAsset`。证据:代码评审;**真机未测**。
- [~] **S4 create SO**:`ResolveScriptableObjectType`+`CreateInstance`+`CreateAsset`+`SaveAssets`;未知类型→`UNKNOWN_ASSET_TYPE`。证据:代码评审;**真机未测**。
- [~] **S5 move_asset**:`MoveAsset` 错误串→`ASSET_MOVE_FAILED`。证据:代码评审;**真机未测**。
- [~] **S6 delete_asset**:存在性检查→`ASSET_NOT_FOUND`,否则 `MoveAssetToTrash`。证据:代码评审;**真机未测**。
- [~] **S7 refresh**:`AssetDatabase.Refresh`→`refreshed:true`。证据:代码评审;**真机未测**。
- [x] **S8 写路径守卫**:`RequireProjectPath` 对 `Assets/` 外路径(C:/x、Packages/...)及 `..` 抛 `INVALID_ASSET_PATH`,4 写命令全过。证据:代码评审(grep 6 处守卫调用)。
- [x] **S9 自描述**:5 `[Command]` 描述非空 + 5 `GetParamsSchema()` 非 null → `CommandRegistry` 反射必收录。证据:代码评审。
- [x] **S10 持久化语义**:create SO 后 `SaveAssets`,无 dirty-only 路径(资产即时落盘)。证据:代码评审。

说明:S1–S7 标 `[~]`(代码完成、真机未测,用户已同意跳过);S8–S10 为代码层可静态确证项,标 `[x]`。无前端改动。

## 4. 术语一致性

对照方案第 0 节 + 第 2.1 节命名 grep:

- 5 命令名:`[Command]` = `Command` 属性 = 注释一致 ✓
- 新类型 `AssetErrorCodes`/`AssetSupport`/5 Handler 命名与 design 一致 ✓
- 防冲突:命令名(含通用词 `refresh`)/类型名 grep 无既有冲突 ✓
- 复用类型零改写(本 feature 不依赖 inspection/mutation 类型)✓

无不一致。

## 5. 架构归并

对照方案第 4 节,已**实际写入** `.codestable/architecture/ARCHITECTURE.md`:

- [x] **名词归并**:§3 M5 行更新,列出 5 个资源命令;并补一行"handler 按命令域分子目录 `Commands/{Inspection,Mutation,Assets}/`(decision commands-category-subdirectory)" ✓
- [x] **流程级约束归并**:§5 新增"资源操作命令(assets)纪律"约束条(即时落盘非 dirty-only + 回收站 + 写路径限 Assets/ + 路径寻址不走 ObjectRef)✓
- [x] 头部"最近更新"改为 2026-06-25(cmd-assets 验收归并)✓

判据自检:未读 design 的人打开 ARCHITECTURE.md 现可知"系统有 5 个资源命令、资产操作即时落盘且删除进回收站、写路径只允许 Assets/ 下、handler 按域分子目录"。归并到位。

## 6. requirement 回写

方案 frontmatter `requirement: agent-editor-control`(status: current)。

- [x] 指向 current req,本次**未改**用户故事/边界/pitch(资源操作属 pitch「指挥编辑器干活」愿景内)→ 不动愿景,在 req 文末**追加一条交付进度变更日志**(2026-06-25,记 cmd-assets 落地、读/场景写/资源三类齐备、仅剩 cmd-csharp、真机跳过)。已实际写入 `requirements/agent-editor-control.md`。

## 7. roadmap 回写

方案 frontmatter `roadmap: file-bridge` / `roadmap_item: cmd-assets`,两字段均有值,必须回写:

- [x] `file-bridge-items.yaml`:`cmd-assets` 由 `in-progress` → `done`(`feature` 核对一致)。
- [x] `validate-yaml.py --file` 校验通过(见下)。
- [x] 主文档 `file-bridge-roadmap.md` §5 第 6 条同步:状态 → `done(2026-06-25,…验收;真机测试经用户决定跳过)`、对应 feature 填实、标题加 ✅ done、备注补资产语义 + convention 落地。

## 8. attention.md 候选盘点

- [x] 无新候选:本 feature 未引入新的编译命令/代理/起服务步骤/路径陷阱。已有"失焦需先开 Background"注意事项对真机调本 feature 命令同样适用,已记录无需重复。

## 9. 遗留

- **真机验证待补**:S1–S7 运行时行为(导入/建/移动/删除/回收站恢复/SO 实例化)经用户决定跳过测试,以代码评审为证据。建议日后在 Unity 中实跑一轮补证。
- **重构债(design 2.5 超范围观察)**:类型名→Type 解析现分两处——`SceneObjectResolver.FindType`(限 Component)与 `AssetSupport.ResolveScriptableObjectType`(限 ScriptableObject)。建议后续 `cs-refactor` 抽通用 `TypeResolver`(按基类过滤)收敛。不阻塞。
- 已知限制:`create_asset folder`/`text` 要求父目录已存在(不递归建链);需先逐级 create 或先 import。
- 已知限制:`import_asset`/`create_asset text` 用 `overwrite`/覆盖写,目标已存在会被覆盖(无显式确认)。
- 实现阶段无"顺手发现"。
