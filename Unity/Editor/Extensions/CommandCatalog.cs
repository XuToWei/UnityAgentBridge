using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;

namespace AgentBridge
{
    /// <summary>
    /// 命令目录(命令管理器 EM1)。用 Unity TypeCache 列出所有 ICommandHandler(内置+扩展),
    /// 实例化后取 Command/Description + Type.Assembly 来源,交叉全局禁用名单(启停态)。
    /// 用 TypeCache 而非 CommandRegistry.GetAll()——后者已过滤禁用项,目录需含禁用命令才能在窗口再启用。
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
