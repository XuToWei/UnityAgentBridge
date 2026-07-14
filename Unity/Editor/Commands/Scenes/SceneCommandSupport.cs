using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AgentBridge
{
    internal static class SceneCommandErrorCodes
    {
        public const string SceneNotLoaded = "SCENE_NOT_LOADED";
        public const string SceneAlreadyLoaded = "SCENE_ALREADY_LOADED";
        public const string SceneOpenFailed = "SCENE_OPEN_FAILED";
        public const string SceneCloseFailed = "SCENE_CLOSE_FAILED";
        public const string SceneSaveFailed = "SCENE_SAVE_FAILED";
        public const string SceneSetActiveFailed = "SCENE_SET_ACTIVE_FAILED";
        public const string UnsavedScenes = "UNSAVED_SCENES";
        public const string LastScene = "LAST_SCENE";
        public const string EditModeRequired = "EDIT_MODE_REQUIRED";
    }

    internal enum SceneUnsavedOperation
    {
        OpenSingle,
        OpenSingleKeepingTarget,
        CloseScene,
        PlaySceneSwitch,
        PlaySceneAlreadyOpen,
        RunTests,
        SaveCommand
    }

    internal sealed class SceneUnsavedResult
    {
        public SceneUnsavedResult(
            string action,
            string[] dirtyScenes,
            string[] savedScenes,
            string[] discardedScenes)
        {
            Action = action;
            DirtyScenes = dirtyScenes;
            SavedScenes = savedScenes;
            DiscardedScenes = discardedScenes;
        }

        public string Action { get; }
        public string[] DirtyScenes { get; }
        public string[] SavedScenes { get; }
        public string[] DiscardedScenes { get; }
    }

    internal static class SceneCommandSupport
    {
        internal const string IfUnsavedError = "error";
        internal const string IfUnsavedSave = "save";
        internal const string IfUnsavedDiscard = "discard";

        public static void RequireEditMode(string command)
        {
            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                throw new CommandException(SceneCommandErrorCodes.EditModeRequired,
                    command + " 只能在稳定的 EditMode 执行");
            }
        }

        public static Scene ResolveLoadedScene(JObject @params, bool selectorRequired = false)
        {
            var pathToken = @params?["scenePath"];
            var handleToken = @params?["sceneHandle"];
            var hasPath = pathToken != null && pathToken.Type != JTokenType.Null;
            var hasHandle = handleToken != null && handleToken.Type != JTokenType.Null;
            if (hasPath && hasHandle)
            {
                throw new CommandException(ErrorCodes.InvalidParams,
                    "scenePath 与 sceneHandle 只能提供一个");
            }
            if (!hasPath && !hasHandle)
            {
                if (selectorRequired)
                {
                    throw new CommandException(ErrorCodes.InvalidParams,
                        "缺 scenePath 或 sceneHandle");
                }
                var active = SceneManager.GetActiveScene();
                if (!active.IsValid() || !active.isLoaded)
                {
                    throw new CommandException(SceneCommandErrorCodes.SceneNotLoaded,
                        "当前没有有效的 active scene");
                }
                return active;
            }

            if (hasHandle)
            {
                var handle = ReadInt(handleToken, "sceneHandle");
                var scene = default(Scene);
                for (var i = 0; i < SceneManager.sceneCount; i++)
                {
                    var candidate = SceneManager.GetSceneAt(i);
                    if (candidate.handle == handle)
                    {
                        scene = candidate;
                        break;
                    }
                }
                if (!scene.IsValid() || !scene.isLoaded)
                {
                    throw new CommandException(SceneCommandErrorCodes.SceneNotLoaded,
                        $"未找到已加载 sceneHandle={handle} 的场景");
                }
                return scene;
            }

            var path = pathToken.Value<string>();
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new CommandException(ErrorCodes.InvalidParams, "scenePath 不能为空白");
            }
            var normalized = path.Replace('\\', '/');
            var matches = new List<Scene>();
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded && string.Equals(Normalize(scene.path), normalized, PathComparison))
                {
                    matches.Add(scene);
                }
            }
            if (matches.Count == 0)
            {
                throw new CommandException(SceneCommandErrorCodes.SceneNotLoaded,
                    $"场景未加载:'{path}'");
            }
            return matches[0];
        }

        public static Scene FindLoadedScene(string path)
        {
            var normalized = Normalize(path);
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded && string.Equals(Normalize(scene.path), normalized, PathComparison))
                {
                    return scene;
                }
            }
            return default(Scene);
        }

        public static string RequireSceneAssetPath(string path, string field = "scenePath", bool mustExist = true)
        {
            var normalized = AssetSupport.RequireProjectPath(path, field);
            if (!normalized.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
            {
                throw new CommandException(PlayModeErrorCodes.InvalidScenePath,
                    field + " 必须是 Assets/ 下的 .unity 文件");
            }
            if (mustExist && AssetDatabase.LoadAssetAtPath<SceneAsset>(normalized) == null)
            {
                throw new CommandException(PlayModeErrorCodes.SceneNotFound,
                    $"场景资产不存在:'{normalized}'");
            }
            return normalized;
        }

        public static void RequireSaveFolder(string scenePath)
        {
            var folder = Path.GetDirectoryName(scenePath)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(folder) || !AssetDatabase.IsValidFolder(folder))
            {
                throw new CommandException(SceneCommandErrorCodes.SceneSaveFailed,
                    $"保存目录不存在:'{folder}'");
            }
        }

        public static List<Scene> DirtyScenes()
        {
            var result = new List<Scene>();
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded && scene.isDirty)
                {
                    result.Add(scene);
                }
            }
            return result;
        }

        public static SceneUnsavedResult HandleUnsavedScenes(
            JObject @params,
            SceneUnsavedOperation operation,
            IEnumerable<Scene> scenes = null)
        {
            return HandleUnsavedScenes(@params?["ifUnsaved"], operation, scenes);
        }

        public static string ReadIfUnsaved(
            JObject @params,
            SceneUnsavedOperation operation)
        {
            return ReadIfUnsaved(@params?["ifUnsaved"], operation);
        }

        public static SceneUnsavedResult HandleUnsavedScenes(
            string action,
            SceneUnsavedOperation operation,
            IEnumerable<Scene> scenes = null)
        {
            return HandleUnsavedScenes(
                action == null ? null : new JValue(action),
                operation,
                scenes);
        }

        private static SceneUnsavedResult HandleUnsavedScenes(
            JToken actionToken,
            SceneUnsavedOperation operation,
            IEnumerable<Scene> scenes)
        {
            var action = ReadIfUnsaved(actionToken, operation);
            var dirty = (scenes ?? DirtyScenes())
                .Where(scene => scene.IsValid() && scene.isLoaded && scene.isDirty)
                .GroupBy(GetHandle)
                .Select(group => group.First())
                .ToArray();
            var dirtyLabels = dirty.Select(scene => UnsavedLabel(scene, operation)).ToArray();

            // Running the current/already-open scene does not switch scenes, so its dirty
            // state is reported but the requested policy intentionally is not applied.
            if (operation == SceneUnsavedOperation.PlaySceneAlreadyOpen || dirty.Length == 0)
            {
                return Result(action, dirtyLabels);
            }

            if (action == IfUnsavedError)
            {
                throw new CommandException(SceneCommandErrorCodes.UnsavedScenes,
                    UnsavedErrorMessage(operation, dirtyLabels));
            }
            if (action == IfUnsavedSave)
            {
                return Result(action, dirtyLabels, SaveDirtyScenes(dirty, operation));
            }

            return Result(action, dirtyLabels, discardedScenes: dirtyLabels);
        }

        public static string Label(Scene scene)
        {
            return string.IsNullOrEmpty(scene.path)
                ? $"{scene.name}(handle={GetHandle(scene)})"
                : scene.path;
        }

        public static int GetHandle(Scene scene)
        {
            // Unity 6 exposes Scene.handle as SceneHandle with implicit int conversion;
            // older supported versions expose int directly. The explicit cast keeps JSON stable.
            return (int)scene.handle;
        }

        public static string[] SaveDirtyScenes(IEnumerable<Scene> scenes)
        {
            return SaveDirtyScenes(scenes, SceneUnsavedOperation.SaveCommand);
        }

        private static string[] SaveDirtyScenes(
            IEnumerable<Scene> scenes,
            SceneUnsavedOperation operation)
        {
            var dirty = scenes.Where(scene => scene.IsValid() && scene.isLoaded && scene.isDirty).ToArray();
            foreach (var scene in dirty)
            {
                if (string.IsNullOrEmpty(scene.path))
                {
                    throw new CommandException(SceneCommandErrorCodes.SceneSaveFailed,
                        UnnamedSceneSaveMessage(scene, operation));
                }
            }

            if (operation == SceneUnsavedOperation.PlaySceneSwitch)
            {
                // Save exactly the preflighted dirty set. SaveOpenScenes also touches clean,
                // unnamed additive scenes and can open an interactive Save As dialog.
                if (!EditorSceneManager.SaveScenes(dirty))
                {
                    throw new CommandException(SceneCommandErrorCodes.SceneSaveFailed,
                        "未能保存全部 dirty 场景;场景切换已中止");
                }
                return dirty.Select(scene => scene.path).ToArray();
            }

            var saved = new List<string>();
            foreach (var scene in dirty)
            {
                if (!EditorSceneManager.SaveScene(scene))
                {
                    throw new CommandException(SceneCommandErrorCodes.SceneSaveFailed,
                        $"保存场景失败:'{Label(scene)}'");
                }
                saved.Add(scene.path);
            }
            return saved.ToArray();
        }

        private static string UnsavedLabel(Scene scene, SceneUnsavedOperation operation)
        {
            // Preserve play_scene's existing wire representation for unnamed scenes.
            if ((operation == SceneUnsavedOperation.PlaySceneSwitch ||
                 operation == SceneUnsavedOperation.PlaySceneAlreadyOpen) &&
                string.IsNullOrEmpty(scene.path))
            {
                return scene.name;
            }
            return Label(scene);
        }

        private static string ReadIfUnsaved(JToken token, SceneUnsavedOperation operation)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return IfUnsavedError;
            }
            if (token.Type != JTokenType.String)
            {
                throw new CommandException(ErrorCodes.InvalidParams,
                    InvalidIfUnsavedMessage(operation));
            }

            var value = token.Value<string>();
            var allowsDiscard = operation != SceneUnsavedOperation.RunTests;
            if (value != IfUnsavedError && value != IfUnsavedSave &&
                (!allowsDiscard || value != IfUnsavedDiscard))
            {
                throw new CommandException(ErrorCodes.InvalidParams,
                    InvalidIfUnsavedMessage(operation));
            }
            return value;
        }

        private static string InvalidIfUnsavedMessage(SceneUnsavedOperation operation)
        {
            if (operation == SceneUnsavedOperation.RunTests)
            {
                return "ifUnsaved 只能是 error / save";
            }
            if (operation == SceneUnsavedOperation.PlaySceneSwitch ||
                operation == SceneUnsavedOperation.PlaySceneAlreadyOpen)
            {
                return "ifUnsaved 只能是 error / save / discard 之一。";
            }
            return "ifUnsaved 只能是 error / save / discard";
        }

        private static string UnsavedErrorMessage(
            SceneUnsavedOperation operation,
            string[] dirtyLabels)
        {
            var labels = string.Join(", ", dirtyLabels);
            switch (operation)
            {
                case SceneUnsavedOperation.OpenSingleKeepingTarget:
                    return "其它已加载场景有未保存修改:" + labels;
                case SceneUnsavedOperation.CloseScene:
                    return $"场景有未保存修改:'{labels}'";
                case SceneUnsavedOperation.PlaySceneSwitch:
                    return "当前打开场景有未保存修改,默认不会切换场景:" + labels;
                case SceneUnsavedOperation.RunTests:
                    return "当前场景有未保存修改,run_tests 默认拒绝启动:" + labels;
                default:
                    return "当前场景有未保存修改:" + labels;
            }
        }

        private static string UnnamedSceneSaveMessage(
            Scene scene,
            SceneUnsavedOperation operation)
        {
            switch (operation)
            {
                case SceneUnsavedOperation.PlaySceneSwitch:
                    return $"未命名场景 '{scene.name}' 无法非交互保存,请先手动保存或传 ifUnsaved=discard。";
                case SceneUnsavedOperation.RunTests:
                    return $"未命名场景 '{scene.name}' 无法非交互保存;请先使用 save_scene.saveAs";
                case SceneUnsavedOperation.SaveCommand:
                    return $"未命名场景 '{scene.name}' 无法通过 all=true 非交互保存;" +
                           $"请单独调用 save_scene 并传 sceneHandle={GetHandle(scene)} 与 saveAs=Assets/.../*.unity";
                default:
                    return $"未命名场景 '{scene.name}' 无法非交互保存;请先使用 save_scene.saveAs 或选择 discard";
            }
        }

        private static SceneUnsavedResult Result(
            string action,
            string[] dirtyScenes,
            string[] savedScenes = null,
            string[] discardedScenes = null)
        {
            return new SceneUnsavedResult(
                action,
                dirtyScenes,
                savedScenes ?? Array.Empty<string>(),
                discardedScenes ?? Array.Empty<string>());
        }

        public static object Describe(Scene scene)
        {
            var active = SceneManager.GetActiveScene();
            var buildInfo = GetBuildInfo(scene.path);
            return new
            {
                name = scene.name,
                path = scene.path,
                handle = GetHandle(scene),
                loaded = scene.isLoaded,
                dirty = scene.isDirty,
                active = active.IsValid() && scene.handle == active.handle,
                rootCount = scene.isLoaded ? scene.rootCount : 0,
                buildSettings = buildInfo
            };
        }

        private static object GetBuildInfo(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return new { present = false, enabled = false, index = -1 };
            }
            var enabledIndex = 0;
            foreach (var entry in EditorBuildSettings.scenes)
            {
                if (string.Equals(Normalize(entry.path), Normalize(path), PathComparison))
                {
                    return new
                    {
                        present = true,
                        enabled = entry.enabled,
                        index = entry.enabled ? enabledIndex : -1
                    };
                }
                if (entry.enabled)
                {
                    enabledIndex++;
                }
            }
            return new { present = false, enabled = false, index = -1 };
        }

        public static bool ReadBool(JObject @params, string name, bool defaultValue)
        {
            var token = @params?[name];
            return token == null || token.Type == JTokenType.Null ? defaultValue : token.Value<bool>();
        }

        private static int ReadInt(JToken token, string name)
        {
            try
            {
                var value = token.Value<long>();
                if (value < int.MinValue || value > int.MaxValue)
                {
                    throw new OverflowException();
                }
                return (int)value;
            }
            catch (Exception)
            {
                throw new CommandException(ErrorCodes.InvalidParams,
                    name + " 必须是 32 位 integer");
            }
        }

        private static string Normalize(string path)
        {
            return (path ?? "").Replace('\\', '/');
        }

        public static bool PathsEqual(string left, string right)
        {
            return string.Equals(Normalize(left), Normalize(right), PathComparison);
        }

        private static StringComparison PathComparison =>
            Application.platform == RuntimePlatform.WindowsEditor ||
            Application.platform == RuntimePlatform.OSXEditor
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

        public static JObject SceneSelectorSchema(bool required = false)
        {
            var schema = JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""scenePath"": { ""type"": ""string"" },
    ""sceneHandle"": { ""type"": ""integer"", ""minimum"": -2147483648, ""maximum"": 2147483647 }
  }
}");
            if (required)
            {
                schema["anyOf"] = new JArray(
                    new JObject { ["required"] = new JArray("scenePath") },
                    new JObject { ["required"] = new JArray("sceneHandle") });
            }
            return schema;
        }
    }
}
