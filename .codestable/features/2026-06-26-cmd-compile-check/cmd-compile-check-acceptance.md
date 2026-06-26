---
doc_type: feature-acceptance
feature: 2026-06-26-cmd-compile-check
status: accepted
summary: cmd-compile-check 验收闭环——recompile + get_compile_result(异步两步、SessionState 跨 reload 收集编译 error/warning);架构归并编译自检子系统;requirement 加变更日志;真实编译+reload 闭环以活体验证为证据
tags: [unity, agent, compilation, self-check, acceptance]
---

# cmd-compile-check 验收报告

> 阶段:阶段 3(验收闭环)
> 验收日期:2026-06-26
> 关联方案 doc:`.codestable/features/2026-06-26-cmd-compile-check/cmd-compile-check-design.md`

验收方式:AI 无法 headless 编译/驱动 Unity 编译+domain reload。本验收以**代码评审 + API 一致性 + design 对照**确认实现到位、命名/结构一致;**EditMode 单测(映射 + 读预置 SessionState)** 覆盖可测部分;**真实编译闭环(写错码→recompile→get_compile_result 读到 error)由用户活体验证**(D6 边界)。

## 1. 接口契约核对

对照 design 第 2.1 节:
- [x] `recompile`(`Commands/Compilation/RecompileHandler.cs`):`{command:"recompile"}` → `{status:ok, result:{requested:true}}`。代码:`Execute` 调 `CompilationPipeline.RequestScriptCompilation()` 后立即 `return new {requested=true}` → **一致**。
- [x] `get_compile_result`(`GetCompileResultHandler.cs`):读 `CompileMonitor.Read()`,`Where(type=="error"/"warning")` 拆 `errors[]`/`warnings[]`,返回 `{compiling,compiledAt,errorCount,warningCount,errors,warnings}` → **一致**(errors/warnings 分两数组,符合用户决策)。
- [x] `CompileMonitor`(`Editor/Compilation/`):`[InitializeOnLoad]` 订阅 `compilationStarted`(清空+compiling=true)/ `assemblyCompilationFinished`(逐条 Map 追加)/ `compilationFinished`(compiling=false+compiledAt);读写 `SessionState["AgentBridge.CompileResult"]` → **一致**。
- [x] DTO `CompileMessage{file,line,column,message,type}` / `CompileResult{compiling,compiledAt,messages[]}` → **一致**。
- [x] 流程图(2.2):写脚本→recompile→RequestScriptCompilation→响应先于 reload→三事件填 SessionState→get_compile_result 读。代码落点齐(grep 确认 `RequestScriptCompilation`/三事件订阅/`SessionState`)。

**偏差**:无。

## 2. 行为与决策核对

**关键决策落地**:
- [x] D1 两命令异步:`recompile` 立即返回 + `get_compile_result` 读结果(无同步"编译并返回")。
- [x] D2 SessionState 持久化:`CompileMonitor` 读写 SessionState,无文件落盘(grep 收集器无 File 写)。
- [x] D3 专用 recompile:用 `RequestScriptCompilation`,不复用 refresh;refresh 未改。
- [x] D4 三事件收集:三个事件均订阅,started 清空、assemblyFinished 追加、finished 盖戳。
- [x] D5 错误语义:`get_compile_result` 恒 `ok` 带结果体(空态/编译中/有错都 ok);两命令可被禁用(复用 dispatch 的 COMMAND_DISABLED)。
- [x] D6 测试边界:EditMode 测映射 + 读预置态;真实编译闭环活体——已照此落地。

**明确不做(反向核对)**:
- [x] 无"单命令同步编译并返回结果"(`recompile` 只触发+返回,不等编译)。
- [x] 不持久化到 `AgentBridge/` 文件(用 SessionState)。
- [x] 不改 `AgentBridgeHost.Tick`/`CommandDispatcher`/`refresh`(本 feature git 仅新增 `Editor/Compilation/`、`Commands/Compilation/`、`CompilationCommandTests.cs`;dispatcher/host 的改动属之前未提交批次,非本次)。
- [x] 不返回编译产物/IL/程序集列表——只回 error/warning 消息。

**流程级约束**:
- [x] recompile 在 reload 前返回:handler 仅触发+同步 return。
- [x] 结果只经 SessionState 跨 reload;`CompileMonitor` 是唯一写入方,handler 只 `Read`。
- [x] 编译错误是数据非命令错误:`get_compile_result` 报错时仍 `status:ok`,错误在 `result.errors`。

**挂载点反向核对(可卸载性)**——对照 2.3:
- [x] `RecompileHandler.cs` / `GetCompileResultHandler.cs` / `CompileMonitor.cs` 均有落点。
- [x] 反向核查(grep):本 feature 不往 host/dispatch/registry 插桩——纯 M3 自动注册 + 独立 `[InitializeOnLoad]` 收集器。
- [x] 拔除沙盘:删 `Editor/Compilation/` + `Commands/Compilation/` → 两命令消失、收集停止,生产其余零影响(SessionState 键自然失效)。

## 3. 验收场景核对

对照 design 第 3 节:
- [~] **S1 recompile 触发**:代码评审(RequestScriptCompilation+requested:true);真实重编译**活体待验**(EditMode 不能跑 recompile——会 reload 中断测试运行)。
- [~] **S2 干净代码后 errorCount:0**:空态单测 `GetCompileResult_EmptyWhenNoCompile` 证 errorCount=0/空数组;"真实编译成功后"路径活体待验。
- [~] **S3 错误代码 → errors[0] 有 file/line/message**:拆分逻辑经 `GetCompileResult_SplitsErrorsAndWarnings`(预置)证;**真实写错码→recompile→读到 error 活体待验**。
- [x] **S4 尚无编译**:✅ `GetCompileResult_EmptyWhenNoCompile`(compiling:false、compiledAt:null、空数组)。
- [x] **S5 编译中**:✅ `GetCompileResult_CompilingFlag`(compiling:true)。
- [~] **S6 跨 reload 存活**:SessionState 语义(Unity 保证跨 reload),**活体待验**(EditMode 无法触发真 reload)。
- [x] **S7 warnings 拆分**:✅ `GetCompileResult_SplitsErrorsAndWarnings`(1 error + 1 warning → errorCount=1/warningCount=1/分两数组)。
- [x] **Map 映射**:✅ `Map_ErrorAndWarning`(Error→"error"、Warning→"warning"、字段拷贝)。
- [x] **注册**:✅ `Commands_Registered`(recompile + get_compile_result 在 CommandRegistry.Commands)。
- [~] **S8 禁用**:复用 dispatch 的 COMMAND_DISABLED(`DispatchTests.Dispatch_DisabledCommand_Rejected` 已证机制);未对这两命令单独再测。

`[x]` = 单测覆盖;`[~]` = 真实编译/reload 相关,活体待用户验(D6 边界,本 feature 性质即此)。

## 4. 术语一致性

- `recompile`/`get_compile_result`/`CompileMonitor`/`CompileMessage`/`CompileResult` 与 design §0 一致(grep 代码命中一致)✓
- 防冲突:这些名 grep 全仓仅本 feature 命中 ✓

## 5. 架构归并

已**实际写入** `.codestable/architecture/ARCHITECTURE.md`:
- [x] M5 命令集行 + 子目录:加 `recompile/get_compile_result(编译自检)`、`Commands/{...,Compilation}` ✓
- [x] §3 新增"编译自检子系统(`Editor/Compilation/` + `Commands/Compilation/`)"小节:异步两步原因(reload)+ recompile/get_compile_result + CompileMonitor/SessionState + AI 流程 ✓
- [x] §5 新增"编译自检(compilation)纪律"约束(异步两步、SessionState 单向、编译错误是数据)✓
- [x] 头部最近更新 → 2026-06-26(cmd-compile-check)✓

判据自检:未读 design 的人现可从 ARCHITECTURE 知道"有编译自检命令、为何异步两步、怎么交互"。归并到位。

## 6. requirement 回写

frontmatter `requirement: agent-editor-control`(current)。本 feature 加"AI 自检编译错误"子能力、用户视角(pitch/边界)未变 → **已加变更日志**(2026-06-26 条),愿景未覆盖。

## 7. roadmap 回写

frontmatter 无 `roadmap`/`roadmap_item` → **非 roadmap 起头,跳过**(横切能力 feature,不属 file-bridge roadmap 既有条目)。

## 8. attention.md 候选盘点

- [x] **有候选**:"改脚本/触发编译会 domain reload 打断在途请求;编译自检须 `recompile` 后轮询 `get_compile_result`,不能指望同步拿结果"。下个涉及编译/长任务的 feature 会再撞。登记不擅写。

## 9. 遗留

- **真实编译闭环活体待验**:S1/S2(真实成功)/S3(真实报错)/S6(跨 reload)由用户在宿主工程活体验证(写错码→recompile→等 reload→get_compile_result)。首跑可能微调 `UnityEditor.Compilation` API(`CompilerMessage` 字段、`RequestScriptCompilation()` 重载、`SessionState`)。
- **6.6 隐式转换**:与本 feature 无关,但同期 entityid 迁移留有"6.6 移除 int→EntityId 隐式转换待处理"(见那批改动)。
- **长任务作业模式**:本 feature 只解决"编译"这一种 reload 型异步;通用"长任务(烘焙/批处理)异步作业"模式仍未抽象,日后如有需求单独开 feature(同构 recompile+get_compile_result)。
- 实现阶段无"顺手发现"(未碰生产既有代码)。
