# Attention

本文件是 CodeStable 技能启动必读的项目注意事项入口。所有 CodeStable 子技能开始工作前必须读取它。

## 项目碎片知识

<!-- cs-note managed: 用 cs-note 维护，新条目按下面分节追加 -->

### 编译与构建

### 运行与本地起服务

- 桥接默认随编辑器加载自启(`[InitializeOnLoad]`);失焦时编辑器会节流、`EditorApplication.update` 不跑、桥接不轮询 → 要失焦也驱动,先在 Unity 点 `Tools/AgentBridge/Enable Background (No Throttling)`。

### 测试

### 命令与脚本陷阱

- 触发脚本编译 / 改 `.cs`(含命令调 `CompilationPipeline.RequestScriptCompilation`)会引发 domain reload,打断在途请求(被 `ReclaimOrphans` 补 INTERRUPTED);故编译/reload 类操作做不到一条命令同步拿结果——须异步两步:触发命令立即返回 + 轮询读结果(如 `recompile` → 轮询 `get_compile_result` 到 `compiling:false`)。

### 路径与目录约定

### 环境变量与凭证

### 其他
