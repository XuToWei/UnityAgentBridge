using UnityEditor;

namespace AgentBridge
{
    /// <summary>
    /// domain reload 后重应用命令禁用名单。
    /// 用 delayCall 推到下一帧,确保 CommandRegistry 已完成可用命令扫描。
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
