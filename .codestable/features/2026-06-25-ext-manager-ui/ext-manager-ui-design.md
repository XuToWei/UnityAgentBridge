---
doc_type: feature-design
feature: 2026-06-25-ext-manager-ui
requirement: extension-management
roadmap: extension-manager
roadmap_item: ext-manager-ui
status: approved
summary: 扩展管理窗口打磨——概览头 + 状态/名称过滤 + ScrollView + 打开目录按钮 + 未生效警示;纯 UI 单文件
tags: [unity, agent, extension, editor-window, ui]
---

# ext-manager-ui design

## 0. 术语约定

| 术语 | 定义 | 防冲突 |
|---|---|---|
| 概览头 | 窗口顶部一行汇总:扩展数 / 已启用 / 已禁用 / 命令总数 | 全新(UI 局部) |
| 状态过滤 | 按启停态筛列表:全部 / 已启用 / 已禁用 | 全新 |
| 名称过滤 | 按 id/name 子串筛列表(本地,非远程搜索) | 与已 drop 的远程搜索区分 |
| 未生效警示 | `Compiled=false`(源码未编译/有错)时的高亮提示 | 全新 |

grep:本 feature 不引入跨文件新类型,仅在 `ExtensionManagerWindow` 内加私有字段/方法。

## 1. 决策与约束

### 需求摘要
- **做什么**:在已交付的扩展管理窗口上加打磨,提升多扩展时的可用性与可诊断性。
- **为谁**:用扩展管理窗口的开发者。
- **成功标准**:窗口能 ① 顶部显示概览计数 ② 按状态(全部/启用/禁用)+ 名称子串过滤列表 ③ 列表超长可滚动不溢出 ④ 每扩展一键在文件管理器打开其目录 ⑤ `Compiled=false` 的扩展醒目提示原因。
- **明确不做**:
  - 不加安装入口(纯本地重订已移除;扩展用户自放)。
  - 不做远程索引/搜索(`ext-remote-index` 已 drop;名称过滤仅过滤**本地已扫描**列表,不联网)。
  - 不改启停/卸载/扫描的既有逻辑(ext-core/ext-enable-disable 已交付,本 feature 只动呈现层)。
  - 不改 file-bridge、不改任何契约/数据模型。
  - 不做扩展内容编辑(改 manifest/源码)。

### 复杂度档位
走默认档位。纯 EditorWindow IMGUI 呈现层。

### 关键决策(候选打磨,review 时可逐条砍)
- **D1 概览头**:顶部一行 `扩展 N · 启用 E · 禁用 D · 命令 C`(从 `_installed` 现算,无新数据源)。
- **D2 状态过滤**:工具栏三选一(全部/已启用/已禁用),按 `ext.Enabled` 筛。
- **D3 名称过滤**:一个搜索框,按 `id`/`name` 子串(忽略大小写)筛**本地列表**;空则不筛。
- **D4 ScrollView**:列表整体包进 `EditorGUILayout.BeginScrollView`,多扩展不溢出窗口。
- **D5 打开目录**:每扩展行加按钮 → `EditorUtility.RevealInFinder(Assets/AgentBridgeExtensions/{id})`。
- **D6 未生效警示**:`Compiled=false` 时该行用 `HelpBox(Warning)` 提示"源码未编译或编译有错,查看 Console";区别于"已禁用"(Enabled=false 是用户主动,不是错误)。

### 前置依赖
ext-core + ext-enable-disable(均 done)。`ext-remote-index` 已 drop,非依赖。

## 2. 名词与编排

### 2.1 名词层

**现状**(`Extensions/ExtensionManagerWindow.cs`):
- 字段:`_lastMessage` / `_installed`(`List<InstalledExtension>`)/ `_expanded`(逐命令展开集)。
- `OnGUI`:顶部 HelpBox 提示 → "已安装" + Rescan → 空态 → `foreach` 每扩展一个 box(foldout + 生效标 + 扩展级启停 + 卸载 + 展开后逐命令启停)。
- `InstalledExtension` 已带 `Id/Name/Version/Enabled/Commands/DisabledCommands/Compiled`——**打磨所需数据全有**,无需改模型/Scan。

**变化**(全部在 `ExtensionManagerWindow` 内,私有):

| 名词 | 角色 |
|---|---|
| `_statusFilter`(enum: All/Enabled/Disabled) | D2 状态过滤态 |
| `_nameFilter`(string) | D3 名称过滤态 |
| `_scroll`(Vector2) | D4 滚动位置 |
| 概览头渲染 | D1,从 `_installed` 现算计数 |
| `Filtered()` 私有方法 | 按 `_statusFilter`+`_nameFilter` 过滤 `_installed` |
| 行内"打开目录"按钮 | D5 `RevealInFinder` |
| 行内未生效 HelpBox | D6 |

**接口示例**(UI 行为,非 API):
```
概览头:  扩展 3 · 启用 2 · 禁用 1 · 命令 7
工具栏:  [全部|已启用|已禁用]   搜索:[ rock____ ]
列表(ScrollView):
  ▸ My Ext (my-ext) v1.0.0      已生效   [Disable][Open][Uninstall]
  ▸ Broken (broken) v0.1.0      ⚠未生效  [Disable][Open][Uninstall]
      └ HelpBox: 源码未编译或编译有错,查看 Console
```

### 2.2 编排层

无跨模块流程,单一 `OnGUI` 渲染管线。结构:
```
OnGUI → 概览头(算计数) → 工具栏(状态+名称过滤 + Rescan) → ScrollView{ foreach Filtered(): 扩展行(+未生效警示) + 展开逐命令 } 
```
(模块单一、调用线性,不画 mermaid。)

**现状 → 变化**:现 `OnGUI` 直接 `foreach _installed`;变为 `foreach Filtered()`,外包 ScrollView,前加概览头+工具栏,行内加 Open 按钮 + 未生效 HelpBox。**启停/卸载/foldout/逐命令逻辑原样保留**,只是被过滤+滚动包裹。

**流程级约束**:
- **纯呈现**:不改 `LocalRegistry.Scan`/`ExtensionState`/`ExtensionInstaller` 任何调用语义;过滤只影响"显示哪些行",不影响数据。
- **过滤不持久**:`_statusFilter`/`_nameFilter` 是窗口实例态,不写盘(关窗即忘),与 `meta.enabled`(持久)区分。
- **计数实时**:概览头每帧从 `_installed` 现算,Rescan/启停后自动反映。
- **未生效 ≠ 已禁用**:`Compiled`(编译态)与 `Enabled`(用户启停)正交,警示只针对前者。

### 2.3 挂载点清单

| 挂载位置 | 文件 | 动作 |
|---|---|---|
| 概览头 + 工具栏(状态/名称过滤)+ ScrollView | `ExtensionManagerWindow.OnGUI` | 改 |
| 行内"打开目录"按钮 | `ExtensionManagerWindow`(行渲染) | 改 |
| 行内未生效警示 | `ExtensionManagerWindow`(行渲染) | 改 |

全部落在 `ExtensionManagerWindow` 单文件内。**拔除**:把这些私有字段/分支删掉,窗口回到 ext-enable-disable 后的"列表+启停+卸载"状态。无外部牵连。

### 2.4 推进策略
```
1. 概览头 + 工具栏过滤:加 _statusFilter/_nameFilter/Filtered();顶部渲染计数 + 状态切换 + 搜索框;列表改 foreach Filtered()
   退出:窗口顶部出计数;切状态/输入名称列表实时筛
2. ScrollView + 行内打开目录 + 未生效警示:列表包 ScrollView;每行加 Open(RevealInFinder)按钮;Compiled=false 行加 HelpBox
   退出:多扩展可滚动;Open 打开对应目录;未生效行有醒目提示
```

### 2.5 结构健康度与微重构

##### 评估
- compound:`commands-category-subdirectory` 不适用(非 Commands/)。无其它命中。
- 文件级:`ExtensionManagerWindow.cs`(现 ~95 行)加打磨后约 ~150 行。仍是单一 EditorWindow,职责单一(扩展管理呈现)。行渲染可抽 `DrawExtensionRow` 私有方法保持 OnGUI 可读——属 implement 自决的函数拆分,非跨文件微重构。
- 目录级:不新增文件。

##### 结论:不做(微重构)
单文件、单职责、改动量小;行渲染若偏长,implement 内部抽私有方法即可,无需独立微重构步。

##### 超出范围的观察
无。

## 3. 验收契约

### 关键场景清单
1. **概览头**:有 N 个扩展(E 启用/D 禁用、命令共 C)时,顶部显示 `扩展 N · 启用 E · 禁用 D · 命令 C`,启停/Rescan 后实时更新。
2. **状态过滤**:选"已禁用"→ 仅列 `Enabled=false` 的扩展;"已启用"→ 仅 `Enabled=true`;"全部"→ 全列。
3. **名称过滤**:输入子串 → 仅列 id/name 含该子串(忽略大小写)的扩展;清空 → 全列。状态+名称过滤叠加生效。
4. **ScrollView**:扩展数多到超窗口高度时,列表可滚动,概览头/工具栏不被推走。
5. **打开目录**:点某扩展 Open → 文件管理器打开 `Assets/AgentBridgeExtensions/{id}`。
6. **未生效警示**:`Compiled=false` 的扩展行有 Warning 提示("源码未编译或编译有错…");`Compiled=true` 无此提示。
7. **既有功能不回归**:启停(扩展级/命令级)、卸载、Rescan、foldout 展开 在过滤/滚动后仍正常工作。

### 明确不做的反向核对项
- 无安装入口(grep 窗口无 `InstallFromGitHub`/安装控件——refactor 已删,本 feature 不恢复)。
- 名称过滤不联网(只过滤 `_installed` 内存列表,无 `HttpClient`/远程调用)。
- 未改 `LocalRegistry`/`ExtensionState`/`ExtensionInstaller`/数据模型/file-bridge(grep 本 feature 改动仅 `ExtensionManagerWindow.cs`)。
- 过滤态不写盘(无 meta/EditorPrefs 写入)。

## 4. 与项目级架构文档的关系

acceptance 提炼回 `architecture/ARCHITECTURE.md`:
- 扩展子系统小节的 `ExtensionManagerWindow` 描述补一句"含概览/过滤/滚动/打开目录/未生效警示"(轻量,UI 细节不展开)。
- 无新名词/契约/约束——纯呈现层,大概率仅一句话补充或"无架构维度变更"。
- requirement `extension-management`:窗口打磨不改用户视角愿景(req 已含"在窗口里看到装了哪些"),acceptance 时大概率"未变"。

关联:roadmap `extension-manager` EM5;依赖 feature `ext-core`/`ext-enable-disable`;无 decision 牵涉。
