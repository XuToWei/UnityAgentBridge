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

        /// <summary>当前被禁用的命令名(只读)。</summary>
        public static IReadOnlyCollection<string> Disabled()
        {
            return Read();
        }

        /// <summary>启停一条命令(内置或扩展均可)。</summary>
        public static void SetEnabled(string command, bool enabled)
        {
            if (string.IsNullOrEmpty(command))
            {
                return;
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
            CommandRegistry.SetDisabledCommands(Read());
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
