# bridge-core 验收报告

> 阶段：阶段 3（验收闭环）
> 验收日期：2026-06-24
> 关联方案 doc：.codestable/features/2026-06-24-bridge-core/bridge-core-design.md
> 真机环境：Unity 6000.3.12f1，工程 G:\GitHub\X\Unity,包经 disk 安装、AgentBridge.Editor 已编译、宿主自动启动

## 1. 接口契约核对

**接口示例逐项核对**：
- [x] 协议信封(design 2.1,来源 file-bridge 4.1)：真机 `x1.response.json` 实测 `{v,id,status,result,error,timestamp}` 字段齐全;`status=ok` 时 `error=null`、`result={message:pong, unityVersion}`;`status=error` 时 `result=null`、`error={code,message}`(见 x2/x3/x6)。一致。
- [x] handler 框架(ICommandHandler.Command/Execute(JObject))：`PingHandler.Execute` 返回匿名对象 → 序列化进 `result`。一致。
- [x] 分发器 `CommandDispatcher.Dispatch(Request)→Response` 永不抛：未知命令/异常/解析失败均转 error 响应(x2/x3 实测)。一致。
- [x] `FileChannel.TryClaimNext / WriteResponse`：requests→processing 原子 move、tmp→rename 写响应。一致(x4/x5 实测)。

**名词层"现状 → 变化"逐项核对**：13 个全新类型均落地,与 2.1 名词表逐一对应(Request/Response/ErrorInfo/ErrorCodes/ICommandHandler/CommandAttribute/CommandException/CommandRegistry/CommandDispatcher/FileChannel/BridgeSettings/AgentBridgeHost/PingHandler)。无现状(全新工程)。一致。

**流程图核对**(2.2 mermaid)：
- [x] 加载→InitializeOnLoad→Start → `AgentBridgeHost.cs:12,29`
- [x] 扫 processing 孤儿补 INTERRUPTED → `ReclaimOrphans` (`AgentBridgeHost.cs`),x6 实测
- [x] update 轮询节流 → `Tick` `EditorApplication.update`(`AgentBridgeHost.cs:33`)
- [x] 认领 requests→processing → `FileChannel.TryClaimNext`
- [x] 解析失败→INTERNAL_ERROR / Dispatch → `CommandDispatcher`
- [x] 写响应 tmp→rename + 删 processing → `WriteResponse`/`ReleaseProcessed`

无偏差。

## 2. 行为与决策核对

**需求摘要逐项验证**：
- [x] Agent 写请求→Unity 轮询认领→主线程执行→写响应→Agent 读回：x1 实测往返成功。
- [x] 成功标准(ping→pong,200ms 量级):实测响应数百 ms 内到达。

**明确不做逐项核对**(反向核对项,grep 证据)：
- [x] 无 HTTP/Socket/网络:`grep HttpListener|TcpListener|Socket|UnityWebRequest|WebClient` → 0 命中。
- [x] 仅 ping 命令:`grep \[Command\(` → 仅 `PingHandler.cs` 实际命中(README/ICommandHandler 内为文档/注释,非编译命令)。
- [x] 纯 Editor:asmdef `includePlatforms:[Editor]`。
- [x] 不碰 Features/:`grep Features` 于 Unity/ → 0 命中。
- [x] 临时 boom 测试 handler 已删除(`Assets/Editor/__AgentBridgeTestBoom.cs` 不存在)。

**关键决策落地**：
- [x] D1 Newtonsoft:package.json 声明依赖;协议类型用 `[JsonProperty]` + `JObject`/`JToken`。
- [x] D2 纯 UPM 包:`Unity/package.json` + `Unity/Editor/`,与 `Features/` 并列;bridge-core 未碰 Features。
- [x] D3 Editor-only asmdef:`includePlatforms:[Editor]`。
- [x] D4 轮询:`EditorApplication.update` + `PollIntervalMs` 节流。
- [x] D5 domain reload 孤儿→INTERRUPTED(at-most-once):`ReclaimOrphans`,x6 实测无重复执行。

**编排层变化逐项核对**：轮询主循环整条建立(见 1 节流程图核对),与 2.2"变化"一致。

**流程级约束核对**：
- [x] 主线程:Dispatch 在 update 回调内同步调用。
- [x] 单次认领:`File.Move` 原子,失败跳过;x5 实测 processing 空、仅 1 响应。
- [x] 原子读写:tmp→rename;x4 实测半截 .tmp 不被处理。
- [x] 错误语义:Dispatch 永不抛,每请求必有响应;x2(UNKNOWN_COMMAND)/x3(HANDLER_EXCEPTION)实测。
- [x] 幂等/中断:x6 INTERRUPTED 实测,不重试。
- [x] 扩展点:`CommandRegistry` 反射扫描 `[Command]`;命令名重复拒绝注册(代码 `CommandRegistry.Rebuild`)。
- [x] 可观测:Console 日志(start/stop/错误)。

**挂载点反向核对(可卸载性)**：
- [x] M1 `[InitializeOnLoad]` → `AgentBridgeHost.cs:12`
- [x] M2 `EditorApplication.update` 订阅 → `AgentBridgeHost.cs:33,43`
- [x] M3 `[Command]` 反射注册扫描点 → `CommandRegistry`
- [x] M4 `Tools/AgentBridge/Start|Stop` 菜单 → `AgentBridgeMenu.cs:8,11,14,17`
- [x] M5 `BridgeSettings` EditorPrefs(PollIntervalMs/RootDir) → `BridgeSettings.cs`
- [x] **反向 grep**：本 feature 的全部对外挂入(InitializeOnLoad/update/MenuItem/EditorPrefs/Command)均落在清单内,无清单外引用。
- [x] **拔除沙盘**：删除 `Unity/` 包 → 桥接消失、`AgentBridge/` 目录不再创建、菜单消失、EditorPrefs key 残留无害。无残留代码。

## 3. 验收场景核对

| 场景 | 证据来源 | 结果 |
|---|---|---|
| S1 正常 ping 往返 | 手工/真机 | [x] 通过——x1: status=ok, result.message=pong, id 回显 |
| S2 未知命令 | 手工/真机 | [x] 通过——x2: error.code=UNKNOWN_COMMAND |
| S3 handler 异常 | 手工/真机(临时 boom) | [x] 通过——x3: error.code=HANDLER_EXCEPTION + 堆栈摘要(:9) |
| S4 半截 .tmp 不处理 | 手工/真机 | [x] 通过——rename 前无响应,rename 后 pong |
| S5 认领单次无残留 | 手工/真机 | [x] 通过——processing 空, x5 仅 1 响应 |
| S6 domain reload 中断 | 手工/真机(孤儿+重编译) | [x] 通过——x6: error.code=INTERRUPTED, 不重复 |
| S7 配置生效 | 手工/真机(默认值) | [◑] 部分——默认 RootDir(工程根/AgentBridge)与 200ms 轮询实地生效;改非默认值未单独验证(EditorPrefs 实时读取,代码层确认 `BridgeSettings`) |

**反向核对项**:全部守住(见第 2 节明确不做)。

> S7 备注:改值路径无 UI,需临时菜单触发。默认值生效已由 S1-S6 全程实证(收发目录在工程根、响应数百 ms 到达)。改值是 EditorPrefs getter 的实时读取,风险极低,记为部分通过、不阻塞验收。

## 4. 术语一致性

- `namespace AgentBridge`:14 个 .cs 文件全部一致(grep count 确认)。
- 术语 Command/Handler/Claim/Request/Response:命名与 design 第 0 节一致。
- 防冲突:空仓库起步,无禁用词冲突。

无不一致。

## 5. 架构归并

对照 design 第 4 节,已**实际写入** `.codestable/architecture/ARCHITECTURE.md`：
- [x] 名词归并：Request/Response/ErrorInfo 协议信封、ICommandHandler/CommandAttribute 框架契约 → 写入第 2 节术语表 + 第 3 节模块索引。
- [x] 动词骨架归并：轮询认领→分发→响应主循环、M1~M6 六模块 → 写入第 3 节。
- [x] 流程级约束归并：原子写、单次认领、主线程执行、domain reload→INTERRUPTED、命令名唯一、扩展点 → 写入第 5 节已知约束。
- [x] 关键架构决定(文件 IPC/轮询/反射注册/Editor-only) → 写入第 4 节。

归并后,未读 design 者打开 ARCHITECTURE.md 即可知系统有此能力、其形态(文件协议+轮询+handler 框架)、交互约束。

## 6. requirement 回写

design frontmatter `requirement` 原为空,bridge-core 交付了用户(Agent/开发者)可感的新能力("Agent 通过文件驱动 Unity 编辑器")。
- [x] 触发 backfill：新建 `.codestable/requirements/agent-editor-control.md`,`status: current`(能力机制已交付可用);用户故事/边界按能力愿景层撰写,变更日志记录本次交付范围=传输层+ping。
- [x] 回填 design frontmatter `requirement: agent-editor-control`。

## 7. roadmap 回写

design frontmatter `roadmap: file-bridge` / `roadmap_item: bridge-core`,两字段都有值：
- [x] `file-bridge-items.yaml`:`bridge-core` 由 `in-progress` → `done`(feature 字段核对一致),validate-yaml 通过。
- [x] `file-bridge-roadmap.md` 主文档第 5 节子 feature 清单:`bridge-core` 状态标 done。

## 8. attention.md 候选盘点

- [x] 候选 1：**失焦时 Unity 编辑器不重编译、`EditorApplication.update` 也不跑**——验证/驱动文件桥接必须让 Unity 窗口在前台,否则响应不出现、重编译不触发。建议放 `attention.md`「运行与本地起服务」节。下一个命令 feature 的 AI 验证时还会撞。
- 登记不擅自写入,落不落由用户在退出后环节定(走 cs-note)。

## 9. 遗留

- 已知限制：S7 配置改值未单测(默认值已实证);改值需临时菜单触发。
- 后续优化点：无。
- 实现阶段"顺手发现"列表：无。
