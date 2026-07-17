using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace AgentBridge
{
    public sealed class CloseSceneHandler : ICommandHandler
    {
        public string Command => "close_scene";
        public string Description => "关闭指定已加载场景;显式控制 dirty 场景 error/save/discard";
        public string Group => "Scenes";
        public bool CanDisable => true;
        public CommandBatchMode BatchMode => CommandBatchMode.NotAllowed;

        public Task<object> ExecuteAsync(JObject @params)
        {
            SceneCommandSupport.RequireEditMode(Command);
            if (SceneManager.sceneCount <= 1)
            {
                throw new CommandException(SceneCommandErrorCodes.LastScene,
                    "不能关闭最后一个已加载场景;请先 open_scene(additive) 或 open_scene(single)");
            }
            var scene = SceneCommandSupport.ResolveLoadedScene(@params);
            var wasDirty = scene.isDirty;
            var unsaved = SceneCommandSupport.HandleUnsavedScenes(
                @params,
                SceneUnsavedOperation.CloseScene,
                new[] { scene });

            var label = SceneCommandSupport.Label(scene);
            var handle = SceneCommandSupport.GetHandle(scene);
            if (SceneManager.GetActiveScene().handle == handle)
            {
                for (var i = 0; i < SceneManager.sceneCount; i++)
                {
                    var fallback = SceneManager.GetSceneAt(i);
                    if (fallback.isLoaded && fallback.handle != handle)
                    {
                        SceneManager.SetActiveScene(fallback);
                        break;
                    }
                }
            }
            var removeScene = SceneCommandSupport.ReadBool(@params, "removeScene", true);
            if (!EditorSceneManager.CloseScene(scene, removeScene))
            {
                throw new CommandException(SceneCommandErrorCodes.SceneCloseFailed,
                    $"关闭场景失败:'{label}'");
            }
            return Task.FromResult<object>(new
            {
                closed = true,
                scene = label,
                sceneHandle = handle,
                removed = removeScene,
                wasDirty,
                saved = unsaved.SavedScenes.Length == 1,
                discarded = unsaved.DiscardedScenes.Length == 1
            });
        }

        public JObject ParamsSchema { get; } = JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""scenePath"": { ""type"": ""string"" },
    ""sceneHandle"": { ""type"": ""integer"", ""minimum"": -2147483648, ""maximum"": 2147483647 },
    ""ifUnsaved"": { ""type"": ""string"", ""enum"": [""error"", ""save"", ""discard""], ""default"": ""error"" },
    ""removeScene"": { ""type"": ""boolean"", ""default"": true }
  }
}");
    }
}
