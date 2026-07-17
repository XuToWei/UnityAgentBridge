using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace AgentBridge
{
    public sealed class ResumeHandler : ICommandHandler
    {
        public string Command => "resume";
        public string Description => "恢复已暂停的 PlayMode;重复调用幂等";
        public string Group => "PlayMode";
        public bool CanDisable => true;
        public CommandBatchMode BatchMode => CommandBatchMode.NotAllowed;

        public Task<object> ExecuteAsync(JObject @params)
        {
            PlayControlSupport.RequireActive(Command);
            var changed = EditorApplication.isPaused;
            EditorApplication.isPaused = false;
            return Task.FromResult<object>(new { paused = EditorApplication.isPaused, changed });
        }

        public JObject ParamsSchema { get; } = new JObject();
    }
}
