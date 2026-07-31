using System;
using System.Reflection;
using UnityEditor;
using UnityEditorInternal;

namespace AgentBridge
{
    /// <summary>
    /// 为当前 Profiler session 提供跨查询稳定的标识。优先使用 Unity 自身保存进
    /// .data 的 session metadata GUID；反射不可用时才退化为当前缓冲锚点。
    /// </summary>
    internal static class ProfilerCaptureIdentity
    {
        private const string ManagedIdKey =
            "AgentBridge.Profiler.ManagedCaptureId";
        private const string ManagedSessionKey =
            "AgentBridge.Profiler.ManagedSessionGuid";

        private static readonly PropertyInfo SessionGuidProperty =
            typeof(ProfilerDriver).GetProperty(
                "profilerInternalSessionMetaDataGuid",
                BindingFlags.Static | BindingFlags.Public |
                BindingFlags.NonPublic);

        internal static string GetCurrentCaptureId(
            int connectedProfiler,
            int firstFrameIndex,
            int lastFrameIndex)
        {
            var active = ProfilerCaptureStore.ReadActive();
            if (active != null &&
                !string.IsNullOrEmpty(active.CaptureId))
            {
                return active.CaptureId;
            }

            var sessionGuid = TryGetSessionGuid();
            if (sessionGuid.HasValue && sessionGuid.Value != Guid.Empty)
            {
                var key = sessionGuid.Value.ToString("N");
                var managedSession = SessionState.GetString(
                    ManagedSessionKey, "");
                var managedId = SessionState.GetString(ManagedIdKey, "");
                if (string.Equals(
                        managedSession, key, StringComparison.Ordinal) &&
                    !string.IsNullOrEmpty(managedId))
                {
                    return managedId;
                }
                return $"session_{key}";
            }

            // 2021.3–Unity 6 均存在上述 metadata GUID。该分支只为异常/裁剪过的
            // Editor API 保留；明确带 buffer 前缀，避免被误认为可重载的已保存 capture。
            return $"buffer_{connectedProfiler}_{firstFrameIndex}_{lastFrameIndex}";
        }

        internal static void AssignManagedCaptureId(string captureId)
        {
            if (string.IsNullOrEmpty(captureId))
            {
                return;
            }
            var sessionGuid = TryGetSessionGuid();
            if (!sessionGuid.HasValue || sessionGuid.Value == Guid.Empty)
            {
                return;
            }
            SessionState.SetString(ManagedIdKey, captureId);
            SessionState.SetString(
                ManagedSessionKey, sessionGuid.Value.ToString("N"));
        }

        internal static void ClearManagedCaptureId()
        {
            SessionState.EraseString(ManagedIdKey);
            SessionState.EraseString(ManagedSessionKey);
        }

        internal static Guid? TryGetSessionGuid()
        {
            if (SessionGuidProperty == null ||
                SessionGuidProperty.PropertyType != typeof(Guid))
            {
                return null;
            }
            try
            {
                return (Guid)SessionGuidProperty.GetValue(null, null);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
