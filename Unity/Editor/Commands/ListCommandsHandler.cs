using System.Linq;
using Newtonsoft.Json.Linq;

namespace AgentBridge
{
    /// <summary>
    /// 内置元命令 list_commands(M5 / 4.7)。返回当前所有命令的 名字+描述+参数 schema + commandsVersion。
    /// 它自己也是注册命令,故出现在自己的清单里。是 AI 发现可用命令的入口。
    /// </summary>
    public sealed class ListCommandsHandler : ICommandHandler
    {
        public string Command => "list_commands";
        public string Description => "列出所有可用命令及其描述/参数 schema";

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
