using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace AgentBridge
{
    /// <summary>
    /// 命令启停(命令管理器 EM2)。全局禁用名单存 EditorPrefs(key 带工程标识,避免跨工程串),
    /// 覆盖内置+扩展所有命令。改完推给 file-bridge CommandRegistry.SetDisabledCommands;
    /// domain reload 后由 CommandToggleBootstrap 重应用。取代 ext-enable-disable 的 per-extension meta。
    /// </summary>
    public static class CommandToggle
    {
        // EditorPrefs 是按 Unity 安装共享的 → key 带 dataPath 区分工程。
        private static string PrefKey => "AgentBridge.DisabledCommands." + Application.dataPath;

        /// <summary>某命令是否不可禁用(由 handler.CanDisable 声明,经 CommandRegistry 汇总)。</summary>
        public static bool IsEssential(string command)
        {
            return !CommandRegistry.CanDisable(command);
        }

        /// <summary>当前被禁用的命令名(只读)。不可禁用命令永不计入(即使 EditorPrefs 里有也忽略)。</summary>
        public static IReadOnlyCollection<string> Disabled()
        {
            var set = Read();
            set.RemoveWhere(c => !CommandRegistry.CanDisable(c));
            return set;
        }

        /// <summary>启停一条命令(内置或扩展均可)。不可禁用命令拒绝禁用。</summary>
        public static void SetEnabled(string command, bool enabled)
        {
            if (string.IsNullOrEmpty(command))
            {
                return;
            }
            if (!enabled && !CommandRegistry.CanDisable(command))
            {
                return; // 不可禁用命令
            }
            var set = Read();
            if (enabled)
            {
                set.Remove(command);
            }
            else
            {
                set.Add(command);
            }
            Write(set);
            Reapply();
        }

        /// <summary>从 EditorPrefs 重建禁用名单并推给 file-bridge。domain reload 后调用。</summary>
        public static void Reapply()
        {
            CommandRegistry.SetDisabledCommands(Disabled());
        }

        private static HashSet<string> Read()
        {
            var raw = EditorPrefs.GetString(PrefKey, "");
            return new HashSet<string>(raw.Split('\n').Where(s => !string.IsNullOrEmpty(s)));
        }

        private static void Write(IEnumerable<string> names)
        {
            EditorPrefs.SetString(PrefKey, string.Join("\n", names));
        }
    }
}
