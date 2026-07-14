using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace AgentBridge
{
    /// <summary>
    /// 通过 Unity Editor 内部 GameView API 设置 Game View 固定分辨率。
    /// Unity 未公开稳定 API,这里集中做反射和版本兼容处理。
    /// </summary>
    internal static class GameViewResolutionUtility
    {
        public const string UnavailableError = "GAME_VIEW_RESOLUTION_UNAVAILABLE";
        public const string FailedError = "GAME_VIEW_RESOLUTION_FAILED";

        private static bool s_Init;
        private static Type s_GameViewType;
        private static Type s_GameViewSizesType;
        private static Type s_GameViewSizeType;
        private static Type s_GameViewSizeGroupType;
        private static Type s_GameViewSizeTypeEnum;
        private static Type s_GameViewSizeGroupTypeEnum;
        private static PropertyInfo s_GameViewSizesInstance;
        private static MethodInfo s_GetGroup;
        private static MethodInfo s_AddCustomSize;
        private static MethodInfo s_RemoveCustomSize;
        private static MethodInfo s_GetBuiltinCount;
        private static MethodInfo s_GetCustomCount;
        private static MethodInfo s_GetGameViewSize;
        private static MethodInfo s_SaveToHDD;
        private static PropertyInfo s_SelectedSizeIndex;
        private static PropertyInfo s_GameViewCurrentSizeGroupType;
        private static ConstructorInfo s_GameViewSizeConstructor;
        private static PropertyInfo s_SizeTypeProperty;
        private static PropertyInfo s_WidthProperty;
        private static PropertyInfo s_HeightProperty;
        private static PropertyInfo s_BaseTextProperty;
        private static object s_FixedResolutionValue;

        public readonly struct Result
        {
            public Result(
                int width,
                int height,
                string label,
                int selectedIndex,
                bool created,
                string group,
                RestoreToken restore)
            {
                Width = width;
                Height = height;
                Label = label;
                SelectedIndex = selectedIndex;
                Created = created;
                Group = group;
                Restore = restore;
            }

            public int Width { get; }
            public int Height { get; }
            public string Label { get; }
            public int SelectedIndex { get; }
            public bool Created { get; }
            public string Group { get; }
            public RestoreToken Restore { get; }
        }

        public readonly struct RestoreToken
        {
            public RestoreToken(
                int selectedIndex,
                bool removeCreated,
                int width,
                int height,
                string label,
                string group)
            {
                SelectedIndex = selectedIndex;
                RemoveCreated = removeCreated;
                Width = width;
                Height = height;
                Label = label;
                Group = group;
            }

            public int SelectedIndex { get; }
            public bool RemoveCreated { get; }
            public int Width { get; }
            public int Height { get; }
            public string Label { get; }
            public string Group { get; }
        }

        public readonly struct RestoreResult
        {
            public RestoreResult(
                int selectedIndex,
                int width,
                int height,
                string label,
                bool removedCreated,
                string group)
            {
                SelectedIndex = selectedIndex;
                Width = width;
                Height = height;
                Label = label;
                RemovedCreated = removedCreated;
                Group = group;
            }

            public int SelectedIndex { get; }
            public int Width { get; }
            public int Height { get; }
            public string Label { get; }
            public bool RemovedCreated { get; }
            public string Group { get; }
        }

        public static Result SetFixedResolution(int width, int height, string label)
        {
            if (Application.isBatchMode)
            {
                throw new CommandException(UnavailableError, "BatchMode 下无法访问 Game View。");
            }
            bool ready;
            try
            {
                ready = EnsureReflection();
            }
            catch (Exception ex)
            {
                s_GameViewType = null;
                throw new CommandException(UnavailableError,
                    "无法访问编辑器 Game View 分辨率 API(内部类型或成员反射失败): " + ex.Message);
            }
            if (!ready)
            {
                throw new CommandException(UnavailableError,
                    "无法访问编辑器 Game View 分辨率 API(内部类型或成员反射失败,可能是 Unity 版本不兼容)。");
            }

            object rollbackSizes = null;
            object rollbackGroup = null;
            EditorWindow rollbackGameView = null;
            var rollbackSelectedIndex = -1;
            var rollbackCustomIndex = -1;
            try
            {
                var sizes = s_GameViewSizesInstance.GetValue(null, null);
                rollbackSizes = sizes;
                if (sizes == null)
                {
                    throw new CommandException(UnavailableError, "无法访问 GameViewSizes.instance。");
                }

                var gameView = GetGameView();
                rollbackGameView = gameView;
                rollbackSelectedIndex = Convert.ToInt32(s_SelectedSizeIndex.GetValue(gameView, null));
                var groupType = GetCurrentSizeGroupType(gameView);
                var group = s_GetGroup.Invoke(sizes, new[] { groupType });
                rollbackGroup = group;
                if (group == null)
                {
                    throw new CommandException(UnavailableError, "无法访问当前 Game View 尺寸组。");
                }

                var index = FindFixedSize(group, width, height);
                var created = false;
                if (index < 0)
                {
                    // RemoveCustomSize expects an index inside the custom-size list, not the
                    // combined built-in + custom selectedSizeIndex.
                    rollbackCustomIndex = CustomSizeCount(group);
                    var size = s_GameViewSizeConstructor.Invoke(new[] { s_FixedResolutionValue, width, height, label });
                    s_AddCustomSize.Invoke(group, new[] { size });
                    s_SaveToHDD.Invoke(sizes, null);
                    index = FindFixedSize(group, width, height);
                    if (index < 0)
                    {
                        throw new CommandException(FailedError, $"已添加 {width}x{height},但无法在 Game View 尺寸列表中找到它。");
                    }
                    created = true;
                }

                s_SelectedSizeIndex.SetValue(gameView, index, null);
                ((EditorWindow)gameView).Repaint();
                EditorApplication.QueuePlayerLoopUpdate();

                var selectedIndex = Convert.ToInt32(s_SelectedSizeIndex.GetValue(gameView, null));
                if (selectedIndex != index)
                {
                    throw new CommandException(FailedError, $"设置 Game View 分辨率失败:selectedSizeIndex={selectedIndex},期望 {index}。");
                }

                var selectedSize = GetSize(group, selectedIndex);
                var selectedInfo = ReadSize(selectedSize);
                if (!selectedInfo.IsFixedResolution || selectedInfo.Width != width || selectedInfo.Height != height)
                {
                    throw new CommandException(FailedError,
                        $"设置 Game View 分辨率后验证失败:当前为 {selectedInfo.Width}x{selectedInfo.Height}。");
                }

                return new Result(
                    width,
                    height,
                    selectedInfo.Label,
                    selectedIndex,
                    created,
                    groupType.ToString(),
                    new RestoreToken(
                        rollbackSelectedIndex,
                        created,
                        width,
                        height,
                        selectedInfo.Label,
                        groupType.ToString()));
            }
            catch (CommandException)
            {
                RollbackSelectionAndCustomSize(rollbackGameView, rollbackSelectedIndex,
                    rollbackSizes, rollbackGroup, rollbackCustomIndex);
                throw;
            }
            catch (TargetInvocationException ex)
            {
                RollbackSelectionAndCustomSize(rollbackGameView, rollbackSelectedIndex,
                    rollbackSizes, rollbackGroup, rollbackCustomIndex);
                throw new CommandException(FailedError, "设置 Game View 分辨率失败: " + (ex.InnerException?.Message ?? ex.Message));
            }
            catch (Exception ex)
            {
                RollbackSelectionAndCustomSize(rollbackGameView, rollbackSelectedIndex,
                    rollbackSizes, rollbackGroup, rollbackCustomIndex);
                throw new CommandException(FailedError, "设置 Game View 分辨率失败: " + ex.Message);
            }
        }

        private static bool EnsureReflection()
        {
            if (s_Init)
            {
                return s_GameViewType != null;
            }
            s_Init = true;

            var editorAssembly = typeof(Editor).Assembly;
            s_GameViewType = editorAssembly.GetType("UnityEditor.GameView");
            s_GameViewSizesType = editorAssembly.GetType("UnityEditor.GameViewSizes");
            s_GameViewSizeType = editorAssembly.GetType("UnityEditor.GameViewSize");
            s_GameViewSizeGroupType = editorAssembly.GetType("UnityEditor.GameViewSizeGroup");
            s_GameViewSizeTypeEnum = editorAssembly.GetType("UnityEditor.GameViewSizeType");
            s_GameViewSizeGroupTypeEnum = editorAssembly.GetType("UnityEditor.GameViewSizeGroupType");
            if (s_GameViewType == null || s_GameViewSizesType == null || s_GameViewSizeType == null ||
                s_GameViewSizeGroupType == null || s_GameViewSizeTypeEnum == null || s_GameViewSizeGroupTypeEnum == null)
            {
                s_GameViewType = null;
                return false;
            }

            var scriptableSingletonType = typeof(ScriptableSingleton<>).MakeGenericType(s_GameViewSizesType);
            const BindingFlags StaticFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            const BindingFlags InstanceFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            s_GameViewSizesInstance = scriptableSingletonType.GetProperty("instance", StaticFlags);
            s_GetGroup = s_GameViewSizesType.GetMethod("GetGroup", InstanceFlags, null, new[] { s_GameViewSizeGroupTypeEnum }, null);
            s_SaveToHDD = s_GameViewSizesType.GetMethod("SaveToHDD", InstanceFlags);
            s_AddCustomSize = s_GameViewSizeGroupType.GetMethod("AddCustomSize", InstanceFlags, null, new[] { s_GameViewSizeType }, null);
            s_RemoveCustomSize = s_GameViewSizeGroupType.GetMethod("RemoveCustomSize", InstanceFlags, null, new[] { typeof(int) }, null);
            s_GetBuiltinCount = s_GameViewSizeGroupType.GetMethod("GetBuiltinCount", InstanceFlags);
            s_GetCustomCount = s_GameViewSizeGroupType.GetMethod("GetCustomCount", InstanceFlags);
            s_GetGameViewSize = s_GameViewSizeGroupType.GetMethod("GetGameViewSize", InstanceFlags, null, new[] { typeof(int) }, null);
            s_SelectedSizeIndex = s_GameViewType.GetProperty("selectedSizeIndex", InstanceFlags);
            // Unity 6000.2+ changed currentSizeGroupType from an instance property to a
            // static property. Support both shapes so 2021.3 through Unity 6 remain valid.
            s_GameViewCurrentSizeGroupType =
                s_GameViewType.GetProperty("currentSizeGroupType", InstanceFlags) ??
                s_GameViewType.GetProperty("currentSizeGroupType", StaticFlags);
            s_GameViewSizeConstructor = s_GameViewSizeType.GetConstructor(InstanceFlags, null,
                new[] { s_GameViewSizeTypeEnum, typeof(int), typeof(int), typeof(string) }, null);

            s_SizeTypeProperty = s_GameViewSizeType.GetProperty("sizeType", InstanceFlags);
            s_WidthProperty = s_GameViewSizeType.GetProperty("width", InstanceFlags);
            s_HeightProperty = s_GameViewSizeType.GetProperty("height", InstanceFlags);
            s_BaseTextProperty = s_GameViewSizeType.GetProperty("baseText", InstanceFlags);
            s_FixedResolutionValue = Enum.Parse(s_GameViewSizeTypeEnum, "FixedResolution");

            if (s_GameViewSizesInstance == null || s_GetGroup == null || s_AddCustomSize == null || s_RemoveCustomSize == null ||
                s_SaveToHDD == null ||
                s_GetBuiltinCount == null || s_GetCustomCount == null || s_GetGameViewSize == null ||
                s_SelectedSizeIndex == null || s_GameViewCurrentSizeGroupType == null ||
                s_GameViewSizeConstructor == null || s_SizeTypeProperty == null ||
                s_WidthProperty == null || s_HeightProperty == null || s_BaseTextProperty == null)
            {
                s_GameViewType = null;
                return false;
            }

            return true;
        }

        private static void RollbackSelectionAndCustomSize(
            EditorWindow gameView,
            int selectedIndex,
            object sizes,
            object group,
            int customIndex)
        {
            try
            {
                if (gameView != null && selectedIndex >= 0 && s_SelectedSizeIndex != null)
                {
                    s_SelectedSizeIndex.SetValue(gameView, selectedIndex, null);
                    gameView.Repaint();
                }
                if (sizes != null && group != null && customIndex >= 0 && s_RemoveCustomSize != null)
                {
                    s_RemoveCustomSize.Invoke(group, new object[] { customIndex });
                    s_SaveToHDD?.Invoke(sizes, null);
                }
            }
            catch (Exception)
            {
                // Preserve the original command failure. A detailed compatibility error is
                // more actionable than replacing it with a cleanup exception.
            }
        }

        public static RestoreResult RestoreFixedResolution(RestoreToken token)
        {
            if (Application.isBatchMode)
            {
                throw new CommandException(UnavailableError, "BatchMode 下无法访问 Game View。");
            }
            try
            {
                if (!EnsureReflection())
                {
                    throw new CommandException(UnavailableError,
                        "无法访问编辑器 Game View 分辨率 API(内部类型或成员反射失败,可能是 Unity 版本不兼容)。");
                }

                var sizes = s_GameViewSizesInstance.GetValue(null, null);
                var gameView = GetGameView();
                var groupType = GetCurrentSizeGroupType(gameView);
                var groupName = groupType.ToString();
                if (!string.Equals(groupName, token.Group, StringComparison.Ordinal))
                {
                    throw new CommandException(FailedError,
                        $"Game View 尺寸组已从 {token.Group} 切换为 {groupName},拒绝使用过期 restore token。");
                }
                var group = s_GetGroup.Invoke(sizes, new[] { groupType });
                var sizeCount = SizeCount(group);
                if (token.SelectedIndex < 0 || token.SelectedIndex >= sizeCount)
                {
                    throw new CommandException(FailedError,
                        $"restore selectedIndex={token.SelectedIndex} 超出当前范围 0..{Math.Max(0, sizeCount - 1)}。");
                }

                s_SelectedSizeIndex.SetValue(gameView, token.SelectedIndex, null);
                var removedCreated = false;
                if (token.RemoveCreated)
                {
                    var customIndex = FindCustomSize(
                        group, token.Width, token.Height, token.Label);
                    if (customIndex >= 0)
                    {
                        s_RemoveCustomSize.Invoke(group, new object[] { customIndex });
                        s_SaveToHDD.Invoke(sizes, null);
                        removedCreated = true;
                    }
                }

                gameView.Repaint();
                EditorApplication.QueuePlayerLoopUpdate();
                var restoredIndex = Convert.ToInt32(s_SelectedSizeIndex.GetValue(gameView, null));
                if (restoredIndex != token.SelectedIndex)
                {
                    throw new CommandException(FailedError,
                        $"恢复 Game View 选择失败:selectedSizeIndex={restoredIndex},期望 {token.SelectedIndex}。");
                }
                var restored = ReadSize(GetSize(group, restoredIndex));
                return new RestoreResult(
                    restoredIndex,
                    restored.Width,
                    restored.Height,
                    restored.Label,
                    removedCreated,
                    groupName);
            }
            catch (CommandException)
            {
                throw;
            }
            catch (TargetInvocationException ex)
            {
                throw new CommandException(FailedError,
                    "恢复 Game View 分辨率失败: " + (ex.InnerException?.Message ?? ex.Message));
            }
            catch (Exception ex)
            {
                throw new CommandException(FailedError, "恢复 Game View 分辨率失败: " + ex.Message);
            }
        }

        private static object GetCurrentSizeGroupType(EditorWindow gameView)
        {
            var getter = s_GameViewCurrentSizeGroupType.GetGetMethod(true);
            return s_GameViewCurrentSizeGroupType.GetValue(getter != null && getter.IsStatic ? null : gameView, null);
        }

        private static EditorWindow GetGameView()
        {
            var existing = Resources.FindObjectsOfTypeAll(s_GameViewType);
            if (existing != null && existing.Length > 0)
            {
                return (EditorWindow)existing[0];
            }
            return EditorWindow.GetWindow(s_GameViewType);
        }

        private static int FindFixedSize(object group, int width, int height)
        {
            var count = SizeCount(group);
            for (var i = 0; i < count; i++)
            {
                if (IsMatchingFixedSize(GetSize(group, i), width, height))
                {
                    return i;
                }
            }
            return -1;
        }

        private static int FindCustomSize(
            object group,
            int width,
            int height,
            string label)
        {
            var builtinCount = Convert.ToInt32(s_GetBuiltinCount.Invoke(group, null));
            var customCount = CustomSizeCount(group);
            for (var customIndex = 0; customIndex < customCount; customIndex++)
            {
                var info = ReadSize(GetSize(group, builtinCount + customIndex));
                if (info.IsFixedResolution && info.Width == width && info.Height == height &&
                    string.Equals(info.Label, label, StringComparison.Ordinal))
                {
                    return customIndex;
                }
            }
            return -1;
        }

        private static int SizeCount(object group)
        {
            var builtin = Convert.ToInt32(s_GetBuiltinCount.Invoke(group, null));
            var custom = CustomSizeCount(group);
            return builtin + custom;
        }

        private static int CustomSizeCount(object group)
        {
            return Convert.ToInt32(s_GetCustomCount.Invoke(group, null));
        }

        private static object GetSize(object group, int index)
        {
            return s_GetGameViewSize.Invoke(group, new object[] { index });
        }

        private static bool IsMatchingFixedSize(object size, int width, int height)
        {
            var info = ReadSize(size);
            return info.IsFixedResolution && info.Width == width && info.Height == height;
        }

        private static SizeInfo ReadSize(object size)
        {
            var sizeType = s_SizeTypeProperty.GetValue(size, null);
            var width = Convert.ToInt32(s_WidthProperty.GetValue(size, null));
            var height = Convert.ToInt32(s_HeightProperty.GetValue(size, null));
            var label = s_BaseTextProperty.GetValue(size, null) as string ?? "";
            return new SizeInfo(Equals(sizeType, s_FixedResolutionValue), width, height, label);
        }

        private readonly struct SizeInfo
        {
            public SizeInfo(bool isFixedResolution, int width, int height, string label)
            {
                IsFixedResolution = isFixedResolution;
                Width = width;
                Height = height;
                Label = label;
            }

            public bool IsFixedResolution { get; }
            public int Width { get; }
            public int Height { get; }
            public string Label { get; }
        }
    }
}
