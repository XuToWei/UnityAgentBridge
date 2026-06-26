---
doc_type: feature-acceptance
feature: 2026-06-25-ext-core
status: accepted
summary: ext-core 验收闭环——扩展最小闭环(manifest/安装/卸载/扫描/最小窗口),契约/范围守护全核对通过;新建 extension-management req + 架构归并 EM 子系统;真机经用户决定跳过
tags: [unity, agent, extension, acceptance]
---

# ext-core 验收报告

> 阶段:阶段 3(验收闭环)
> 验收日期:2026-06-25
> 关联方案 doc:`.codestable/features/2026-06-25-ext-core/ext-core-design.md`

验收方式说明:以**代码评审 + grep 契约核对 + design 对照**为主。**用户决定跳过真机测试**(沿用本会话节奏),第 3 节运行时场景以代码层证据为准、标"真机未测";无 headless 编译。**额外限制**:本仓库 `Unity/` 是 UPM 包、无 `Assets/` 目录,真机验证需宿主工程(见第 8/9 节)。

## 1. 接口契约核对

对照方案第 2.1 节 + roadmap §4.1-4.5:

**接口示例逐项核对**:
- [x] `ExtensionInstaller.InstallFromGitHub(repoUrl, ref=null)`(`ExtensionInstaller.cs`):→ `InstallResult{Ok,Id,ErrorCode,Message}`。流程:下 zip→解压→读校验 manifest→冲突预检→拷贝→写 meta→Refresh → **一致**。
- [x] `ExtensionInstaller.Uninstall(id)`:`Directory.Exists` 否→false;否则 `AssetDatabase.DeleteAsset`+`Refresh`→true → **一致**。
- [x] `LocalRegistry.Scan()`(`LocalRegistry.cs`):→ `List<InstalledExtension>`,`Compiled = commands 非空 && 全部 ∈ CommandRegistry.Commands` → **一致**。

**名词层"现状 → 变化"逐项核对**(全部新增 `Unity/Editor/Extensions/`):
- [x] `ExtensionManifest`/`InstalledMeta`:字段与 roadmap §4.1/§4.2 一致(id/name/version/.../sourceDir;id/version/sourceRepo/commit/installedAt/enabled)。
- [x] `ExtensionErrorCodes`:6 码与 roadmap §4.3(已 GIT_FAILED→DOWNLOAD_FAILED)一致。
- [x] `InstallResult`/`InstalledExtension`:与 §4.3/§4.5 一致。
- [x] `ExtensionManagerWindow`:`[MenuItem("Tools/AgentBridge/Extensions")]` + 安装/列表/卸载。

**流程图核对**(第 2.2 节安装 mermaid):
- [x] `InstallFromGitHub → DownloadAndExtract(DOWNLOAD_FAILED)→ ReadManifest(MANIFEST_MISSING)→ Validate(MANIFEST_INVALID)→ 冲突预检(ID_CONFLICT/COMMAND_CONFLICT)→ CopyDir → WriteMeta → AssetDatabase.Refresh`,逐节点 grep 有落点。

**偏差**:一处**契约 API 命名同步**(非偏离):roadmap 原 `InstallFromGit` 已于设计前按用户决定(zip-only)经 `cs-roadmap update` 改为 `InstallFromGitHub`,代码与更新后契约一致。无未处理偏差。

## 2. 行为与决策核对

对照方案第 1 节 + 第 2.2 节:

**需求摘要逐项验证**:
- [x] 粘 GitHub URL 安装 / 卸载 / 已装列表 —— `ExtensionManagerWindow` + `ExtensionInstaller` + `LocalRegistry` 三件齐备。

**明确不做逐项核对**(第 3 节反向核对项):
- [x] 不做启停切换:`Extensions/` grep 无 enable/disable 切换功能(命中仅 `OnEnable` 生命周期 + `DisabledScope` 空 URL 按钮 + 注释)。
- [x] 不做远程索引/搜索:无远程栏/搜索框/IndexEntry(grep exit 1)。
- [x] 无 `InstallFromIndex`(grep exit 1,留 ext-remote-index)。
- [x] 不自做命令分发:`Extensions/` 无 `[Command]`/`CommandDispatcher`/`ICommandHandler`/`Rebuild`(grep exit 1),只**读** `CommandRegistry.Commands`。
- [x] 不做 git clone/外部进程:grep 无 `Process`/`git clone`(exit 1),拉取仅 `HttpClient`。

**关键决策落地**:
- [x] D1 zip-only:`DownloadAndExtract` 用 `HttpClient` 下 `github.com/{o}/{r}/archive/{ref}.zip`。
- [x] D2 默认 ref:`refOrBranch==null` → `["main","master"]` 依次试。
- [x] D3 同步+进度条:`DisplayProgressBar` + `finally ClearProgressBar`。
- [x] D4 冲突预检:id 目录存在→ID_CONFLICT;命令 ∩ CommandRegistry→COMMAND_CONFLICT;均在拷贝**前**。
- [x] D5 enabled 仅记录:meta 写 `enabled=true`;`LocalRegistry` 读出;无切换逻辑。
- [x] D6 复用注册表:`CommandRegistry.Commands` 只读(预检 + Compiled)。
- [x] D7 独立目录:代码全在 `Unity/Editor/Extensions/`(非 Commands/)。

**流程级约束核对**:
- [x] 安装目标限 `Assets/AgentBridgeExtensions/`(`InstallRoot` 常量);id 格式 `^[a-z0-9-]+$` 校验;`ParseGitHub` 拒非 GitHub。
- [x] 冲突预检前置;`CopyDir` 失败 `TryDelete(destRel)` 清半截;`IO_FAILED`。
- [x] 注册解耦:安装只 `Refresh`,不碰分发(命令靠 file-bridge 重扫注册)。
- [x] 卸载幂等:目录不存在返回 false 不抛。

**挂载点反向核对(可卸载性)**——对照第 2.3 节:
- [x] 挂载点(菜单窗口 / Installer API / LocalRegistry API):grep 确认菜单项 1 处(`Tools/AgentBridge/Extensions`),API 类各 1。
- [x] **反向核查**:`Extensions/` 所有类型仅被本目录互引 + 只读 file-bridge `CommandRegistry`;无外部代码引用扩展子系统(grep 无 file-bridge 反向依赖)。
- [x] **拔除沙盘推演**:删 `Unity/Editor/Extensions/` → 菜单项消失、API 没了、file-bridge 完全不受影响(单向只读依赖);已装扩展目录 `Assets/AgentBridgeExtensions/` 是用户数据,卸载逻辑随之消失需手删(已在 2.3 注明)。无残留耦合。

## 3. 验收场景核对

对照方案第 3 节。**运行时证据 = 代码评审(真机经用户决定跳过)**:

- [~] **S1 安装成功**:下载→拷 Assets→meta→Refresh→Ok。代码路径完整。**真机未测**。
- [~] **S2 manifest 缺失** → `ReadManifest` 抛 MANIFEST_MISSING。**真机未测**。
- [~] **S3 manifest 非法**(id 格式/缺 name/version)→ `Validate` 抛 MANIFEST_INVALID。**真机未测**。
- [~] **S4 id 冲突** → `Directory.Exists(destRel)` → ID_CONFLICT,不覆盖。**真机未测**。
- [~] **S5 命令冲突** → `manifest.Commands ∩ CommandRegistry.Commands` → COMMAND_CONFLICT。**真机未测**。
- [~] **S6 下载失败** → 两 ref 都失败 → DOWNLOAD_FAILED。**真机未测**。
- [~] **S7 卸载** → DeleteAsset+Refresh→true;不存在→false。**真机未测**。
- [~] **S8 本地扫描** → `Scan` 读 manifest+meta,Compiled 交叉注册表。**真机未测**。
- [~] **S9 窗口闭环** → IMGUI 安装/列表/卸载齐备。**真机未测**(EditorWindow,需 Unity 渲染)。
- [~] **S10 跨 roadmap 集成** → 安装+重编译后命令经 file-bridge 注册。**真机未测**(依赖 Unity 重编译 + 示例扩展)。

说明:S1–S10 全标 `[~]`(代码完成、真机未测,用户同意跳过)。代码层可静态确证项(错误码落点、范围守护、契约一致)已在第 1/2/4 节勾选。这是 EditorWindow + 网络 + 跨 roadmap 集成的 feature,运行时证据**强依赖宿主工程真机跑**,本轮缺位明确记入第 9 节遗留。

## 4. 术语一致性

对照方案第 0 节 grep:
- 类型名 `ExtensionManifest`/`InstalledMeta`/`ExtensionInstaller`/`LocalRegistry`/`InstalledExtension`/`ExtensionManagerWindow`/`ExtensionErrorCodes` 与 design 一致 ✓
- 安装目录常量 `Assets/AgentBridgeExtensions` 与 roadmap §4.2 一致 ✓
- 防冲突:grep 无既有同名 ✓
- 复用 `CommandRegistry` 名一致、只读 ✓

无不一致。

## 5. 架构归并

对照方案第 4 节,已**实际写入** `.codestable/architecture/ARCHITECTURE.md`:

- [x] **新子系统**:§3 新增"扩展管理子系统(`Unity/Editor/Extensions/`)"小节——模块清单 + 安装流程 + "命令注册仍归 M3"说明 ✓
- [x] **流程级约束归并**:§5 新增"扩展安装(extension-manager)纪律"约束条(装入路径限制 + 冲突预检 + 注册解耦 + zip-only)✓
- [x] 顺带修正 §3 M5 行残留的"csharp 为后续 feature"(已 drop)✓
- [x] 头部"最近更新"→ 2026-06-25(ext-core 验收归并)✓

判据自检:未读 design 的人打开 ARCHITECTURE.md 现可知"系统有扩展管理子系统、扩展是源码包+manifest、装进 Assets 由 file-bridge 反射接管、安装受路径/冲突/zip-only 约束"。归并到位。

## 6. requirement 回写

方案 frontmatter 原 `requirement: null`,extension-manager 无对应 req。

- [x] 空 + 新增用户可感能力 → **已 backfill**:新建 `requirements/extension-management.md`(`status: current`),含用户故事/为什么/怎么解决/边界/变更日志;design frontmatter 改 `requirement: extension-management`;roadmap `related_requirements: [extension-management]`。yaml 校验通过。

## 7. roadmap 回写

方案 frontmatter `roadmap: extension-manager` / `roadmap_item: ext-core`,均有值:

- [x] `extension-manager-items.yaml`:`ext-core` `in-progress` → `done`(feature 核对一致),校验通过。
- [x] 主文档 §5 第 1 条同步:标 ✅ done、状态填实、对应 feature、依赖注明均 done、描述改 GitHub zip。

## 8. attention.md 候选盘点

- [x] **有候选**(仅登记,不擅自写):
  - 候选 1:**ext 系列真机测试需要含 `Assets/` 的宿主 Unity 工程**——本仓库 `Unity/` 是纯 UPM 包,无 `Assets/`;扩展安装目标 `Assets/AgentBridgeExtensions/` 在包仓库里不存在。后续 ext-* feature(enable-disable/remote-index/manager-ui)真机验证都会撞。建议放 attention.md「运行与本地起服务」节。

## 9. 遗留

- **真机验证整体缺位(重)**:ext-core 是本批技术最重的 feature(网络下载 + zip 解压 + EditorWindow + 跨 roadmap 命令注册),S1–S10 全靠代码评审、零真机。**强烈建议**在宿主工程里实跑一轮(尤其 HttpClient 同步下载在 Unity 主线程的行为、ZipFile 可用性、Refresh 后命令注册链路)再上生产。
- 已知限制:`DownloadAndExtract` 同步 `HttpClient.GetByteArrayAsync().GetAwaiter().GetResult()` 阻塞编辑器主线程;大仓库/慢网会卡住编辑器(v1 取舍,见 design D3)。
- 已知限制:默认 ref 仅试 `main`/`master`;非这两个默认分支名的仓库需显式传 ref。
- 已知限制:`InstallResult.Ok` 只表示源码落盘+Refresh 触发,不保证扩展源码编译无错(用户源码错由 Unity Console 报)。
- 实现阶段无"顺手发现"。
