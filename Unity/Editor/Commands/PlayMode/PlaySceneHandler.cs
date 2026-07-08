using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace AgentBridge
{
    /// <summary>
    /// play_scene(运行控制):打开指定场景并请求进入 Play Mode。
    /// params:scenePath 或 buildIndex 二选一;ifUnsaved(error/save/discard,默认 error);requireInBuildSettings(默认 false)。
    /// </summary>
    public sealed class PlaySceneHandler : ICommandHandler
    {
        private const string UnsavedError = "error";
        private const string UnsavedSave = "save";
        private const string UnsavedDiscard = "discard";
        private const string UnitySceneExtension = ".unity";

        public string Command => "play_scene";

        public string Description =>
            "打开指定场景并请求进入 Play Mode:scenePath 或 buildIndex 二选一;ifUnsaved=error|save|discard;可 requireInBuildSettings";

        public string Group => "PlayMode";
        public bool CanDisable => true;

        public object Execute(JObject @params)
        {
            if (EditorApplication.isPlaying)
            {
                throw new CommandException(PlayModeErrorCodes.PlayModeAlreadyActive, "Unity 已在 Play Mode,不能切换场景后重新运行。");
            }
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                throw new CommandException(PlayModeErrorCodes.PlayModeTransition, "Unity 正在进入或退出 Play Mode,请稍后重试。");
            }

            var requireInBuildSettings = GetOptionalBool(@params, "requireInBuildSettings", false);
            var ifUnsaved = GetIfUnsaved(@params);
            var target = ResolveTargetScene(@params, requireInBuildSettings);
            var alreadyOpen = IsOnlyOpenScene(target.Path);
            var unsaved = HandleUnsavedScenes(ifUnsaved, alreadyOpen);

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

        private static TargetScene ResolveTargetScene(JObject @params, bool requireInBuildSettings)
        {
            var scenePathToken = @params?["scenePath"];
            var buildIndexToken = @params?["buildIndex"];
            var hasScenePath = HasValue(scenePathToken);
            var hasBuildIndex = HasValue(buildIndexToken);

            if (hasScenePath == hasBuildIndex)
            {
                throw new CommandException(ErrorCodes.InvalidParams, "scenePath 与 buildIndex 必须二选一且只能提供一个。");
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

        private static string RequireScenePath(string scenePath)
        {
            if (string.IsNullOrWhiteSpace(scenePath))
            {
                throw new CommandException(PlayModeErrorCodes.InvalidScenePath, "缺 scenePath。");
            }

            var path = scenePath.Replace('\\', '/').Trim();
            if (Path.IsPathRooted(path) || path.Contains(":") || path.Contains("..") ||
                (path != "Assets" && !path.StartsWith("Assets/", StringComparison.Ordinal)) ||
                !path.EndsWith(UnitySceneExtension, StringComparison.OrdinalIgnoreCase))
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
                if (string.Equals(scenePath, path, StringComparison.OrdinalIgnoreCase))
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
            return scene.isLoaded && string.Equals((scene.path ?? "").Replace('\\', '/'), targetPath, StringComparison.OrdinalIgnoreCase);
        }

        private static UnsavedResult HandleUnsavedScenes(string action, bool alreadyOpen)
        {
            var dirtyScenes = GetDirtyScenes();
            var dirtySceneLabels = ToLabels(dirtyScenes);
            var saved = new List<string>();
            var discarded = new List<string>();

            if (alreadyOpen || dirtyScenes.Count == 0)
            {
                return new UnsavedResult(dirtySceneLabels, saved.ToArray(), discarded.ToArray());
            }

            switch (action)
            {
                case UnsavedError:
                    throw new CommandException(PlayModeErrorCodes.UnsavedScenes,
                        "当前打开场景有未保存修改,默认不会切换场景:" + string.Join(", ", dirtySceneLabels));
                case UnsavedSave:
                    SaveDirtyScenes(dirtyScenes, saved);
                    break;
                case UnsavedDiscard:
                    discarded.AddRange(dirtySceneLabels);
                    break;
            }

            return new UnsavedResult(dirtySceneLabels, saved.ToArray(), discarded.ToArray());
        }

        private static List<Scene> GetDirtyScenes()
        {
            var result = new List<Scene>();
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isDirty)
                {
                    result.Add(scene);
                }
            }
            return result;
        }

        private static string[] ToLabels(List<Scene> scenes)
        {
            var result = new string[scenes.Count];
            for (var i = 0; i < scenes.Count; i++)
            {
                result[i] = SceneLabel(scenes[i]);
            }
            return result;
        }

        private static string SceneLabel(Scene scene)
        {
            return string.IsNullOrEmpty(scene.path) ? scene.name : scene.path;
        }

        private static void SaveDirtyScenes(List<Scene> dirtyScenes, List<string> saved)
        {
            foreach (var scene in dirtyScenes)
            {
                if (string.IsNullOrEmpty(scene.path))
                {
                    throw new CommandException(PlayModeErrorCodes.SceneSaveFailed,
                        $"未命名场景 '{scene.name}' 无法非交互保存,请先手动保存或传 ifUnsaved=discard。");
                }
                if (!EditorSceneManager.SaveScene(scene))
                {
                    throw new CommandException(PlayModeErrorCodes.SceneSaveFailed, $"保存场景失败:'{scene.path}'");
                }
                saved.Add(scene.path);
            }
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
                throw new CommandException(PlayModeErrorCodes.SceneOpenFailed, "打开场景失败: " + ex.Message);
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
                throw new CommandException(PlayModeErrorCodes.EnterPlayModeFailed, "请求进入 Play Mode 失败: " + ex.Message);
            }
        }

        private static string GetIfUnsaved(JObject @params)
        {
            var token = @params?["ifUnsaved"];
            if (!HasValue(token))
            {
                return UnsavedError;
            }
            var action = GetString(token, "ifUnsaved");
            if (action != UnsavedError && action != UnsavedSave && action != UnsavedDiscard)
            {
                throw new CommandException(ErrorCodes.InvalidParams, "ifUnsaved 只能是 error / save / discard 之一。");
            }
            return action;
        }

        private static int GetBuildIndex(JToken token)
        {
            if (token.Type != JTokenType.Integer)
            {
                throw new CommandException(ErrorCodes.InvalidParams, "buildIndex 必须是 integer。");
            }
            var value = token.Value<long>();
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
                throw new CommandException(ErrorCodes.InvalidParams, name + " 必须是 boolean。");
            }
            return token.Value<bool>();
        }

        private static string GetString(JToken token, string name)
        {
            if (token.Type != JTokenType.String)
            {
                throw new CommandException(ErrorCodes.InvalidParams, name + " 必须是 string。");
            }
            return token.Value<string>();
        }

        private static bool HasValue(JToken token)
        {
            return token != null && token.Type != JTokenType.Null;
        }

        public JObject GetParamsSchema()
        {
            return JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""scenePath"": { ""type"": ""string"", ""description"": ""Assets/ 下的 .unity 场景路径,例如 Assets/Scenes/Main.unity;与 buildIndex 二选一。"" },
    ""buildIndex"": { ""type"": ""integer"", ""minimum"": 0, ""description"": ""Build Settings 中已启用场景列表的运行时 build index;与 scenePath 二选一。"" },
    ""requireInBuildSettings"": { ""type"": ""boolean"", ""description"": ""scenePath 是否必须存在于 Build Settings 且已启用,默认 false。"" },
    ""ifUnsaved"": { ""type"": ""string"", ""enum"": [""error"", ""save"", ""discard""], ""description"": ""切换场景前如何处理未保存场景,默认 error。"" }
  }
}");
        }

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

        private sealed class UnsavedResult
        {
            public UnsavedResult(string[] dirtyScenes, string[] savedScenes, string[] discardedScenes)
            {
                DirtyScenes = dirtyScenes;
                SavedScenes = savedScenes;
                DiscardedScenes = discardedScenes;
            }

            public string[] DirtyScenes { get; }
            public string[] SavedScenes { get; }
            public string[] DiscardedScenes { get; }
        }
    }
}
