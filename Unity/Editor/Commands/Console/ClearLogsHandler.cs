using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace AgentBridge
{
    public sealed class ClearLogsHandler : ICommandHandler
    {
        public string Command => "clear_logs";
        public string Description => "清空 Unity Editor Console 当前日志;不可撤销,返回 clearedCount";
        public string Group => "Console";
        public bool CanDisable => true;
        public CommandBatchMode BatchMode => CommandBatchMode.NotAllowed;

        public Task<object> ExecuteAsync(JObject @params)
        {
            var count = ConsoleLogReader.Clear();
            return Task.FromResult<object>(new { cleared = true, clearedCount = count });
        }

        public JObject ParamsSchema { get; } = new JObject();
    }
}
