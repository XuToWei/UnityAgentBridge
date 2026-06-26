---
doc_type: refactor-design
refactor: 2026-06-25-ext-remote-cleanup
status: approved
scope: Unity/Editor/Extensions/ —— 移除 ext-core 远程安装(纯本地重订后作废)
summary: 删 InstallFromGitHub+InstallResult+ExtensionErrorCodes+窗口粘URL控件;留 Uninstall/scan/启停/模型
---

# ext-remote-cleanup refactor design

## 1. 本次范围
- scan 全 4 条(R1 瘦身 ExtensionInstaller / R2 删 InstallResult / R3 删 ExtensionErrorCodes / R4 窗口去安装控件)。
- 明确不做:ExtensionManifest/InstalledMeta/InstalledExtension/LocalRegistry/ExtensionState/Bootstrap 不动;manifest.repo 不动;不碰 file-bridge/assets。
- 工作量:小;风险:低(纯删除,引用已 grep 闭合)。
- **非行为等价**:移除"从 URL 安装"行为——已由纯本地重订拍板,本次仅代码对齐(见 scan 性质说明)。

## 2. 前置依赖
- 无需补测试(无测试基建;验证靠编译 + grep 无残留 + 真机由用户决定跳过)。
- 调用方已搜:`InstallFromGitHub`/`InstallResult`/`ExtensionErrorCodes` 引用全部落在待删/待改文件内(grep 已确认)。

## 3. 执行顺序

- 步骤 1:R4 先去窗口安装控件(切断对 InstallFromGitHub 的唯一调用方)
  - 操作:`ExtensionManagerWindow.cs` 删 `_url` + 顶部安装 UI 块 + 类注释相应句;保留 _lastMessage/列表/启停/卸载
  - 退出信号:grep 窗口无 `InstallFromGitHub`/`_url`;窗口仍有启停+卸载
  - 验证:AI 自证(grep + 通读)
  - 回滚:git revert 该步

- 步骤 2:R1 瘦身 ExtensionInstaller 为 InstallRoot + Uninstall
  - 操作:删 InstallFromGitHub + 全部远程私有方法 + InstallException + 远程 usings;留 InstallRoot/Uninstall
  - 退出信号:grep 文件内无 InstallFromGitHub/HttpClient/ZipFile/InstallResult/ExtensionErrorCodes/InstallException
  - 验证:AI 自证
  - 回滚:git revert 该步

- 步骤 3:R2+R3 删 InstallResult.cs 与 ExtensionErrorCodes.cs(+ .meta)
  - 操作:删两文件及其 .meta
  - 退出信号:grep 全 Extensions/ 无 `InstallResult`/`ExtensionErrorCodes` 残留引用
  - 验证:AI 自证(全局 grep)
  - 回滚:git revert 该步

## 4. 风险与看点
- 顺序关键:先断调用方(步1)再删被调(步2/3),避免中间态编译错。
- 唯一"行为"看点:窗口不再有安装入口——预期内(纯本地)。
- 真机编译验证缺位(用户跳过),靠 grep 无残留 + 引用闭合性自证。
