---
doc_type: roadmap
slug: extension-manager
status: completed
created: 2026-06-24
last_reviewed: 2026-06-25
tags: [unity, agent, extension, command-manager, editor-ui, local]
related_requirements: [extension-management]
related_architecture: []
---

# UnityAgentBridge 命令管理器:浏览 / 启停所有命令 + 管理本地扩展

## 1. 背景

在 file-bridge 的 handler 框架之上加一层**命令管理器**:用一个 Editor 窗口**统一浏览所有命令**(内置 + 扩展),**任意命令可隐藏式启停**(从 `list_commands` 隐藏 + dispatch 拒调,不重编译不删码),扩展来源的命令还可**卸载**整个扩展包。

命令通过 Unity `TypeCache.GetTypesDerivedFrom<ICommandHandler>()` 发现(Unity 维护的类型快表),按**来源程序集**区分内置(`AgentBridge.Editor`)与扩展(扩展自带的 asmdef)。扩展仍是放在 `Assets/AgentBridgeExtensions/{id}/` 的本地源码包,但其命令的"发现"不再靠文件夹扫描——文件夹/manifest 退化为只服务"卸载"与"友好归属"。

> **2026-06-25 第三次方向性重订**:由"本地扩展包管理"改为"命令管理器"(用户决定用 TypeCache 列所有 ICommandHandler 显示)。理由见 §8。前两次:远程→纯本地→命令管理器。

> 跨 roadmap:复用 file-bridge M3 的反射注册 + `command-discovery-mechanism` 的 `list_commands`/`commandsVersion`,以及 `ext-enable-disable` 已落地的 file-bridge 禁用名单过滤(`CommandRegistry.SetDisabledCommands`/`IsDisabled` + `COMMAND_DISABLED`)。本 roadmap 只换"禁用名单的真相源",不改 file-bridge 过滤本身。

## 2. 范围与明确不做

### 本 roadmap 覆盖
- 命令发现:`TypeCache` 列所有 `ICommandHandler`,带 `[Command]` 名/描述 + 来源程序集
- 来源归属:内置(`AgentBridge.Editor`)/ 扩展(其 asmdef 程序集,折射到 `Assets/AgentBridgeExtensions/{id}` 折射友好名)/ 其它
- 隐藏式启停:**任意命令**(含内置)从 `list_commands` 隐藏 + dispatch 拒调,不重编译不删码
- **全局禁用名单**:跨内置/扩展的单一持久化(内置命令无 meta,必须全局存),domain reload 重应用
- 扩展卸载:删 `Assets/AgentBridgeExtensions/{id}/` 目录 + Refresh
- Editor 命令管理器窗口:按来源分组列所有命令,每命令启停,扩展项卸载

### 明确不做
- **远程获取/安装**——扩展由用户自放(承自上次纯本地重订)
- **策展索引/搜索/市场**
- 比"命令"更细的启停粒度
- 扩展间依赖解析 / 自动更新 / 签名审查
- 运行时(非编辑器)

## 3. 模块拆分(概设)

```
extension-manager(命令管理器,依赖 file-bridge M3)
├── EM1 命令目录(Catalog)  TypeCache 列所有 ICommandHandler → 命令名/描述/来源程序集/schema;来源=Type.Assembly
├── EM2 全局禁用名单         EditorPrefs(工程标识 key)存禁用命令名集合 + 启停 API + domain reload Reapply → file-bridge SetDisabledCommands
├── EM3 扩展归属与卸载       扫 Assets/AgentBridgeExtensions/* 读 manifest,把命令折射到扩展 id;卸载删目录
└── EM4 命令管理器窗口       按来源(内置/各扩展/其它)分组列命令,逐命令启停,扩展项卸载
```

### EM1 · 命令目录(Catalog)
- **职责**:`TypeCache.GetTypesDerivedFrom<ICommandHandler>()` → 每个 handler 取 `[Command]` 名/描述 + `Type.Assembly` 来源 + schema(实例化或复用 CommandRegistry)。产出统一命令条目列表。
- **承载子 feature**:`command-catalog`
- **触碰现有代码**:新增;可复用 `CommandRegistry` 的已注册信息

### EM2 · 全局禁用名单
- **职责**:EditorPrefs(工程标识 key)单一持久化所有被禁命令名(内置+扩展共用),提供 `SetEnabled(command,bool)`;`Reapply()` 读存储推给 file-bridge;`[InitializeOnLoad]` 域重载重应用。**取代** ext-enable-disable 的 per-extension `meta.enabled/disabledCommands`。
- **承载子 feature**:`command-catalog`(与 EM1 同 feature 落地数据/逻辑层)
- **触碰现有代码**:复用 file-bridge `CommandRegistry.SetDisabledCommands`(过滤不变);废弃 `ExtensionState` 的 meta 持久化

### EM3 · 扩展归属与卸载
- **职责**:扫 `Assets/AgentBridgeExtensions/*` 读 manifest,把命令(`manifest.commands`)折射到扩展 id(供窗口按扩展分组 + 卸载);卸载删目录 + Refresh。
- **承载子 feature**:`command-manager-window`(归属/卸载随窗口);复用 ext-core 的 `LocalRegistry`/`ExtensionInstaller.Uninstall`
- **触碰现有代码**:复用 ext-core 既有扫描/卸载

### EM4 · 命令管理器窗口
- **职责**:按来源分组(内置 / 各扩展 / 其它)列所有命令,显示启停态 + 描述,逐命令 Enable/Disable,扩展项 Uninstall。
- **承载子 feature**:`command-manager-window`
- **触碰现有代码**:重写 `ExtensionManagerWindow`

## 4. 模块间接口契约 / 共享协议(架构层详设)

### 4.1 命令目录(EM1)
```csharp
public sealed class CommandEntry {
    public string Name;          // [Command] 名
    public string Description;
    public string Assembly;      // Type.Assembly 名:AgentBridge.Editor=内置,其它=扩展程序集
    public bool   IsBuiltin;     // Assembly == "AgentBridge.Editor"
    public string ExtensionId;   // 折射到的扩展 id(EM3 归属;无则 null)
    public bool   Enabled;       // 不在全局禁用名单
}
public static class CommandCatalog {
    static List<CommandEntry> All(); // TypeCache 列举 + 交叉禁用名单 + 交叉扩展归属
}
```
来源:`TypeCache.GetTypesDerivedFrom<ICommandHandler>()`;每类型取 `GetCustomAttribute<CommandAttribute>()` 名/描述、`type.Assembly.GetName().Name` 来源。

### 4.2 全局禁用名单(EM2)
```csharp
public static class CommandToggle {
    static void SetEnabled(string command, bool enabled); // 改全局存储 + Reapply
    static void Reapply();                                // 读存储 → CommandRegistry.SetDisabledCommands
}
```
- **存储位置**:**EditorPrefs**,key 带工程标识(如 `"AgentBridge.DisabledCommands." + Application.dataPath`)避免跨工程串;值为命令名分隔串。**不再用 per-extension meta、不用 json 文件**。
- domain reload 后 `[InitializeOnLoad]` 调 `Reapply()` 重建。

### 4.3 与 file-bridge 衔接(沿用 ext-enable-disable,不变)
- `CommandRegistry.SetDisabledCommands(names)`/`IsDisabled`、`GetAll()`/`Version` 按可见集、dispatch `COMMAND_DISABLED` —— **已落地,本次不改**;只是把"names 从哪来"由 per-ext meta 换成 4.2 全局存储。

### 4.4 扩展归属与卸载(EM3,复用 ext-core)
- `LocalRegistry.Scan()`(ext-core 既有)读 manifest → 命令名→扩展 id 折射表。
- `ExtensionInstaller.Uninstall(id)`(ext-core 既有)删目录 + Refresh。
- manifest(4.1 旧契约)保留 `id/name/version/commands/sourceDir`;`commands` 用于命令→扩展折射。
- **扩展须自带 Editor asmdef**(才能引用 `AgentBridge.Editor`)——其 assembly 名即该扩展命令的来源标识。

## 5. 子 feature 清单

> 前序 feature(ext-core / ext-enable-disable / ext-manager-ui)交付的代码部分复用、部分返工——见各条备注与 §7 存量影响。

1. **ext-core** — extension.json 清单 + 本地扫描 + 卸载 + 最小窗口 ✅ done(部分复用)
   - 状态:done(2026-06-25);**命令管理器模型下:LocalRegistry/Uninstall/manifest 复用(降级为归属+卸载),最小窗口被 command-manager-window 重写**
   - 对应 feature:2026-06-25-ext-core
   - 备注:远程安装代码已 cs-refactor 清理;文件夹扫描不再用于"命令发现"(改 TypeCache)

2. **ext-enable-disable** — 隐藏式启停 + file-bridge 禁用过滤 ✅ done(部分复用 / 部分返工)
   - 状态:done(2026-06-25);**file-bridge 禁用名单过滤(CommandRegistry/COMMAND_DISABLED)复用;但 per-extension meta(enabled/disabledCommands)持久化模型作废,改全局禁用名单(见 command-catalog)**
   - 对应 feature:2026-06-25-ext-enable-disable
   - 备注:`ExtensionState`(meta 持久化)+ `ExtensionStateBootstrap` 将被 command-catalog 的全局存储版取代

3. **ext-remote-index** — 远程索引 ❌ dropped(2026-06-25,纯本地重订时移除)

4. **ext-manager-ui** — 扩展包窗口打磨 ❌ dropped(superseded)
   - 状态:dropped(2026-06-25,验收前被命令管理器重订取代)
   - 对应 feature:2026-06-25-ext-manager-ui(已 impl 未 accept)
   - 移除理由:窗口由"扩展包列表"改"命令管理器",其打磨(概览/过滤/滚动/打开目录/未生效警示)并入 command-manager-window 重做

5. **command-catalog** — 命令管理器完整落地(TypeCache 目录 + 全局禁用名单 + Reapply + 命令管理器窗口)✅ done
   - 所属模块:EM1 + EM2 + EM3 + EM4(合并)
   - 依赖:ext-core / ext-enable-disable(done);复用 file-bridge 过滤
   - 状态:done(2026-06-26,2026-06-25-command-catalog 验收;真机经用户决定跳过)
   - 对应 feature:2026-06-25-command-catalog
   - 备注:`CommandCatalog.All()`(TypeCache 列举+归属+启停态)+ `CommandToggle`(全局 EditorPrefs 名单)+ [InitializeOnLoad] Reapply + 窗口重写(`Tools/AgentBridge/Commands`);**合并了 command-manager-window**(引擎切换与窗口改写耦合);废 ExtensionState/per-ext meta 启停

6. **command-manager-window** — 统一命令管理器窗口 ❌ dropped(absorbed)
   - 状态:dropped(2026-06-26,并入 command-catalog)
   - 移除理由:删 ExtensionState 会断窗口、新旧禁用引擎不能共存,引擎切换与窗口改写无法拆分落地 → 合并进 command-catalog 一并实现
   - 备注:重写 ExtensionManagerWindow——按来源分组列所有命令、逐命令启停、扩展项卸载;并入 ext-manager-ui 的打磨(概览/过滤/滚动)

**最小闭环**:`command-catalog` 做完后,`CommandCatalog.All()` 能列出所有命令(内置+扩展)及启停态,`CommandToggle.SetEnabled` 能禁/启任意命令并经 file-bridge 在 `list_commands` 生效、domain reload 后保持。

## 6. 排期思路

`command-catalog`(数据+逻辑层)先行——它是窗口的数据源,且确立全局禁用名单替代 per-ext meta。`command-manager-window` 依赖它,后做(UI + 复用 ext-core 卸载/归属)。技术依赖外排序由你定。

## 7. 观察项

- **存量返工(重)**:本次是第 3 次重订,在**未提交**的前序代码上改。返工清单:① `ExtensionState`/`ExtensionStateBootstrap`(per-ext meta 持久化)→ 被 command-catalog 的全局存储取代;② `ExtensionManagerWindow`(ext-core+ext-manager-ui 建)→ command-manager-window 重写;③ `InstalledMeta.DisabledCommands`/`meta.enabled` 作为启停真相源作废(meta 可留作扩展元信息,但不再驱动启停)。复用:file-bridge 禁用过滤、LocalRegistry 扫描、ExtensionInstaller.Uninstall、manifest 模型。
- **来源→扩展映射**:命令按 `Type.Assembly` 分内置/扩展;映射到具体扩展 id 依赖"扩展自带 asmdef + manifest.commands 折射"。loose-script(无 asmdef)扩展无法按扩展分组,会落入"其它"组——command-manager-window design 细化。
- **全局禁用名单存储**:定为 EditorPrefs(key 带 `Application.dataPath` 等工程标识,避免跨工程串)。
- **ext-manager-ui 未验收**:其 impl 已完成未走 accept,本次 dropped(superseded);其代码改动随 command-manager-window 重写覆盖。
- req `extension-management` 文案(扩展管理视角)需在新窗口落地后按"命令管理器"刷新。
- **2026-06-26**:`InstalledMeta`/`.agentbridge-meta.json` 模型已删(命令管理器模型下无人读写,启停态归全局 EditorPrefs 名单)→ §4.2 布局块里的 `.agentbridge-meta.json` 描述已成 vestigial(代码无对应);如需扩展元信息再按需重建。

## 8. 变更日志

- **2026-06-25(第三次方向性重订:命令管理器)**:由"本地扩展包管理"改为"统一命令管理器"(用户决定用 TypeCache 列所有 ICommandHandler)。
  - 模型:命令发现改 `TypeCache`(列所有 ICommandHandler);启停真相源由 per-extension `meta` 改**全局禁用名单**(EditorPrefs,工程标识 key),覆盖内置+扩展;窗口由"扩展包列表"改"命令按来源分组 + 逐命令启停 + 扩展卸载"。
  - §1/§2/§3/§4 重写;新增子 feature `command-catalog`、`command-manager-window`;`ext-manager-ui` 标 dropped(superseded,验收前)。
  - 复用:file-bridge 禁用过滤(ext-enable-disable 落地)、LocalRegistry/Uninstall/manifest(ext-core)。返工:ExtensionState meta 持久化、ExtensionManagerWindow。
  - **存量风险**:第 3 次重订且全程未提交,无 baseline 回滚点(用户已知悉并选择继续)。
- **2026-06-25(启停粒度补充)**:启停补成两级粒度(已被本次全局名单模型覆盖)。
- **2026-06-25(方向性重订)**:去远程改纯本地。
- **2026-06-25(早)**:拉取窄化 zip-only(随纯本地失效)。
