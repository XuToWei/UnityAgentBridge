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

        internal static readonly string PreferenceKey =
            $"{EnabledKeyPrefix}{Application.dataPath}";

        // 旧版本没有显式状态时默认启用；宿主仍会独立要求 Bridge root 已存在。
        internal static bool IsEnabled =>
            EditorPrefs.GetBool(PreferenceKey, true);

        internal static void SetEnabled(bool enabled)
        {
            EditorPrefs.SetBool(PreferenceKey, enabled);
        }
    }
}
