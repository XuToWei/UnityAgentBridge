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

        private const string DefaultGroupName = "Standalone";

        private static bool s_Init;
        private static Type s_GameViewType;
        private static Type s_GameViewSizesType;
        private static Type s_GameViewSizeType;
        private static Type s_GameViewSizeGroupType;
        private static Type s_GameViewSizeTypeEnum;
        private static Type s_GameViewSizeGroupTypeEnum;
        private static Type s_ScriptableSingletonType;
        private static PropertyInfo s_GameViewSizesInstance;
        private static MethodInfo s_GetGroup;
        private static MethodInfo s_AddCustomSize;
        private static MethodInfo s_FindSize;
        private static MethodInfo s_GetBuiltinCount;
        private static MethodInfo s_GetCustomCount;
        private static MethodInfo s_GetGameViewSize;
        private static MethodInfo s_SaveToHDD;
        private static PropertyInfo s_SelectedSizeIndex;
        private static PropertyInfo s_GameViewCurrentSizeGroupType;
        private static PropertyInfo s_GameViewSizesCurrentGroupType;
        private static ConstructorInfo s_GameViewSizeConstructor;
        private static PropertyInfo s_SizeTypeProperty;
        private static PropertyInfo s_WidthProperty;
        private static PropertyInfo s_HeightProperty;
        private static PropertyInfo s_BaseTextProperty;
        private static FieldInfo s_SizeTypeField;
        private static FieldInfo s_WidthField;
        private static FieldInfo s_HeightField;
        private static FieldInfo s_BaseTextField;
        private static object s_FixedResolutionValue;
        private static object s_DefaultGroupValue;

        public readonly struct Result
        {
            public Result(int width, int height, string label, int selectedIndex, bool created, string group)
            {
                Width = width;
                Height = height;
                Label = label;
                SelectedIndex = selectedIndex;
                Created = created;
                Group = group;
            }

            public int Width { get; }
            public int Height { get; }
            public string Label { get; }
            public int SelectedIndex { get; }
            public bool Created { get; }
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

            try
            {
                var sizes = s_GameViewSizesInstance.GetValue(null, null);
                if (sizes == null)
                {
                    throw new CommandException(UnavailableError, "无法访问 GameViewSizes.instance。");
                }

                var gameView = GetGameView();
                var groupType = ResolveGroupType(gameView, sizes);
                var group = s_GetGroup.Invoke(sizes, new[] { groupType });
                if (group == null)
                {
                    throw new CommandException(UnavailableError, "无法访问当前 Game View 尺寸组。");
                }

                var index = FindFixedSize(group, width, height);
                var created = false;
                if (index < 0)
                {
                    var size = s_GameViewSizeConstructor.Invoke(new[] { s_FixedResolutionValue, width, height, label });
                    s_AddCustomSize.Invoke(group, new[] { size });
                    s_SaveToHDD?.Invoke(sizes, null);
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

                return new Result(width, height, selectedInfo.Label, selectedIndex, created, GroupName(groupType));
            }
            catch (CommandException)
            {
                throw;
            }
            catch (TargetInvocationException ex)
            {
                throw new CommandException(FailedError, "设置 Game View 分辨率失败: " + (ex.InnerException?.Message ?? ex.Message));
            }
            catch (Exception ex)
            {
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

            s_ScriptableSingletonType = typeof(ScriptableSingleton<>).MakeGenericType(s_GameViewSizesType);
            const BindingFlags StaticFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            const BindingFlags InstanceFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            s_GameViewSizesInstance = s_ScriptableSingletonType.GetProperty("instance", StaticFlags);
            s_GetGroup = s_GameViewSizesType.GetMethod("GetGroup", InstanceFlags, null, new[] { s_GameViewSizeGroupTypeEnum }, null);
            s_SaveToHDD = s_GameViewSizesType.GetMethod("SaveToHDD", InstanceFlags);
            s_AddCustomSize = s_GameViewSizeGroupType.GetMethod("AddCustomSize", InstanceFlags, null, new[] { s_GameViewSizeType }, null);
            s_FindSize = s_GameViewSizeGroupType.GetMethod("FindSize", InstanceFlags, null,
                new[] { s_GameViewSizeTypeEnum, typeof(int), typeof(int) }, null);
            s_GetBuiltinCount = s_GameViewSizeGroupType.GetMethod("GetBuiltinCount", InstanceFlags);
            s_GetCustomCount = s_GameViewSizeGroupType.GetMethod("GetCustomCount", InstanceFlags);
            s_GetGameViewSize = s_GameViewSizeGroupType.GetMethod("GetGameViewSize", InstanceFlags, null, new[] { typeof(int) }, null);
            s_SelectedSizeIndex = s_GameViewType.GetProperty("selectedSizeIndex", InstanceFlags);
            s_GameViewCurrentSizeGroupType = s_GameViewType.GetProperty("currentSizeGroupType", InstanceFlags);
            s_GameViewSizesCurrentGroupType = s_GameViewSizesType.GetProperty("currentGroupType", InstanceFlags);
            s_GameViewSizeConstructor = s_GameViewSizeType.GetConstructor(InstanceFlags, null,
                new[] { s_GameViewSizeTypeEnum, typeof(int), typeof(int), typeof(string) }, null);

            s_SizeTypeProperty = s_GameViewSizeType.GetProperty("sizeType", InstanceFlags);
            s_WidthProperty = s_GameViewSizeType.GetProperty("width", InstanceFlags);
            s_HeightProperty = s_GameViewSizeType.GetProperty("height", InstanceFlags);
            s_BaseTextProperty = s_GameViewSizeType.GetProperty("baseText", InstanceFlags)
                ?? s_GameViewSizeType.GetProperty("displayText", InstanceFlags);
            s_SizeTypeField = s_GameViewSizeType.GetField("m_SizeType", InstanceFlags);
            s_WidthField = s_GameViewSizeType.GetField("m_Width", InstanceFlags);
            s_HeightField = s_GameViewSizeType.GetField("m_Height", InstanceFlags);
            s_BaseTextField = s_GameViewSizeType.GetField("m_BaseText", InstanceFlags)
                ?? s_GameViewSizeType.GetField("m_DisplayText", InstanceFlags);

            if (!TryParseEnum(s_GameViewSizeTypeEnum, "FixedResolution", out s_FixedResolutionValue) ||
                !TryParseEnum(s_GameViewSizeGroupTypeEnum, DefaultGroupName, out s_DefaultGroupValue) ||
                s_GameViewSizesInstance == null || s_GetGroup == null || s_AddCustomSize == null ||
                s_GetBuiltinCount == null || s_GetCustomCount == null || s_GetGameViewSize == null ||
                s_SelectedSizeIndex == null || s_GameViewSizeConstructor == null ||
                !CanReadSizeInfo())
            {
                s_GameViewType = null;
                return false;
            }

            return true;
        }

        private static bool CanReadSizeInfo()
        {
            return (s_SizeTypeProperty != null || s_SizeTypeField != null) &&
                (s_WidthProperty != null || s_WidthField != null) &&
                (s_HeightProperty != null || s_HeightField != null);
        }

        private static bool TryParseEnum(Type enumType, string name, out object value)
        {
            try
            {
                value = Enum.Parse(enumType, name);
                return true;
            }
            catch
            {
                value = null;
                return false;
            }
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

        private static object ResolveGroupType(EditorWindow gameView, object sizes)
        {
            if (s_GameViewCurrentSizeGroupType != null)
            {
                var value = s_GameViewCurrentSizeGroupType.GetValue(gameView, null);
                if (value != null)
                {
                    return value;
                }
            }
            if (s_GameViewSizesCurrentGroupType != null)
            {
                var value = s_GameViewSizesCurrentGroupType.GetValue(sizes, null);
                if (value != null)
                {
                    return value;
                }
            }
            return s_DefaultGroupValue;
        }

        private static int FindFixedSize(object group, int width, int height)
        {
            if (s_FindSize != null)
            {
                var found = Convert.ToInt32(s_FindSize.Invoke(group, new[] { s_FixedResolutionValue, width, height }));
                if (found >= 0 && IsMatchingFixedSize(GetSize(group, found), width, height))
                {
                    return found;
                }
            }

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

        private static int SizeCount(object group)
        {
            var builtin = Convert.ToInt32(s_GetBuiltinCount.Invoke(group, null));
            var custom = Convert.ToInt32(s_GetCustomCount.Invoke(group, null));
            return builtin + custom;
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
            var sizeType = ReadMember(size, s_SizeTypeProperty, s_SizeTypeField);
            var width = Convert.ToInt32(ReadMember(size, s_WidthProperty, s_WidthField));
            var height = Convert.ToInt32(ReadMember(size, s_HeightProperty, s_HeightField));
            var label = ReadMember(size, s_BaseTextProperty, s_BaseTextField) as string ?? "";
            return new SizeInfo(Equals(sizeType, s_FixedResolutionValue), width, height, label);
        }

        private static object ReadMember(object target, PropertyInfo property, FieldInfo field)
        {
            if (property != null)
            {
                return property.GetValue(target, null);
            }
            return field != null ? field.GetValue(target) : null;
        }

        private static string GroupName(object groupType)
        {
            return groupType != null ? groupType.ToString() : DefaultGroupName;
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
