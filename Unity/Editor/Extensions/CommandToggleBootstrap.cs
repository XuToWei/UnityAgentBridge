using UnityEditor;

namespace AgentBridge
{
    /// <summary>
    /// domain reload 重应用钩子(命令管理器 EM2)。禁用名单是 file-bridge 进程内状态,domain reload 重置 →
    /// 加载后从 EditorPrefs 重建。用 delayCall 推到下一帧,确保 CommandRegistry 就绪。
    /// 取代 ext-enable-disable 的 ExtensionStateBootstrap。
    /// </summary>
    [InitializeOnLoad]
    public static class CommandToggleBootstrap
    {
        static CommandToggleBootstrap()
        {
            EditorApplication.delayCall += CommandToggle.Reapply;
        }
    }
}
