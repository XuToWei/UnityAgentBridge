using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace AgentBridge
{
    /// <summary>
    /// 命令启停状态。全局禁用名单存 EditorPrefs,key 带工程标识以避免跨工程串扰。
    /// 禁用名单覆盖内置和扩展命令,改动后同步到 CommandRegistry。
    /// domain reload 后立即恢复,但不构建命令注册表。
    /// </summary>
    [InitializeOnLoad]
    public static class CommandToggle
    {
        // EditorPrefs 是按 Unity 安装共享的 → key 带 dataPath 区分工程。
        private static string PrefKey => "AgentBridge.DisabledCommands." + Application.dataPath;

        static CommandToggle()
        {
            Reapply();
        }

        /// <summary>当前持久化的禁用命令名(只读)。不在读取时构建命令注册表。</summary>
        public static IReadOnlyCollection<string> Disabled()
        {
            return Read();
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

        /// <summary>从 EditorPrefs 恢复禁用名单并同步到 CommandRegistry。domain reload 后不触发命令扫描。</summary>
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
