---
doc_type: decision
category: convention
date: 2026-06-25
slug: commands-category-subdirectory
status: active
area: file-bridge, command-handlers
tags: [convention, directory-layout, commands, handlers, m5]
---

## 背景

M5 内置命令集随子 feature 不断增多:`bridge-core`(ping)、`cmd-introspection`(list_commands)、`cmd-inspection`(4 只读查询)、`cmd-mutation`(4 写操作),后续还有 `cmd-assets`、`cmd-csharp`。命令以反射自动注册(`[Command]` + `ICommandHandler`),物理目录怎么组织不影响注册结果,但影响**人/AI 定位代码**和**feature 边界对齐**。若所有 handler 平铺在 `Editor/Commands/`,会很快超过文件数舒适阈值(实践约 8),且看不出哪个 handler 属于哪个能力域。

## 决定

**按命令类别给 handler 分子目录:`Editor/Commands/{Category}/`**。一类命令一个子目录,目录名用能力域 PascalCase(已落地:`Inspection/`、`Mutation/`;后续:`Assets/`、`Csharp`/`Scripting/` 等)。

配套约定:

1. **内置元命令留 `Commands/` 根**:`ping`、`list_commands` 这类不属任何能力类别、数量少的命令不进子目录。
2. **跨 feature 共享的对象基础设施放 `Editor/Scene/`**(非 `Commands/` 下):`ObjectRef`/`ComponentRef`/`SceneObjectResolver`/`PropertySerializer`/`PropertyDeserializer`/`RefErrorCodes`/`MutationErrorCodes` 等被多个命令域复用的类型,不归任一命令子目录。
3. **子目录 ≈ feature 边界**:一个 `cmd-*` feature 的 handler 基本落在同名子目录,使 feature 可干净卸载(删子目录即拔除该能力域命令)。

## 理由

- **可定位**:按能力域分组,找"查询类命令"直接进 `Inspection/`,不必在平铺列表里扫描。
- **控膨胀**:`Commands/` 根只保留少量元命令,各域命令各自成目录,单目录文件数可控。
- **边界对齐**:子目录与 feature 一一对应,验收阶段的"挂载点反向核对 / 拔除沙盘推演"有清晰物理边界。
- **零成本**:反射注册不依赖目录结构,纯组织约定,不引入任何运行时代价。

## 考虑过的替代方案

- **全平铺在 `Commands/`**:否——命令数已 10+ 且持续增长,平铺很快超阈值、能力域不可见。
- **靠命令名前缀分组不分目录**(如 `get_*`/`set_*`):否——命名前缀不提供物理边界,无法支撑"删目录即卸载"的边界核对。

## 影响 / 约束

- 后续命令 feature(`cmd-assets`、`cmd-csharp` 等)**必须**遵守:新建 `Commands/{Category}/` 子目录放本域 handler,共享基础设施放 `Editor/Scene/`。
- 新增跨域共享类型时优先考虑 `Editor/Scene/`(或未来更合适的共享目录),不要塞进某个命令子目录造成隐性耦合。
- 本约定是软规约(convention),不是强制编译约束;偏离不会报错,但会削弱可定位性与边界清晰度,review 时应纠正。

## 相关文档

- architecture `ARCHITECTURE.md` §3 M5 内置命令集。
- feature `cmd-inspection`(首次按类别建 `Inspection/`)、`cmd-mutation`(`Mutation/`,本约定第二次落地后归档)。
- decision `command-discovery-mechanism`(命令注册/发现机制,与本目录约定正交)。
