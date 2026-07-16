using System.Linq;
using Newtonsoft.Json.Linq;

namespace AgentBridge
{
    /// <summary>
    /// 内置元命令,返回当前可用命令的名称、描述、参数 schema、batch 策略和命令集版本。
    /// AI 可先调用它来发现当前 Unity 侧支持哪些命令。
    /// </summary>
    public sealed class ListCommandsHandler : ICommandHandler
    {
        public string Command => "list_commands";
        public string Description => "列出所有可用命令及其描述/参数 schema/batch 策略";
        public string Group => "Meta";
        public bool CanDisable => false;
        public CommandBatchMode BatchMode => CommandBatchMode.Allowed;

        public async CommandTask<object> ExecuteAsync(JObject @params)
        {
            return new
            {
                commands = CommandRegistry.GetAll().Select(i => new
                {
                    command = i.Command,
                    description = i.Description,
                    paramsSchema = i.ParamsSchema,
                    batchAllowed = i.BatchAllowed,
                    supportsUndoCollapse = i.SupportsUndoCollapse
                }).ToArray(),
                commandsVersion = CommandRegistry.Version
            };
        }

        public JObject ParamsSchema { get; } = new JObject(); // 无参 → 空 schema {}
    }
}
