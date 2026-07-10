using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace AgentBridge
{
    /// <summary>
    /// 失焦后台运行开关。Unity 编辑器失焦时会节流主循环,导致 EditorApplication.update
    /// 近乎停摆、桥接不再轮询。把 Interaction Mode 设为 No Throttling(idle=0)可让编辑器
    /// 失焦也持续运行。
    ///
    /// 说明:Interaction Mode 是全局 EditorPrefs(影响本机该 Unity 版本所有工程),故由 AgentBridgeWindow
    /// 顶部开关显式启停,不在包加载时强制改。首选项键与 Unity Preferences 保持一致;
    /// 应用方法为 Unity internal,用反射调用。
    /// </summary>
    public static class BridgeBackgroundMode
    {
        private const string IdleKey = "ApplicationIdleTime";
        private const string ModeKey = "InteractionMode";

        /// <summary>当前是否为 No Throttling(失焦不节流)。供窗口 Toggle 反映状态。</summary>
        public static bool IsNoThrottling =>
            EditorPrefs.GetInt(IdleKey, 4) == 0 && EditorPrefs.GetInt(ModeKey, 0) == 1;

        public static void EnableNoThrottling()
        {
            EditorPrefs.SetInt(IdleKey, 0);
            EditorPrefs.SetInt(ModeKey, 1);
            Apply();
            Debug.Log("[AgentBridge] Interaction Mode = No Throttling：编辑器失焦也持续运行(CPU 占用会升高)。");
        }

        public static void RestoreDefault()
        {
            EditorPrefs.DeleteKey(IdleKey);
            EditorPrefs.DeleteKey(ModeKey);
            Apply();
            Debug.Log("[AgentBridge] Interaction Mode 恢复 Default。");
        }

        private static void Apply()
        {
            typeof(EditorApplication)
                .GetMethod("UpdateInteractionModeSettings",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                ?.Invoke(null, null);
        }
    }
}
