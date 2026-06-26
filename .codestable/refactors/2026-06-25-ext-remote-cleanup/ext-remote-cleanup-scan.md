# ext-remote-cleanup scan

> 扫描范围:`Unity/Editor/Extensions/`(ext-core 远程安装相关)
> 性质说明:这不是纯"行为等价"重构——它**移除一个当前能跑的行为**(从 GitHub URL 安装扩展)。但该行为已由 extension-manager **纯本地重订**(2026-06-25,roadmap/req/architecture 均已改)拍板移除;本次仅让代码追上已批准口径。属"已决定的范围收缩"的代码落地,经用户明确指示执行。

## 总览
- 发现 4 条删除项,1 文件改 + 2 文件删 + 1 文件改。
- 风险:低(grep 确认远程符号引用全部闭合在待删/待改文件内;保留符号 InstallRoot/Uninstall/ExtensionManifest 仍被引用)。
- 全部为删除,无逻辑重写。

## 清单

### R1 — ExtensionInstaller 瘦身为仅 Uninstall
- 文件:`Extensions/ExtensionInstaller.cs`
- 删:`InstallFromGitHub` + 私有 `DownloadAndExtract`/`ParseGitHub`/`SingleTopDir`/`ReadManifest`/`Parse`/`Validate`/`CopyDir`/`WriteMeta`/`TryDelete` + 内部类 `InstallException` + 远程 usings(System.IO.Compression/System.Net.Http/System.Text.RegularExpressions/System.Linq/Newtonsoft.Json)
- 留:`InstallRoot` 常量 + `Uninstall`(+ System.IO/UnityEditor using)
- 引用核对:`InstallFromGitHub` 仅窗口调(R3 一并删);`InstallRoot`/`Uninstall` 被 ExtensionState/LocalRegistry/窗口用,保留

### R2 — 删 InstallResult.cs
- 文件:`Extensions/InstallResult.cs`(+ .meta)
- 理由:仅 `InstallFromGitHub` 返回类型,删 install 后无引用
- 引用核对:grep 仅 ExtensionInstaller + 自身

### R3 — 删 ExtensionErrorCodes.cs
- 文件:`Extensions/ExtensionErrorCodes.cs`(+ .meta)
- 理由:6 码全为 install 流程错误(MANIFEST_*/ID_CONFLICT/COMMAND_CONFLICT/DOWNLOAD_FAILED/IO_FAILED),删 install 后无引用;Uninstall 返 bool 不抛
- 引用核对:grep 仅 ExtensionInstaller + InstallResult 注释

### R4 — 窗口移除"粘 URL 安装"控件
- 文件:`Extensions/ExtensionManagerWindow.cs`
- 删:`_url` 字段 + OnGUI 顶部"安装扩展(GitHub URL)"label/TextField/Install 按钮块
- 留:`_lastMessage`(卸载/启停复用)、`_installed`、`_expanded`、已装列表 + 启停 + 卸载
- 注:类注释里"粘 URL 安装控件作废"那句同步删/改

## 明确不做
- 不删/不改 `ExtensionManifest`(LocalRegistry 仍读)、`InstalledMeta`、`InstalledExtension`、`LocalRegistry`、`ExtensionState`、`ExtensionStateBootstrap`。
- 不改 manifest 的 `repo` 字段(roadmap 4.1 仍留作可空溯源)。
- 不顺手动 file-bridge / assets / 其它。
