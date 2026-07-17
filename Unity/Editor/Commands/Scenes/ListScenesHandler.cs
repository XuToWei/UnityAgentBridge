using System.Threading.Tasks;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine.SceneManagement;

namespace AgentBridge
{
    public sealed class ListScenesHandler : ICommandHandler
    {
        public string Command => "list_scenes";
        public string Description => "列出当前 Editor 已加载场景:name/path/handle/active/dirty/rootCount/buildSettings";
        public string Group => "Scenes";
        public bool CanDisable => true;
        public CommandBatchMode BatchMode => CommandBatchMode.Allowed;

        public Task<object> ExecuteAsync(JObject @params)
        {
            var scenes = Enumerable.Range(0, SceneManager.sceneCount)
                .Select(i => SceneCommandSupport.Describe(SceneManager.GetSceneAt(i))).ToArray();
            return Task.FromResult<object>(new { count = scenes.Length, scenes });
        }

        public JObject ParamsSchema { get; } = new JObject();
    }
}
