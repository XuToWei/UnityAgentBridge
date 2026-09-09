using System.IO;
using UnityEditor;
using UnityEngine;

namespace AgentBridge
{
    /// <summary>
    /// 按工程持久化桥接宿主的用户启用意图。Bridge root 只保存协议文件，
    /// 不再单独决定 Domain Reload 后是否恢复宿主。
    /// </summary>
    internal static class BridgeHostState
    {
        private const string EnabledKeyPrefix = "AgentBridge.HostEnabled.";

        internal static readonly string PreferenceKey = $"{EnabledKeyPrefix}{Application.dataPath}";

        // 旧版本没有显式状态时,已有 Bridge root 视为已启用;没有 root 则保持关闭。
        internal static bool IsEnabled => EditorPrefs.HasKey(PreferenceKey)
            ? EditorPrefs.GetBool(PreferenceKey, false)
            : Directory.Exists(BridgeSettings.RootDir);

        internal static void SetEnabled(bool enabled)
        {
            EditorPrefs.SetBool(PreferenceKey, enabled);
        }
    }
}
