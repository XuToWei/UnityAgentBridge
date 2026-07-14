using System;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace AgentBridge
{
    internal static class ObjectMutationSupport
    {
        public static bool RequireStableState(string command)
        {
            if (EditorApplication.isPlaying != EditorApplication.isPlayingOrWillChangePlaymode)
            {
                throw new CommandException(PlayModeErrorCodes.PlayModeTransition,
                    $"Unity 正在切换 PlayMode,请稍后重试 {command}");
            }
            return !EditorApplication.isPlaying;
        }

        public static Type ResolveComponentType(string typeName)
        {
            var type = SceneObjectResolver.FindType(typeName, out var ambiguous);
            if (type == null)
            {
                if (ambiguous)
                {
                    throw new CommandException(RefErrorCodes.ComponentTypeAmbiguous,
                        $"组件短类型名 '{typeName}' 命中多个类型;请传完整命名空间类型名");
                }
                throw new CommandException(RefErrorCodes.ComponentNotFound,
                    $"未知组件类型 '{typeName}'");
            }
            if (type.IsAbstract || type.ContainsGenericParameters)
            {
                throw new CommandException("COMPONENT_TYPE_NOT_ADDABLE",
                    $"组件类型不能实例化:'{type.FullName}'");
            }
            return type;
        }

        public static void MarkSceneDirty(GameObject go, bool persistent)
        {
            if (persistent && go != null && go.scene.IsValid())
            {
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(go.scene);
            }
        }

        public static UndoTransaction BeginUndo(string command, bool persistent)
        {
            return new UndoTransaction("AgentBridge " + command, persistent);
        }

        public static Vector3? ReadVector3(JToken token, string name)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }
            if (token.Type != JTokenType.Object)
            {
                throw new CommandException(ErrorCodes.InvalidParams,
                    name + " 必须是包含 x/y/z 的对象");
            }
            return new Vector3(
                ReadFloat(token["x"], name + ".x"),
                ReadFloat(token["y"], name + ".y"),
                ReadFloat(token["z"], name + ".z"));
        }

        private static float ReadFloat(JToken token, string name)
        {
            if (token == null ||
                (token.Type != JTokenType.Integer && token.Type != JTokenType.Float))
            {
                throw new CommandException(ErrorCodes.InvalidParams, name + " 必须是数字");
            }
            try
            {
                var value = token.Value<double>();
                if (double.IsNaN(value) || double.IsInfinity(value) ||
                    value < -float.MaxValue || value > float.MaxValue)
                {
                    throw new OverflowException();
                }
                return (float)value;
            }
            catch (Exception)
            {
                throw new CommandException(ErrorCodes.InvalidParams,
                    name + " 必须是 Single 范围内的有限数字");
            }
        }

        public static JObject Vector3Schema(string description)
        {
            return new JObject
            {
                ["type"] = "object",
                ["description"] = description,
                ["properties"] = new JObject
                {
                    ["x"] = new JObject { ["type"] = "number" },
                    ["y"] = new JObject { ["type"] = "number" },
                    ["z"] = new JObject { ["type"] = "number" }
                },
                ["required"] = new JArray("x", "y", "z")
            };
        }

        internal sealed class UndoTransaction : IDisposable
        {
            private readonly bool m_Persistent;
            private readonly string m_Name;
            private readonly int m_Group;
            private bool m_Completed;
            private bool m_Disposed;

            internal UndoTransaction(string name, bool persistent)
            {
                m_Name = name;
                m_Persistent = persistent;
                if (!persistent)
                {
                    m_Group = -1;
                    return;
                }

                Undo.IncrementCurrentGroup();
                m_Group = Undo.GetCurrentGroup();
                Undo.SetCurrentGroupName(name);
            }

            public string Name => m_Name;
            public int Group => m_Group;

            public void Record(params UnityEngine.Object[] targets)
            {
                if (!m_Persistent || targets == null)
                {
                    return;
                }
                foreach (var target in targets)
                {
                    if (target != null)
                    {
                        Undo.RecordObject(target, m_Name);
                    }
                }
            }

            public void Complete()
            {
                if (m_Completed)
                {
                    return;
                }
                if (m_Persistent)
                {
                    Undo.FlushUndoRecordObjects();
                    Undo.CollapseUndoOperations(m_Group);
                }
                m_Completed = true;
            }

            public void Dispose()
            {
                if (m_Disposed)
                {
                    return;
                }
                m_Disposed = true;
                if (m_Persistent && !m_Completed)
                {
                    Undo.RevertAllDownToGroup(m_Group);
                }
            }
        }
    }
}
