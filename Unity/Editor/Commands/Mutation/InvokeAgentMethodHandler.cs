using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace AgentBridge
{
    /// <summary>按发现命令返回的完整 ID 调用一个 AgentCallable 方法。</summary>
    public sealed class InvokeAgentMethodHandler : ICommandHandler
    {
        public const string CommandName = "invoke_agent_method";

        public string Command => CommandName;
        public string Description => "调用一个 AgentCallable 无参静态方法;忽略返回值,Awaitable 会等待完成";
        public string Group => "Mutation";
        public bool CanDisable => true;
        public CommandBatchMode BatchMode => CommandBatchMode.NotAllowed;

        public async Task<object> ExecuteAsync(JObject @params)
        {
            var id = @params["method"].Value<string>();
            if (!AgentCallableMethodRegistry.TryGet(id, out var method))
            {
                throw new CommandException(AgentCallableErrorCodes.MethodNotFound, $"AgentCallable 方法 '{id}' 不存在;请重新调用 list_agent_methods");
            }

            await method.InvokeAsync();
            return new { method = id, invoked = true };
        }

        public JObject ParamsSchema { get; } = JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""method"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 1024 }
  },
  ""required"": [""method""],
  ""additionalProperties"": false
}");
    }
}
