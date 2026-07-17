using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace AgentBridge
{
    public sealed class PauseHandler : ICommandHandler
    {
        public string Command => "pause";
        public string Description => "暂停当前 PlayMode;重复调用幂等";
        public string Group => "PlayMode";
        public bool CanDisable => true;
        public CommandBatchMode BatchMode => CommandBatchMode.NotAllowed;

        public Task<object> ExecuteAsync(JObject @params)
        {
            PlayControlSupport.RequireActive(Command);
            var changed = !EditorApplication.isPaused;
            EditorApplication.isPaused = true;
            return Task.FromResult<object>(new { paused = EditorApplication.isPaused, changed });
        }

        public JObject ParamsSchema { get; } = new JObject();
    }
}
