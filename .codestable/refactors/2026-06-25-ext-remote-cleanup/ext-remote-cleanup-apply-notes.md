---
doc_type: refactor-apply-notes
refactor: 2026-06-25-ext-remote-cleanup
---

# ext-remote-cleanup apply notes

## 步骤 1: 窗口去安装控件(R4)
- 完成时间: 2026-06-25
- 改动文件: Unity/Editor/Extensions/ExtensionManagerWindow.cs
- 动作: 删 `_url` 字段 + OnGUI 顶部"安装扩展(GitHub URL)"label/TextField/Install 块;类注释改为"纯本地无安装入口";顶部换成一行 HelpBox 提示用户把扩展放入 Assets 后 Rescan。
- 验证结果(AI grep): 窗口无 `_url`/`InstallFromGitHub`/`安装扩展`(exit 1);仍有 Rescan/SetEnabled/SetCommandEnabled/Uninstall。
- 偏离: 无。

## 步骤 2: 瘦身 ExtensionInstaller(R1)
- 完成时间: 2026-06-25
- 改动文件: Unity/Editor/Extensions/ExtensionInstaller.cs
- 动作: 整体重写为仅 `InstallRoot` 常量 + `Uninstall`;删 InstallFromGitHub + 全部远程私有方法 + InstallException + 远程 usings(只留 System.IO/UnityEditor)。
- 验证结果(AI grep): 文件内无 InstallFromGitHub/HttpClient/ZipFile/InstallResult/ExtensionErrorCodes/InstallException(exit 1);InstallRoot+Uninstall 仍在。
- 偏离: 无。

## 步骤 3: 删 InstallResult.cs / ExtensionErrorCodes.cs(R2+R3)
- 完成时间: 2026-06-25
- 改动文件: 删 Unity/Editor/Extensions/InstallResult.cs(+ .meta)、ExtensionErrorCodes.cs(+ .meta)
- 验证结果(AI grep): 全 Unity/Editor/ 无 InstallResult/ExtensionErrorCodes 残留(exit 1)。
- 偏离: 无。

## 收尾核对
- 远程符号清零(全 Unity/Editor):InstallFromGitHub/InstallResult/ExtensionErrorCodes/HttpClient/ZipFile/DownloadAndExtract/InstallException 均 grep 无命中 ✓
- 保留符号引用完整:ExtensionInstaller.InstallRoot/Uninstall、ExtensionManifest、InstalledMeta、LocalRegistry、ExtensionState、Bootstrap 均在且被引用 ✓
- 未夹带:本次仅动 Extensions/(ExtensionInstaller/ExtensionManagerWindow 改 + 2 文件删);file-bridge Dispatch 的改动是上一 feature ext-enable-disable 的,非本次 ✓
- 行为变更(预期内):窗口移除"从 URL 安装"入口——纯本地重订已批准。
- 真机编译验证:经用户决定跳过(沿用本会话节奏);以 grep 引用闭合性自证。
- Extensions/ 现 8 文件:ExtensionInstaller/ExtensionManagerWindow/ExtensionManifest/ExtensionState/ExtensionStateBootstrap/InstalledExtension/InstalledMeta/LocalRegistry。
