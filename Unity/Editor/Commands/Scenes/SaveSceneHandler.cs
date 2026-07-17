using System.Threading.Tasks;
using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace AgentBridge
{
    public sealed class SaveSceneHandler : ICommandHandler
    {
        public string Command => "save_scene";
        public string Description => "保存 active/指定/全部 dirty 场景;未命名场景可用 saveAs 非交互另存";
        public string Group => "Scenes";
        public bool CanDisable => true;
        public CommandBatchMode BatchMode => CommandBatchMode.NotAllowed;

        public Task<object> ExecuteAsync(JObject @params)
        {
            SceneCommandSupport.RequireEditMode(Command);
            var all = SceneCommandSupport.ReadBool(@params, "all", false);
            var hasSelector = @params?["scenePath"] != null || @params?["sceneHandle"] != null;
            var saveAsToken = @params?["saveAs"];
            var hasSaveAs = saveAsToken != null && saveAsToken.Type != JTokenType.Null;
            if (all && (hasSelector || hasSaveAs))
            {
                throw new CommandException(ErrorCodes.InvalidParams,
                    "all=true 时不能同时提供 scenePath/sceneHandle/saveAs");
            }

            if (all)
            {
                var dirty = SceneCommandSupport.DirtyScenes();
                var saved = SceneCommandSupport.SaveDirtyScenes(dirty);
                return Task.FromResult<object>(new { all = true, savedCount = saved.Length, saved });
            }

            var scene = SceneCommandSupport.ResolveLoadedScene(@params);
            var previousPath = scene.path;
            var wasDirty = scene.isDirty;
            string destination = null;
            if (hasSaveAs)
            {
                destination = SceneCommandSupport.RequireSceneAssetPath(
                    saveAsToken.Value<string>(), "saveAs", false);
                SceneCommandSupport.RequireSaveFolder(destination);
                var overwrite = SceneCommandSupport.ReadBool(@params, "overwrite", false);
                var existing = AssetDatabase.LoadAssetAtPath<SceneAsset>(destination);
                if (existing != null &&
                    !SceneCommandSupport.PathsEqual(previousPath, destination) && !overwrite)
                {
                    throw new CommandException(AssetErrorCodes.AssetAlreadyExists,
                        $"目标场景已存在:'{destination}';如需覆盖请传 overwrite=true");
                }
            }
            else if (string.IsNullOrEmpty(scene.path))
            {
                throw new CommandException(SceneCommandErrorCodes.SceneSaveFailed,
                    "未命名场景需要 saveAs=Assets/.../*.unity");
            }

            var ok = hasSaveAs
                ? EditorSceneManager.SaveScene(scene, destination, false)
                : EditorSceneManager.SaveScene(scene);
            if (!ok)
            {
                throw new CommandException(SceneCommandErrorCodes.SceneSaveFailed,
                    $"保存场景失败:'{SceneCommandSupport.Label(scene)}'");
            }

            return Task.FromResult<object>(new
            {
                all = false,
                saved = true,
                wasDirty,
                previousPath,
                scene = SceneCommandSupport.Describe(scene)
            });
        }

        public JObject ParamsSchema { get; } = JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""scenePath"": { ""type"": ""string"", ""description"": ""已加载场景路径;与 sceneHandle 二选一。缺省为 active scene。"" },
    ""sceneHandle"": { ""type"": ""integer"", ""minimum"": -2147483648, ""maximum"": 2147483647 },
    ""all"": { ""type"": ""boolean"", ""description"": ""保存全部 dirty 场景。"" },
    ""saveAs"": { ""type"": ""string"", ""description"": ""单场景另存为 Assets/.../*.unity。"" },
    ""overwrite"": { ""type"": ""boolean"", ""description"": ""saveAs 已存在时允许覆盖,默认 false。"" }
  }
}");
    }
}
