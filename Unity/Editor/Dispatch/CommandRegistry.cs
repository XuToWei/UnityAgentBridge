using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;

namespace AgentBridge
{
    /// <summary>
    /// 命令注册表(M3)。反射扫描已加载程序集中实现 ICommandHandler 的具体类,实例化后按 Command 名建索引。
    /// 命令名重复 → 拒绝注册并记错误日志(不静默覆盖)。
    /// 这是 extension-manager 的对接点:扩展重编译后其 handler 在此自动出现。
    /// 同时收集每命令的描述/参数 schema(自描述),并算命令集内容 hash(Version)。
    /// 对应 file-bridge roadmap 4.3 / 4.7。
    /// </summary>
    public static class CommandRegistry
    {
        private static readonly Dictionary<string, ICommandHandler> s_Handlers =
            new Dictionary<string, ICommandHandler>(StringComparer.Ordinal);

        // 已注册命令的元数据,按命令名排序(决定 Version hash 的稳定性)。
        private static readonly List<CommandInfo> s_Infos = new List<CommandInfo>();

        // 被 extension-manager 禁用的命令名(隐藏式禁用):仍注册,但从 list_commands/Version 剔除、dispatch 拒调。
        // 进程内状态——domain reload 后由 extension-manager Reapply 重建(对应 extension-manager 4.4/4.6)。
        private static readonly HashSet<string> s_Disabled = new HashSet<string>(StringComparer.Ordinal);

        private static bool s_Built;
        private static string s_Version = "";

        /// <summary>当前已注册的命令名(只读快照)。</summary>
        public static IReadOnlyCollection<string> Commands
        {
            get
            {
                if (!s_Built)
                {
                    Rebuild();
                }
                return s_Handlers.Keys.ToArray();
            }
        }

        /// <summary>命令集内容 hash:对排序后命令元数据的 JSON 序列化算 MD5 取短前缀。同一命令集恒定。</summary>
        public static string Version
        {
            get
            {
                if (!s_Built)
                {
                    Rebuild();
                }
                return s_Version;
            }
        }

        /// <summary>可见(未禁用)命令的元数据(供 list_commands)。禁用命令从清单剔除。</summary>
        public static IReadOnlyList<CommandInfo> GetAll()
        {
            if (!s_Built)
            {
                Rebuild();
            }
            return VisibleInfos();
        }

        /// <summary>设置禁用命令名单(由 extension-manager 调用),并重算 Version。</summary>
        public static void SetDisabledCommands(IEnumerable<string> names)
        {
            if (!s_Built)
            {
                Rebuild();
            }
            s_Disabled.Clear();
            if (names != null)
            {
                foreach (var n in names)
                {
                    if (!string.IsNullOrEmpty(n))
                    {
                        s_Disabled.Add(n);
                    }
                }
            }
            s_Version = ComputeVersion(VisibleInfos());
        }

        /// <summary>某命令当前是否被禁用。</summary>
        public static bool IsDisabled(string command)
        {
            return s_Disabled.Contains(command);
        }

        // 可见 = 已注册且不在禁用名单(已排序,保持 Version 稳定)。
        private static List<CommandInfo> VisibleInfos()
        {
            return s_Infos.Where(i => !s_Disabled.Contains(i.Command)).ToList();
        }

        /// <summary>重新扫描并注册所有 handler。domain reload 后静态状态重置,首次查询时自动重建。</summary>
        public static void Rebuild()
        {
            s_Handlers.Clear();
            s_Infos.Clear();

            foreach (var type in TypeFinder.AllTypes())
            {
                if (type.IsAbstract || type.IsInterface)
                {
                    continue;
                }
                if (!typeof(ICommandHandler).IsAssignableFrom(type))
                {
                    continue;
                }
                if (type.GetConstructor(Type.EmptyTypes) == null)
                {
                    Debug.LogError($"[AgentBridge] {type.FullName} 实现 ICommandHandler 但缺少公共无参构造,跳过。");
                    continue;
                }

                var handler = (ICommandHandler)Activator.CreateInstance(type);
                var name = handler.Command;
                if (string.IsNullOrEmpty(name))
                {
                    Debug.LogError($"[AgentBridge] {type.FullName} 的 Command 为空,跳过。");
                    continue;
                }
                if (s_Handlers.TryGetValue(name, out var existing))
                {
                    Debug.LogError(
                        $"[AgentBridge] 命令名 '{name}' 重复({type.FullName}),拒绝注册;保留 {existing.GetType().FullName}。");
                    continue;
                }

                s_Handlers[name] = handler;
                s_Infos.Add(new CommandInfo
                {
                    Command = name,
                    Description = handler.Description,
                    ParamsSchema = handler.GetParamsSchema()
                });
            }

            s_Infos.Sort((a, b) => string.CompareOrdinal(a.Command, b.Command));
            s_Version = ComputeVersion(VisibleInfos()); // Version 基于可见集(剔除禁用),与 list_commands 一致
            s_Built = true;
        }

        public static bool TryGet(string command, out ICommandHandler handler)
        {
            if (!s_Built)
            {
                Rebuild();
            }
            return s_Handlers.TryGetValue(command, out handler);
        }

        // 确定性内容 hash:对排序后的命令元数据 JSON 序列化算 MD5 取前 8 字节。
        // 禁用 string.GetHashCode()(.NET Core 跨进程随机化,会让 Version 每次重启变)。
        private static string ComputeVersion(List<CommandInfo> infos)
        {
            var canonical = JsonConvert.SerializeObject(infos);
            using (var md5 = MD5.Create())
            {
                var bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(canonical));
                var hex = new StringBuilder(16);
                for (var k = 0; k < 8; k++)
                {
                    hex.Append(bytes[k].ToString("x2"));
                }
                return hex.ToString();
            }
        }
    }
}
