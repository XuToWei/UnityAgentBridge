using Newtonsoft.Json.Linq;
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
        public string Description => "连通性测试,返回 pong 与 Unity 版本";
        public string Group => "Meta";
        public bool CanDisable => false;

        public object Execute(JObject @params)
        {
            return new
            {
                message = "pong",
                unityVersion = Application.unityVersion
            };
        }

        public JObject GetParamsSchema()
        {
            return new JObject(); // 无参 → 空 schema {}
        }
    }
}
