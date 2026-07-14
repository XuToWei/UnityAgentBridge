using System;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace AgentBridge
{
    /// <summary>
    /// play_scene(运行控制):打开指定场景并请求进入 Play Mode。
    /// 空参数运行当前场景；也可用 scenePath/buildIndex 指定场景，或 stop=true 停止 Play Mode。
    /// 切换场景时可用 ifUnsaved(error/save/discard,默认 error) 处理未保存内容。
    /// </summary>
    public sealed class PlaySceneHandler : ICommandHandler
    {
        private const string UnitySceneExtension = ".unity";

        public string Command => "play_scene";

        public string Description =>
            "空参运行当前场景;可指定 scenePath/buildIndex,或 stop=true 停止 Play Mode";

        public string Group => "PlayMode";
        public bool CanDisable => true;
        public CommandBatchMode BatchMode => CommandBatchMode.NotAllowed;

        public object Execute(JObject @params)
        {
            if (GetOptionalBool(@params, "stop", false))
            {
                return RequestStopPlayMode();
            }

            if (EditorApplication.isPlaying)
            {
                throw new CommandException(PlayModeErrorCodes.PlayModeAlreadyActive, "Unity 已在 Play Mode,不能切换场景后重新运行。");
            }
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                throw new CommandException(PlayModeErrorCodes.PlayModeTransition, "Unity 正在进入或退出 Play Mode,请稍后重试。");
            }

            var requireInBuildSettings = GetOptionalBool(@params, "requireInBuildSettings", false);
            var ifUnsaved = SceneCommandSupport.ReadIfUnsaved(
                @params,
                SceneUnsavedOperation.PlaySceneSwitch);
            var useCurrentScene = !HasValue(@params?["scenePath"]) && !HasValue(@params?["buildIndex"]);
            var target = ResolveTargetScene(@params, requireInBuildSettings);
            var alreadyOpen = useCurrentScene || IsOnlyOpenScene(target.Path);
            var unsaved = SceneCommandSupport.HandleUnsavedScenes(
                ifUnsaved,
                alreadyOpen
                    ? SceneUnsavedOperation.PlaySceneAlreadyOpen
                    : SceneUnsavedOperation.PlaySceneSwitch);

            var opened = false;
            if (!alreadyOpen)
            {
                OpenTargetScene(target.Path);
                opened = true;
            }

            RequestPlayMode();

            return new
            {
                scene = new
                {
                    path = target.Path,
                    name = target.Name
                },
                opened,
                alreadyOpen,
                playRequested = true,
                playModeState = new
                {
                    isPlaying = EditorApplication.isPlaying,
                    isPlayingOrWillChangePlaymode = EditorApplication.isPlayingOrWillChangePlaymode
                },
                buildSettings = new
                {
                    present = target.BuildSettings.Present,
                    enabledBuildIndex = target.BuildSettings.EnabledBuildIndex >= 0 ? (int?)target.BuildSettings.EnabledBuildIndex : null,
                    enabled = target.BuildSettings.Present ? (bool?)target.BuildSettings.Enabled : null
                },
                unsaved = new
                {
                    action = ifUnsaved,
                    dirtyScenes = unsaved.DirtyScenes,
                    savedScenes = unsaved.SavedScenes,
                    discardedScenes = unsaved.DiscardedScenes
                }
            };
        }

        private static object RequestStopPlayMode()
        {
            var wasActive = EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode;
            if (wasActive)
            {
                EditorApplication.isPlaying = false;
            }

            return new
            {
                stopRequested = wasActive,
                alreadyStopped = !wasActive,
                playModeState = new
                {
                    isPlaying = EditorApplication.isPlaying,
                    isPlayingOrWillChangePlaymode = EditorApplication.isPlayingOrWillChangePlaymode
                }
            };
        }

        private static TargetScene ResolveTargetScene(JObject @params, bool requireInBuildSettings)
        {
            var scenePathToken = @params?["scenePath"];
            var buildIndexToken = @params?["buildIndex"];
            var hasScenePath = HasValue(scenePathToken);
            var hasBuildIndex = HasValue(buildIndexToken);

            if (hasScenePath && hasBuildIndex)
            {
                throw new CommandException(ErrorCodes.InvalidParams, "scenePath 与 buildIndex 只能提供一个。");
            }

            if (!hasScenePath && !hasBuildIndex)
            {
                return ResolveCurrentScene(requireInBuildSettings);
            }

            if (hasScenePath)
            {
                var scenePath = GetString(scenePathToken, "scenePath");
                var normalized = RequireScenePath(scenePath);
                var buildSettings = FindBuildSettings(normalized);
                if (requireInBuildSettings && (!buildSettings.Present || !buildSettings.Enabled))
                {
                    throw new CommandException(PlayModeErrorCodes.SceneNotInBuildSettings,
                        $"场景不在 Build Settings 已启用列表中:'{normalized}'");
                }
                return new TargetScene(normalized, Path.GetFileNameWithoutExtension(normalized), buildSettings);
            }

            var buildIndex = GetBuildIndex(buildIndexToken);
            return ResolveBuildIndex(buildIndex);
        }

        private static TargetScene ResolveCurrentScene(bool requireInBuildSettings)
        {
            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded)
            {
                throw new CommandException(PlayModeErrorCodes.SceneNotFound, "当前没有可运行的已加载场景。");
            }

            var path = (scene.path ?? "").Replace('\\', '/');
            var buildSettings = string.IsNullOrEmpty(path)
                ? new BuildSettingsInfo(false, -1, false)
                : FindBuildSettings(path);
            if (requireInBuildSettings && (!buildSettings.Present || !buildSettings.Enabled))
            {
                throw new CommandException(PlayModeErrorCodes.SceneNotInBuildSettings,
                    $"当前场景不在 Build Settings 已启用列表中:'{path}'");
            }

            return new TargetScene(path, scene.name, buildSettings);
        }

        private static string RequireScenePath(string scenePath)
        {
            if (string.IsNullOrWhiteSpace(scenePath))
            {
                throw new CommandException(PlayModeErrorCodes.InvalidScenePath, "缺 scenePath。");
            }

            string path;
            try
            {
                path = AssetSupport.RequireFilePath(scenePath.Replace('\\', '/').Trim(), "scenePath");
            }
            catch (CommandException ex) when (ex.Code == AssetErrorCodes.InvalidAssetPath)
            {
                throw new CommandException(PlayModeErrorCodes.InvalidScenePath, ex.Message);
            }
            if (!path.EndsWith(UnitySceneExtension, StringComparison.OrdinalIgnoreCase))
            {
                throw new CommandException(PlayModeErrorCodes.InvalidScenePath,
                    $"scenePath 必须是 Assets/ 下的 .unity 场景路径:'{scenePath}'");
            }

            var asset = AssetDatabase.LoadAssetAtPath<SceneAsset>(path);
            if (asset == null)
            {
                throw new CommandException(PlayModeErrorCodes.SceneNotFound, $"场景不存在或不是 SceneAsset:'{path}'");
            }

            return path;
        }

        private static TargetScene ResolveBuildIndex(int buildIndex)
        {
            var enabledIndex = 0;
            var scenes = EditorBuildSettings.scenes;
            for (var i = 0; i < scenes.Length; i++)
            {
                var scene = scenes[i];
                if (!scene.enabled)
                {
                    continue;
                }

                if (enabledIndex == buildIndex)
                {
                    var path = RequireScenePath(scene.path);
                    return new TargetScene(path, Path.GetFileNameWithoutExtension(path), new BuildSettingsInfo(true, enabledIndex, true));
                }
                enabledIndex++;
            }

            throw new CommandException(PlayModeErrorCodes.SceneNotInBuildSettings,
                $"Build Settings 中不存在已启用场景 buildIndex={buildIndex}。");
        }

        private static BuildSettingsInfo FindBuildSettings(string path)
        {
            var enabledIndex = 0;
            var scenes = EditorBuildSettings.scenes;
            for (var i = 0; i < scenes.Length; i++)
            {
                var scene = scenes[i];
                var scenePath = (scene.path ?? "").Replace('\\', '/');
                if (SceneCommandSupport.PathsEqual(scenePath, path))
                {
                    return new BuildSettingsInfo(true, scene.enabled ? enabledIndex : -1, scene.enabled);
                }
                if (scene.enabled)
                {
                    enabledIndex++;
                }
            }
            return new BuildSettingsInfo(false, -1, false);
        }

        private static bool IsOnlyOpenScene(string targetPath)
        {
            if (SceneManager.sceneCount != 1)
            {
                return false;
            }

            var scene = SceneManager.GetSceneAt(0);
            return scene.isLoaded && SceneCommandSupport.PathsEqual(scene.path, targetPath);
        }

        private static void OpenTargetScene(string path)
        {
            try
            {
                var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                if (!scene.IsValid() || !scene.isLoaded)
                {
                    throw new CommandException(PlayModeErrorCodes.SceneOpenFailed, $"打开场景失败:'{path}'");
                }
                SceneManager.SetActiveScene(scene);
            }
            catch (CommandException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new CommandException(PlayModeErrorCodes.SceneOpenFailed, $"打开场景失败: {ex.Message}");
            }
        }

        private static void RequestPlayMode()
        {
            try
            {
                EditorApplication.isPlaying = true;
            }
            catch (Exception ex)
            {
                throw new CommandException(PlayModeErrorCodes.EnterPlayModeFailed, $"请求进入 Play Mode 失败: {ex.Message}");
            }
        }

        private static int GetBuildIndex(JToken token)
        {
            if (token.Type != JTokenType.Integer)
            {
                throw new CommandException(ErrorCodes.InvalidParams, "buildIndex 必须是 integer。");
            }
            long value;
            try
            {
                value = token.Value<long>();
            }
            catch (System.Exception ex) when (ex is System.OverflowException || ex is System.FormatException || ex is System.InvalidCastException)
            {
                throw new CommandException(ErrorCodes.InvalidParams, "buildIndex 必须在 0 到 int.MaxValue 之间。");
            }
            if (value < 0 || value > int.MaxValue)
            {
                throw new CommandException(ErrorCodes.InvalidParams, "buildIndex 必须在 0 到 int.MaxValue 之间。");
            }
            return (int)value;
        }

        private static bool GetOptionalBool(JObject @params, string name, bool defaultValue)
        {
            var token = @params?[name];
            if (!HasValue(token))
            {
                return defaultValue;
            }
            if (token.Type != JTokenType.Boolean)
            {
                throw new CommandException(ErrorCodes.InvalidParams, $"{name} 必须是 boolean。");
            }
            return token.Value<bool>();
        }

        private static string GetString(JToken token, string name)
        {
            if (token.Type != JTokenType.String)
            {
                throw new CommandException(ErrorCodes.InvalidParams, $"{name} 必须是 string。");
            }
            return token.Value<string>();
        }

        private static bool HasValue(JToken token)
        {
            return token != null && token.Type != JTokenType.Null;
        }

        public JObject ParamsSchema { get; } = JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""stop"": { ""type"": ""boolean"", ""description"": ""true 时停止 Play Mode,其它参数忽略;默认 false。"" },
    ""scenePath"": { ""type"": ""string"", ""description"": ""可选 Assets/ 下的 .unity 场景路径;与 buildIndex 只能提供一个。均不提供时运行当前场景。"" },
    ""buildIndex"": { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 2147483647, ""description"": ""可选 Build Settings 运行时 build index;与 scenePath 只能提供一个。"" },
    ""requireInBuildSettings"": { ""type"": ""boolean"", ""description"": ""scenePath 是否必须存在于 Build Settings 且已启用,默认 false。"" },
    ""ifUnsaved"": { ""type"": ""string"", ""enum"": [""error"", ""save"", ""discard""], ""description"": ""切换场景前如何处理未保存场景,默认 error。"" }
  }
}");

        private sealed class TargetScene
        {
            public TargetScene(string path, string name, BuildSettingsInfo buildSettings)
            {
                Path = path;
                Name = name;
                BuildSettings = buildSettings;
            }

            public string Path { get; }
            public string Name { get; }
            public BuildSettingsInfo BuildSettings { get; }
        }

        private readonly struct BuildSettingsInfo
        {
            public BuildSettingsInfo(bool present, int enabledBuildIndex, bool enabled)
            {
                Present = present;
                EnabledBuildIndex = enabledBuildIndex;
                Enabled = enabled;
            }

            public bool Present { get; }
            public int EnabledBuildIndex { get; }
            public bool Enabled { get; }
        }

    }
}
