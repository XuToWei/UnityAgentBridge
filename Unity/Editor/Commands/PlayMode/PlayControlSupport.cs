using UnityEditor;

namespace AgentBridge
{
    internal static class PlayControlSupport
    {
        public static void RequireActive(string command)
        {
            if (EditorApplication.isPlaying != EditorApplication.isPlayingOrWillChangePlaymode)
            {
                throw new CommandException(PlayModeErrorCodes.PlayModeTransition,
                    $"Unity 正在切换 PlayMode,请稍后重试 {command}");
            }
            if (!EditorApplication.isPlaying)
            {
                throw new CommandException("PLAY_MODE_NOT_ACTIVE",
                    $"{command} 需要 Unity 已进入 PlayMode");
            }
        }
    }
}
