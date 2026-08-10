using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace AgentBridge
{
    /// <summary>列出所有带 AgentCallable 特性的有效无参静态方法。</summary>
    public sealed class ListAgentMethodsHandler : ICommandHandler
    {
        public const string CommandName = "list_agent_methods";

        public string Command => CommandName;
        public string Description => "列出可供 Agent 调用的无参静态方法、函数说明及等待超时";
        public string Group => "Meta";
        public bool CanDisable => false;
        public CommandBatchMode BatchMode => CommandBatchMode.Allowed;

        public Task<object> ExecuteAsync(JObject @params)
        {
            return Task.FromResult<object>(new { methods = AgentCallableMethodRegistry.GetAll().Select(method => new { id = method.Id, description = method.Description, timeoutSeconds = method.TimeoutSeconds }).ToArray() });
        }

        public JObject ParamsSchema { get; } = new JObject();
    }
}
