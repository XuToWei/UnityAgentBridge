using Newtonsoft.Json.Linq;
using UnityEngine.SceneManagement;

namespace AgentBridge
{
    public sealed class SetActiveSceneHandler : ICommandHandler
    {
        public string Command => "set_active_scene";
        public string Description => "把指定已加载场景设为 active scene";
        public string Group => "Scenes";
        public bool CanDisable => true;
        public CommandBatchMode BatchMode => CommandBatchMode.NotAllowed;

        public object Execute(JObject @params)
        {
            SceneCommandSupport.RequireEditMode(Command);
            var scene = SceneCommandSupport.ResolveLoadedScene(@params, true);
            var previous = SceneManager.GetActiveScene();
            var changed = !previous.IsValid() || previous.handle != scene.handle;
            if (changed && !SceneManager.SetActiveScene(scene))
            {
                throw new CommandException(SceneCommandErrorCodes.SceneSetActiveFailed,
                    $"无法激活场景:'{SceneCommandSupport.Label(scene)}'");
            }
            return new
            {
                changed,
                previous = previous.IsValid() ? SceneCommandSupport.Label(previous) : null,
                scene = SceneCommandSupport.Describe(scene)
            };
        }

        public JObject ParamsSchema { get; } = JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""scenePath"": { ""type"": ""string"" },
    ""sceneHandle"": { ""type"": ""integer"", ""minimum"": -2147483648, ""maximum"": 2147483647 }
  },
  ""anyOf"": [{ ""required"": [""scenePath""] }, { ""required"": [""sceneHandle""] }]
}");
    }
}
