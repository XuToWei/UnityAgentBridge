using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace AgentBridge
{
    /// <summary>
    /// 内置连通性测试命令。用于验证 Agent → Unity → Agent 的端到端链路。
    /// 也是 ICommandHandler 最小实现示例。
    /// </summary>
    public sealed class PingHandler : ICommandHandler
    {
        public string Command => "ping";
        public string Description => "连通性测试,返回 pong、Unity 版本及 PlayMode/暂停/编译/刷新状态";
        public string Group => "Meta";
        public bool CanDisable => false;
        public CommandBatchMode BatchMode => CommandBatchMode.Allowed;

        public object Execute(JObject @params)
        {
            return new
            {
                message = "pong",
                unityVersion = Application.unityVersion,
                isPlaying = EditorApplication.isPlaying,
                isPlayingOrWillChangePlaymode = EditorApplication.isPlayingOrWillChangePlaymode,
                isPaused = EditorApplication.isPaused,
                isCompiling = EditorApplication.isCompiling,
                isUpdating = EditorApplication.isUpdating
            };
        }

        public JObject ParamsSchema { get; } = new JObject(); // 无参 → 空 schema {}
    }
}
