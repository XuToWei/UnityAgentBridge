using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace AgentBridge
{
    public sealed class OpenSceneHandler : ICommandHandler
    {
        public string Command => "open_scene";
        public string Description => "以 single/additive 打开场景;显式控制 dirty 场景 error/save/discard";
        public string Group => "Scenes";
        public bool CanDisable => true;
        public CommandBatchMode BatchMode => CommandBatchMode.NotAllowed;

        public object Execute(JObject @params)
        {
            SceneCommandSupport.RequireEditMode(Command);
            var path = SceneCommandSupport.RequireSceneAssetPath(@params?["scenePath"]?.Value<string>());
            var modeName = @params?["mode"]?.Value<string>() ?? "single";
            var mode = modeName == "additive" ? OpenSceneMode.Additive : OpenSceneMode.Single;
            if (modeName != "single" && modeName != "additive")
            {
                throw new CommandException(ErrorCodes.InvalidParams, "mode 只能是 single / additive");
            }
            var setActive = SceneCommandSupport.ReadBool(@params, "setActive", mode == OpenSceneMode.Single);
            var already = SceneCommandSupport.FindLoadedScene(path);
            if (mode == OpenSceneMode.Additive && already.IsValid() && already.isLoaded)
            {
                if (setActive && SceneManager.GetActiveScene().handle != already.handle &&
                    !SceneManager.SetActiveScene(already))
                {
                    throw new CommandException(SceneCommandErrorCodes.SceneSetActiveFailed,
                        $"无法激活已加载场景:'{path}'");
                }
                return new { opened = false, alreadyLoaded = true, scene = SceneCommandSupport.Describe(already) };
            }

            if (mode == OpenSceneMode.Single && already.IsValid() && already.isLoaded)
            {
                var others = Enumerable.Range(0, SceneManager.sceneCount)
                    .Select(SceneManager.GetSceneAt)
                    .Where(scene => scene.isLoaded && scene.handle != already.handle)
                    .ToArray();
                var otherLabels = others.Select(SceneCommandSupport.Label).ToArray();
                var unsaved = SceneCommandSupport.HandleUnsavedScenes(
                    @params,
                    SceneUnsavedOperation.OpenSingleKeepingTarget,
                    others);
                if (setActive && SceneManager.GetActiveScene().handle != already.handle &&
                    !SceneManager.SetActiveScene(already))
                {
                    throw new CommandException(SceneCommandErrorCodes.SceneSetActiveFailed,
                        $"无法激活已加载场景:'{path}'");
                }
                foreach (var other in others)
                {
                    if (!EditorSceneManager.CloseScene(other, true))
                    {
                        throw new CommandException(SceneCommandErrorCodes.SceneCloseFailed,
                            $"切换 single 场景时无法关闭:'{SceneCommandSupport.Label(other)}'");
                    }
                }
                return new
                {
                    opened = false,
                    alreadyLoaded = true,
                    mode = modeName,
                    dirtyScenes = unsaved.DirtyScenes,
                    savedScenes = unsaved.SavedScenes,
                    discardedScenes = unsaved.DiscardedScenes,
                    closedScenes = otherLabels,
                    scene = SceneCommandSupport.Describe(already)
                };
            }

            var unsavedScenes = SceneCommandSupport.HandleUnsavedScenes(
                @params,
                SceneUnsavedOperation.OpenSingle,
                mode == OpenSceneMode.Single ? null : Array.Empty<Scene>());

            Scene opened;
            try
            {
                opened = EditorSceneManager.OpenScene(path, mode);
            }
            catch (Exception ex)
            {
                throw new CommandException(SceneCommandErrorCodes.SceneOpenFailed,
                    $"打开场景失败:{ex.Message}");
            }
            if (!opened.IsValid() || !opened.isLoaded)
            {
                throw new CommandException(SceneCommandErrorCodes.SceneOpenFailed,
                    $"打开场景失败:'{path}'");
            }
            if (setActive && SceneManager.GetActiveScene().handle != opened.handle &&
                !SceneManager.SetActiveScene(opened))
            {
                throw new CommandException(SceneCommandErrorCodes.SceneSetActiveFailed,
                    $"场景已打开但无法设为 active:'{path}'");
            }

            return new
            {
                opened = true,
                alreadyLoaded = false,
                mode = modeName,
                dirtyScenes = unsavedScenes.DirtyScenes,
                savedScenes = unsavedScenes.SavedScenes,
                discardedScenes = unsavedScenes.DiscardedScenes,
                scene = SceneCommandSupport.Describe(opened)
            };
        }

        public JObject ParamsSchema { get; } = JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""scenePath"": { ""type"": ""string"" },
    ""mode"": { ""type"": ""string"", ""enum"": [""single"", ""additive""], ""default"": ""single"" },
    ""setActive"": { ""type"": ""boolean"" },
    ""ifUnsaved"": { ""type"": ""string"", ""enum"": [""error"", ""save"", ""discard""], ""default"": ""error"" }
  },
  ""required"": [""scenePath""]
}");
    }
}
