using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;

namespace AgentBridge
{
    /// <summary>
    /// 命令目录。用 Unity TypeCache 列出所有 ICommandHandler,包含内置和扩展命令。
    /// 实例化后读取命令名、描述、分组和程序集来源,再合并当前启停状态。
    /// 不使用 CommandRegistry.GetAll(),因为注册表会过滤禁用命令,窗口需要显示禁用项以便重新启用。
    /// </summary>
    public static class CommandCatalog
    {
        public const string BuiltinAssembly = "AgentBridge.Editor";

        public static List<CommandEntry> All()
        {
            var disabled = new HashSet<string>(CommandToggle.Disabled());

            var entries = new List<CommandEntry>();
            foreach (var type in TypeCache.GetTypesDerivedFrom<ICommandHandler>())
            {
                if (type.IsAbstract || type.IsInterface)
                {
                    continue;
                }
                if (type.GetConstructor(Type.EmptyTypes) == null)
                {
                    continue;
                }
                var handler = (ICommandHandler)Activator.CreateInstance(type);
                var name = handler.Command;
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                var asm = type.Assembly.GetName().Name;
                entries.Add(new CommandEntry
                {
                    Name = name,
                    Description = handler.Description,
                    Group = handler.Group,
                    CanDisable = handler.CanDisable,
                    Assembly = asm,
                    IsBuiltin = asm == BuiltinAssembly,
                    Enabled = !disabled.Contains(name)
                });
            }
            return entries.OrderBy(e => e.Name).ToList();
        }
    }
}
