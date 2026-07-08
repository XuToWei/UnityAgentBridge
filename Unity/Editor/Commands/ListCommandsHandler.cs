using System.Linq;
using Newtonsoft.Json.Linq;

namespace AgentBridge
{
    /// <summary>
    /// 内置元命令,返回当前可用命令的名称、描述、参数 schema 和命令集版本。
    /// AI 可先调用它来发现当前 Unity 侧支持哪些命令。
    /// </summary>
    public sealed class ListCommandsHandler : ICommandHandler
    {
        public string Command => "list_commands";
        public string Description => "列出所有可用命令及其描述/参数 schema";
        public string Group => "Meta";
        public bool CanDisable => false;

        public object Execute(JObject @params)
        {
            return new
            {
                commands = CommandRegistry.GetAll().Select(i => new
                {
                    command = i.Command,
                    description = i.Description,
                    paramsSchema = i.ParamsSchema
                }).ToArray(),
                commandsVersion = CommandRegistry.Version
            };
        }

        public JObject GetParamsSchema()
        {
            return new JObject(); // 无参 → 空 schema {}
        }
    }
}
