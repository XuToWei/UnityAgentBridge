using Newtonsoft.Json.Linq;
using UnityEngine;

namespace AgentBridge
{
    /// <summary>
    /// 内置命令 ping(M5)。打通端到端的最小闭环,也是 handler 框架的示范:
    /// 写一个类实现 ICommandHandler,放进被编译的程序集即生效。
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
