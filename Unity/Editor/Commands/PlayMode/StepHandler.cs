using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace AgentBridge
{
    public sealed class StepHandler : ICommandHandler
    {
        public string Command => "step";
        public string Description => "在已暂停 PlayMode 请求前进一帧;未暂停时拒绝";
        public string Group => "PlayMode";
        public bool CanDisable => true;
        public CommandBatchMode BatchMode => CommandBatchMode.NotAllowed;

        public Task<object> ExecuteAsync(JObject @params)
        {
            PlayControlSupport.RequireActive(Command);
            if (!EditorApplication.isPaused)
            {
                throw new CommandException("PLAY_MODE_NOT_PAUSED",
                    "step 前必须先调用 pause");
            }
            EditorApplication.Step();
            return Task.FromResult<object>(new { stepRequested = true, paused = EditorApplication.isPaused });
        }

        public JObject ParamsSchema { get; } = new JObject();
    }
}
