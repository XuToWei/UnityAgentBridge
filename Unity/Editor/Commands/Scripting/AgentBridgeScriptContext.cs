using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityObject = UnityEngine.Object;

namespace AgentBridge
{
    /// <summary>
    /// execute_csharp 注入的执行上下文。只有通过 Register/Destroy 方法登记的对象
    /// 才会自动参与 Undo、dirty 标记和结构化变更追踪。
    /// </summary>
    public sealed class AgentBridgeScriptContext
    {
        internal const int MaxLogEntries = 100;
        internal const int MaxTrackedEntries = 100;
        internal const int MaxSingleLogChars = 2048;
        internal const int MaxTotalLogChars = 32768;

        private readonly ObjectMutationSupport.UndoTransaction m_Undo;
        private readonly List<ScriptLogEntry> m_Logs = new List<ScriptLogEntry>();
        private readonly List<object> m_Created = new List<object>();
        private readonly List<object> m_Modified = new List<object>();
        private readonly List<object> m_Destroyed = new List<object>();
        private int m_LogChars;

        internal AgentBridgeScriptContext(
            bool persistent,
            ObjectMutationSupport.UndoTransaction undo)
        {
            Persistent = persistent;
            m_Undo = undo;
        }

        /// <summary>Edit Mode 为 true；Play Mode 运行时修改为 false。</summary>
        public bool Persistent { get; }

        /// <summary>可选返回值；执行结束后必须能转换成有界 JSON。</summary>
        public object ReturnValue { get; set; }

        internal IReadOnlyList<ScriptLogEntry> Logs => m_Logs;
        internal IReadOnlyList<object> Created => m_Created;
        internal IReadOnlyList<object> Modified => m_Modified;
        internal IReadOnlyList<object> Destroyed => m_Destroyed;
        internal bool LogsTruncated { get; private set; }
        internal bool TrackingTruncated { get; private set; }

        public void RegisterObjectCreation(UnityObject obj)
        {
            if (Persistent)
            {
                Undo.RegisterCreatedObjectUndo(obj, m_Undo.Name);
                MarkDirty(obj);
            }
            var descriptor = DescribeSupportedObject(obj);
            AddTracked(m_Created, descriptor);
        }

        public void RegisterObjectModification(UnityObject obj)
        {
            EnsureSupportedObject(obj);
            if (Persistent)
            {
                m_Undo.Record(obj);
                EditorUtility.SetDirty(obj);
                RecordPrefabModification(obj);
                MarkDirty(obj);
            }
            AddTracked(m_Modified, DescribeSupportedObject(obj));
        }

        public void DestroyObject(UnityObject obj)
        {
            if (obj is Transform)
            {
                throw new ScriptContextException(
                    ScriptErrorCodes.TrackingUnsupportedObject,
                    "Transform cannot be destroyed independently; destroy its GameObject instead");
            }

            var descriptor = DescribeSupportedObject(obj);
            AddTracked(m_Destroyed, descriptor);
            if (Persistent)
            {
                MarkDirty(obj);
                Undo.DestroyObjectImmediate(obj);
            }
            else
            {
                UnityObject.DestroyImmediate(obj);
            }
        }

        public void Log(string format, params object[] args)
        {
            AddLog("info", Format(format, args));
        }

        public void LogWarning(string format, params object[] args)
        {
            AddLog("warning", Format(format, args));
        }

        public void LogError(string format, params object[] args)
        {
            AddLog("error", Format(format, args));
        }

        private void AddLog(string level, string message)
        {
            if (m_Logs.Count >= MaxLogEntries || m_LogChars >= MaxTotalLogChars)
            {
                LogsTruncated = true;
                return;
            }

            message = message ?? string.Empty;
            var remaining = MaxTotalLogChars - m_LogChars;
            var max = Math.Min(MaxSingleLogChars, remaining);
            if (max <= 0)
            {
                LogsTruncated = true;
                return;
            }
            if (message.Length > max)
            {
                message = max == 1 ? "…" : message.Substring(0, max - 1) + "…";
                LogsTruncated = true;
            }

            m_LogChars += message.Length;
            m_Logs.Add(new ScriptLogEntry { level = level, message = message });
        }

        private void AddTracked(List<object> target, object descriptor)
        {
            if (target.Count >= MaxTrackedEntries)
            {
                TrackingTruncated = true;
                return;
            }
            target.Add(descriptor);
        }

        private static object DescribeSupportedObject(UnityObject obj)
        {
            EnsureSupportedObject(obj);
            if (obj is GameObject go)
            {
                return SceneObjectResolver.Describe(go);
            }
            return SceneObjectResolver.Describe((Component)obj);
        }

        private static void EnsureSupportedObject(UnityObject obj)
        {
            if (obj == null)
            {
                throw new ScriptContextException(
                    ScriptErrorCodes.TrackingUnsupportedObject,
                    "tracked object must not be null");
            }
            if (EditorUtility.IsPersistent(obj))
            {
                throw new ScriptContextException(
                    ScriptErrorCodes.TrackingUnsupportedObject,
                    "project assets are not supported by execute_csharp tracking");
            }
            if (obj is GameObject go && go.scene.IsValid())
            {
                return;
            }
            if (obj is Component component && component.gameObject.scene.IsValid())
            {
                return;
            }

            throw new ScriptContextException(
                ScriptErrorCodes.TrackingUnsupportedObject,
                $"only scene GameObjects and Components can be tracked, got {obj.GetType().FullName}");
        }

        private static void MarkDirty(UnityObject obj)
        {
            var go = obj as GameObject ?? (obj as Component)?.gameObject;
            ObjectMutationSupport.MarkSceneDirty(go, true);
        }

        private static void RecordPrefabModification(UnityObject obj)
        {
            if (obj is GameObject || obj is Component)
            {
                PrefabUtility.RecordPrefabInstancePropertyModifications(obj);
            }
        }

        private static string Format(string format, object[] args)
        {
            if (args == null || args.Length == 0)
            {
                return format ?? string.Empty;
            }
            try
            {
                return string.Format(format ?? string.Empty, args);
            }
            catch
            {
                return (format ?? string.Empty) + " " + string.Join(", ", args);
            }
        }

        internal sealed class ScriptLogEntry
        {
            public string level;
            public string message;
        }
    }

    internal sealed class ScriptContextException : Exception
    {
        internal ScriptContextException(string code, string message) : base(message)
        {
            Code = code;
        }

        internal string Code { get; }
    }
}