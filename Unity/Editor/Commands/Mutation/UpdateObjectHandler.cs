using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AgentBridge
{
    public sealed class UpdateObjectHandler : ICommandHandler
    {
        private static readonly string[] UpdateFields =
        {
            "name", "parent", "targetScenePath", "siblingIndex", "active", "tag", "layer",
            "isStatic", "position", "rotation", "scale"
        };

        public string Command => "update_object";
        public string Description => "更新 GameObject 名称/父级/场景/顺序/Active/Tag/Layer/Static/本地 Transform;返回新 ObjectRef";
        public string Group => "Mutation";
        public bool CanDisable => true;
        public CommandBatchMode BatchMode => CommandBatchMode.AllowedWithUndoCollapse;

        public object Execute(JObject @params)
        {
            var persistent = ObjectMutationSupport.RequireStableState(Command);
            var supplied = UpdateFields.Where(name => @params?.Property(name) != null).ToArray();
            if (supplied.Length == 0)
            {
                throw new CommandException(ErrorCodes.InvalidParams,
                    "至少提供一个要更新的字段");
            }
            if (!persistent && @params?.Property("isStatic") != null)
            {
                throw new CommandException("STATIC_EDIT_MODE_REQUIRED",
                    "isStatic 只能在 EditMode 修改");
            }

            var go = SceneObjectResolver.ResolveObject(@params?["object"]?.ToObject<ObjectRef>());
            var transform = go.transform;
            var oldScene = go.scene;
            var oldLocalPosition = transform.localPosition;
            var oldLocalRotation = transform.localRotation;
            var oldLocalScale = transform.localScale;

            var parentProperty = @params?.Property("parent");
            GameObject newParent = null;
            if (parentProperty != null && parentProperty.Value.Type != JTokenType.Null)
            {
                newParent = SceneObjectResolver.ResolveObject(parentProperty.Value.ToObject<ObjectRef>());
                if (newParent == go || newParent.transform.IsChildOf(transform))
                {
                    throw new CommandException("PARENT_CYCLE",
                        "parent 不能是对象自身或其后代");
                }
            }

            Scene targetScene = default(Scene);
            var targetSceneToken = @params?["targetScenePath"];
            if (targetSceneToken != null && targetSceneToken.Type != JTokenType.Null)
            {
                var targetPath = targetSceneToken.Value<string>();
                targetScene = SceneCommandSupport.FindLoadedScene(targetPath);
                if (!targetScene.IsValid() || !targetScene.isLoaded)
                {
                    throw new CommandException(SceneCommandErrorCodes.SceneNotLoaded,
                        $"目标场景未加载:'{targetPath}'");
                }
                if (newParent != null && newParent.scene.handle != targetScene.handle)
                {
                    throw new CommandException(ErrorCodes.InvalidParams,
                        "parent 所在场景与 targetScenePath 不一致");
                }
            }
            else if (newParent != null)
            {
                targetScene = newParent.scene;
            }

            ValidateScalarFields(@params, go, newParent, parentProperty != null, targetScene);
            var position = ObjectMutationSupport.ReadVector3(@params?["position"], "position");
            var rotation = ObjectMutationSupport.ReadVector3(@params?["rotation"], "rotation");
            var scale = ObjectMutationSupport.ReadVector3(@params?["scale"], "scale");
            var worldPositionStays = SceneCommandSupport.ReadBool(@params, "worldPositionStays", true);
            var changed = new List<string>();
            var runtimeSnapshot = persistent ? null : RuntimeObjectSnapshot.Capture(go);

            using (var mutation = ObjectMutationSupport.BeginUndo(Command, persistent))
            {
                mutation.Record(go, transform);
                try
                {
                    if (parentProperty != null || targetScene.IsValid())
                    {
                        ApplyParentAndScene(go, newParent, targetScene, parentProperty != null,
                            worldPositionStays, persistent, oldLocalPosition, oldLocalRotation, oldLocalScale);
                        changed.Add(parentProperty != null ? "parent" : "targetScenePath");
                    }
                    ApplyGameObjectFields(@params, go, changed);
                    if (position.HasValue)
                    {
                        transform.localPosition = position.Value;
                        changed.Add("position");
                    }
                    if (rotation.HasValue)
                    {
                        transform.localEulerAngles = rotation.Value;
                        changed.Add("rotation");
                    }
                    if (scale.HasValue)
                    {
                        transform.localScale = scale.Value;
                        changed.Add("scale");
                    }
                    if (@params?.Property("siblingIndex") != null)
                    {
                        transform.SetSiblingIndex(@params["siblingIndex"].Value<int>());
                        changed.Add("siblingIndex");
                    }
                    if (persistent)
                    {
                        PrefabUtility.RecordPrefabInstancePropertyModifications(go);
                        PrefabUtility.RecordPrefabInstancePropertyModifications(transform);
                        if (oldScene.IsValid())
                        {
                            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(oldScene);
                        }
                        ObjectMutationSupport.MarkSceneDirty(go, true);
                    }
                    mutation.Complete();
                }
                catch (CommandException ex)
                {
                    RestoreRuntimeOrThrow(runtimeSnapshot, ex);
                    throw;
                }
                catch (Exception ex)
                {
                    RestoreRuntimeOrThrow(runtimeSnapshot, ex);
                    throw new CommandException("OBJECT_UPDATE_FAILED", ex.Message);
                }
            }

            return new
            {
                updated = true,
                changed = changed.Distinct().ToArray(),
                @object = SceneObjectResolver.Describe(go),
                state = new
                {
                    parent = transform.parent == null ? null : SceneObjectResolver.Describe(transform.parent.gameObject),
                    siblingIndex = transform.GetSiblingIndex(),
                    active = go.activeSelf,
                    tag = go.tag,
                    layer = go.layer,
                    isStatic = go.isStatic,
                    position = DescribeVector(transform.localPosition),
                    rotation = DescribeVector(transform.localEulerAngles),
                    scale = DescribeVector(transform.localScale)
                },
                persistent
            };
        }

        private static void ValidateScalarFields(JObject @params, GameObject go, GameObject parent,
            bool parentSupplied, Scene targetScene)
        {
            var nameToken = @params?["name"];
            if (nameToken != null && nameToken.Value<string>()?.Length > 255)
            {
                throw new CommandException(ErrorCodes.InvalidParams, "name 不能超过 255 个字符");
            }
            var tagToken = @params?["tag"];
            if (tagToken != null && !InternalEditorUtility.tags.Contains(tagToken.Value<string>()))
            {
                throw new CommandException("INVALID_TAG", $"Tag 未定义:'{tagToken.Value<string>()}'");
            }
            ResolveLayer(@params?["layer"]);

            var siblingToken = @params?["siblingIndex"];
            if (siblingToken != null)
            {
                var movingToOtherSceneRoot = !parentSupplied && targetScene.IsValid() &&
                                             targetScene.handle != go.scene.handle;
                var parentTransform = parentSupplied
                    ? parent?.transform
                    : movingToOtherSceneRoot ? null : go.transform.parent;
                var siblingScene = parentTransform != null
                    ? parentTransform.gameObject.scene
                    : targetScene.IsValid() ? targetScene : go.scene;
                var count = parentTransform == null
                    ? siblingScene.GetRootGameObjects().Length
                    : parentTransform.childCount;
                var sameContainer = parentTransform == go.transform.parent &&
                                    siblingScene.handle == go.scene.handle;
                var max = Math.Max(0, count - (sameContainer ? 1 : 0));
                var index = siblingToken.Value<int>();
                if (index < 0 || index > max)
                {
                    throw new CommandException("SIBLING_INDEX_OUT_OF_RANGE",
                        $"siblingIndex={index} 超出允许范围 0..{max}");
                }
            }
        }

        private static void ApplyParentAndScene(GameObject go, GameObject parent, Scene targetScene,
            bool parentSupplied, bool worldPositionStays, bool persistent,
            Vector3 oldLocalPosition, Quaternion oldLocalRotation, Vector3 oldLocalScale)
        {
            var desiredParent = parentSupplied
                ? parent?.transform
                : targetScene.IsValid() && targetScene.handle != go.scene.handle
                    ? null
                    : go.transform.parent;
            var desiredScene = targetScene.IsValid() ? targetScene :
                (desiredParent != null ? desiredParent.gameObject.scene : go.scene);

            if (go.transform.parent != desiredParent)
            {
                if (persistent)
                {
                    Undo.SetTransformParent(go.transform, desiredParent, "AgentBridge update_object");
                }
                else
                {
                    go.transform.SetParent(desiredParent, true);
                }
                if (!worldPositionStays)
                {
                    go.transform.localPosition = oldLocalPosition;
                    go.transform.localRotation = oldLocalRotation;
                    go.transform.localScale = oldLocalScale;
                }
            }

            if (desiredScene.IsValid() && go.scene.handle != desiredScene.handle)
            {
                if (go.transform.parent != null)
                {
                    if (persistent)
                    {
                        Undo.SetTransformParent(go.transform, null, "AgentBridge update_object");
                    }
                    else
                    {
                        go.transform.SetParent(null, worldPositionStays);
                    }
                }
                if (persistent)
                {
                    Undo.MoveGameObjectToScene(go, desiredScene, "AgentBridge update_object");
                }
                else
                {
                    SceneManager.MoveGameObjectToScene(go, desiredScene);
                }
                if (desiredParent != null)
                {
                    if (persistent)
                    {
                        Undo.SetTransformParent(go.transform, desiredParent, "AgentBridge update_object");
                    }
                    else
                    {
                        go.transform.SetParent(desiredParent, worldPositionStays);
                    }
                }
            }
        }

        private static void ApplyGameObjectFields(JObject @params, GameObject go, List<string> changed)
        {
            if (@params?.Property("name") != null)
            {
                go.name = @params["name"].Value<string>();
                changed.Add("name");
            }
            if (@params?.Property("active") != null)
            {
                go.SetActive(@params["active"].Value<bool>());
                changed.Add("active");
            }
            if (@params?.Property("tag") != null)
            {
                go.tag = @params["tag"].Value<string>();
                changed.Add("tag");
            }
            if (@params?.Property("layer") != null)
            {
                go.layer = ResolveLayer(@params["layer"]).Value;
                changed.Add("layer");
            }
            if (@params?.Property("isStatic") != null)
            {
                GameObjectUtility.SetStaticEditorFlags(go,
                    @params["isStatic"].Value<bool>() ? (StaticEditorFlags)(-1) : 0);
                changed.Add("isStatic");
            }
        }

        private static int? ResolveLayer(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }
            var layer = token.Type == JTokenType.Integer
                ? token.Value<int>()
                : LayerMask.NameToLayer(token.Value<string>());
            if (layer < 0 || layer > 31)
            {
                throw new CommandException("INVALID_LAYER", "layer 必须是 0..31 或已定义 Layer 名称");
            }
            return layer;
        }

        private static object DescribeVector(Vector3 value)
        {
            return new { x = value.x, y = value.y, z = value.z };
        }

        private static void RestoreRuntimeOrThrow(
            RuntimeObjectSnapshot snapshot,
            Exception operationError = null)
        {
            if (snapshot == null)
            {
                return;
            }
            try
            {
                snapshot.Restore();
            }
            catch (Exception rollbackError)
            {
                throw new CommandException("OBJECT_UPDATE_FAILED",
                    $"PlayMode 更新失败且回滚失败:{operationError?.Message ?? "命令参数或 Unity 操作失败"}; rollback:{rollbackError.Message}");
            }
        }

        private sealed class RuntimeObjectSnapshot
        {
            private readonly GameObject m_Object;
            private readonly Transform m_Parent;
            private readonly Scene m_Scene;
            private readonly int m_SiblingIndex;
            private readonly string m_Name;
            private readonly bool m_Active;
            private readonly string m_Tag;
            private readonly int m_Layer;
            private readonly Vector3 m_LocalPosition;
            private readonly Quaternion m_LocalRotation;
            private readonly Vector3 m_LocalScale;

            private RuntimeObjectSnapshot(GameObject go)
            {
                m_Object = go;
                m_Parent = go.transform.parent;
                m_Scene = go.scene;
                m_SiblingIndex = go.transform.GetSiblingIndex();
                m_Name = go.name;
                m_Active = go.activeSelf;
                m_Tag = go.tag;
                m_Layer = go.layer;
                m_LocalPosition = go.transform.localPosition;
                m_LocalRotation = go.transform.localRotation;
                m_LocalScale = go.transform.localScale;
            }

            public static RuntimeObjectSnapshot Capture(GameObject go)
            {
                return new RuntimeObjectSnapshot(go);
            }

            public void Restore()
            {
                if (m_Object == null)
                {
                    throw new InvalidOperationException("目标 GameObject 已被销毁");
                }

                var transform = m_Object.transform;
                if (m_Object.scene.handle != m_Scene.handle)
                {
                    transform.SetParent(null, true);
                    SceneManager.MoveGameObjectToScene(m_Object, m_Scene);
                }
                transform.SetParent(m_Parent, false);
                transform.localPosition = m_LocalPosition;
                transform.localRotation = m_LocalRotation;
                transform.localScale = m_LocalScale;
                transform.SetSiblingIndex(m_SiblingIndex);
                m_Object.name = m_Name;
                m_Object.tag = m_Tag;
                m_Object.layer = m_Layer;
                m_Object.SetActive(m_Active);
            }
        }

        public JObject ParamsSchema { get; } = CreateParamsSchema();

        private static JObject CreateParamsSchema()
        {
            var properties = new JObject
            {
                ["object"] = SceneObjectResolver.CreateObjectRefSchema(),
                ["name"] = new JObject { ["type"] = "string", ["maxLength"] = 255 },
                ["parent"] = new JObject
                {
                    ["anyOf"] = new JArray(
                        SceneObjectResolver.CreateObjectRefSchema(),
                        new JObject { ["type"] = "null" })
                },
                ["targetScenePath"] = new JObject { ["type"] = "string" },
                ["worldPositionStays"] = new JObject { ["type"] = "boolean", ["default"] = true },
                ["siblingIndex"] = new JObject { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = int.MaxValue },
                ["active"] = new JObject { ["type"] = "boolean" },
                ["tag"] = new JObject { ["type"] = "string", ["minLength"] = 1 },
                ["layer"] = new JObject
                {
                    ["anyOf"] = new JArray(
                        new JObject { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = 31 },
                        new JObject { ["type"] = "string", ["minLength"] = 1 })
                },
                ["isStatic"] = new JObject { ["type"] = "boolean" },
                ["position"] = ObjectMutationSupport.Vector3Schema("本地坐标"),
                ["rotation"] = ObjectMutationSupport.Vector3Schema("本地欧拉角(度)"),
                ["scale"] = ObjectMutationSupport.Vector3Schema("本地缩放")
            };
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = new JArray("object")
            };
        }
    }
}
