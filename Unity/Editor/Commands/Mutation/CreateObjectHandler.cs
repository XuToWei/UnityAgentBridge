using System;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace AgentBridge
{
    /// <summary>
    /// create_object(写):创建 GameObject。params.kind = empty(默认) / primitive / prefab。
    /// primitive 需 params.primitive(Cube/Sphere/...);prefab 需 params.prefabPath(资源路径)。
    /// 可选 params.name、params.parent(ObjectRef)、position/rotation/scale/active。
    /// Transform 参数均使用父级下的本地坐标。记录 Undo、标 dirty 不自动保存。返回新对象 ObjectRef。
    /// </summary>
    public sealed class CreateObjectHandler : ICommandHandler
    {
        public string Command => "create_object";
        public string Description => "创建 GameObject;EditMode 记录 Undo/标 dirty,PlayMode 修改运行时副本并返回 persistent=false";
        public string Group => "Mutation";
        public bool CanDisable => true;
        public CommandBatchMode BatchMode => CommandBatchMode.AllowedWithUndoCollapse;

        public object Execute(JObject @params)
        {
            var persistent = ObjectMutationSupport.RequireStableState(Command);
            var kind = (@params?["kind"]?.Value<string>() ?? "empty").ToLowerInvariant();
            var name = @params?["name"]?.Value<string>();
            var position = ObjectMutationSupport.ReadVector3(@params?["position"], "position");
            var rotation = ObjectMutationSupport.ReadVector3(@params?["rotation"], "rotation");
            var scale = ObjectMutationSupport.ReadVector3(@params?["scale"], "scale");
            var active = ReadOptionalBool(@params?["active"], "active");

            GameObject parent = null;
            var parentRef = @params?["parent"]?.ToObject<ObjectRef>();
            if (parentRef != null)
            {
                parent = SceneObjectResolver.ResolveObject(parentRef); // OBJECT_NOT_FOUND 等
            }

            GameObject go;
            switch (kind)
            {
                case "empty":
                    go = new GameObject(string.IsNullOrEmpty(name) ? "GameObject" : name);
                    break;

                case "primitive":
                    var primName = @params?["primitive"]?.Value<string>();
                    if (string.IsNullOrEmpty(primName) ||
                        !Enum.TryParse<PrimitiveType>(primName, true, out var prim) ||
                        !Enum.IsDefined(typeof(PrimitiveType), prim))
                    {
                        throw new CommandException(ErrorCodes.InvalidParams,
                            $"未知图元类型 '{primName}'(应为 Cube/Sphere/Capsule/Cylinder/Plane/Quad)");
                    }
                    go = GameObject.CreatePrimitive(prim);
                    if (!string.IsNullOrEmpty(name))
                    {
                        go.name = name;
                    }
                    break;

                case "prefab":
                    var prefabPath = @params?["prefabPath"]?.Value<string>();
                    if (string.IsNullOrEmpty(prefabPath))
                    {
                        throw new CommandException(ErrorCodes.InvalidParams, "kind=prefab 需 prefabPath");
                    }
                    var asset = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                    if (asset == null)
                    {
                        throw new CommandException(MutationErrorCodes.CreateFailed, $"prefab 资源未找到:'{prefabPath}'");
                    }
                    go = PrefabUtility.InstantiatePrefab(asset) as GameObject;
                    if (go == null)
                    {
                        throw new CommandException(MutationErrorCodes.CreateFailed, $"实例化 prefab 失败:'{prefabPath}'");
                    }
                    if (!string.IsNullOrEmpty(name))
                    {
                        go.name = name;
                    }
                    break;

                default:
                    throw new CommandException(ErrorCodes.InvalidParams,
                        $"未知 kind '{kind}'(应为 empty/primitive/prefab)");
            }

            var undoRegistered = false;
            try
            {
                using (var mutation = ObjectMutationSupport.BeginUndo(Command, persistent))
                {
                    // 先登记新对象，再执行后续初始化；任何异常都会撤销整个创建。
                    if (persistent)
                    {
                        Undo.RegisterCreatedObjectUndo(go, mutation.Name);
                        undoRegistered = true;
                    }
                    if (parent != null)
                    {
                        go.transform.SetParent(parent.transform, true);
                    }
                    ApplyInitialState(go, position, rotation, scale, active);
                    if (persistent)
                    {
                        PrefabUtility.RecordPrefabInstancePropertyModifications(go);
                        PrefabUtility.RecordPrefabInstancePropertyModifications(go.transform);
                    }
                    ObjectMutationSupport.MarkSceneDirty(go, persistent);
                    mutation.Complete();
                }
            }
            catch
            {
                if ((!persistent || !undoRegistered) && go != null)
                {
                    UnityEngine.Object.DestroyImmediate(go);
                }
                throw;
            }

            return new
            {
                @object = SceneObjectResolver.Describe(go),
                persistent
            };
        }

        private static void ApplyInitialState(
            GameObject go,
            Vector3? position,
            Vector3? rotation,
            Vector3? scale,
            bool? active)
        {
            if (position.HasValue)
            {
                go.transform.localPosition = position.Value;
            }
            if (rotation.HasValue)
            {
                go.transform.localEulerAngles = rotation.Value;
            }
            if (scale.HasValue)
            {
                go.transform.localScale = scale.Value;
            }
            if (active.HasValue)
            {
                go.SetActive(active.Value);
            }
        }

        private static bool? ReadOptionalBool(JToken token, string name)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }
            if (token.Type != JTokenType.Boolean)
            {
                throw new CommandException(ErrorCodes.InvalidParams, name + " 必须是 boolean");
            }
            return token.Value<bool>();
        }

        public JObject ParamsSchema { get; } = CreateParamsSchema();

        private static JObject CreateParamsSchema()
        {
            var parent = SceneObjectResolver.CreateObjectRefSchema();
            parent["description"] = "可选父对象;path 或 instanceId 至少提供一个。";
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["kind"] = new JObject
                    {
                        ["type"] = "string", ["enum"] = new JArray("empty", "primitive", "prefab"),
                        ["default"] = "empty"
                    },
                    ["primitive"] = new JObject
                    {
                        ["type"] = "string", ["description"] = "kind=primitive 时使用,如 Cube/Sphere/Capsule。"
                    },
                    ["prefabPath"] = new JObject
                    {
                        ["type"] = "string", ["description"] = "kind=prefab 时使用的 Assets/ 路径。"
                    },
                    ["name"] = new JObject { ["type"] = "string" },
                    ["parent"] = parent,
                    ["position"] = ObjectMutationSupport.Vector3Schema("可选本地坐标。"),
                    ["rotation"] = ObjectMutationSupport.Vector3Schema("可选本地欧拉角(度)。"),
                    ["scale"] = ObjectMutationSupport.Vector3Schema("可选本地缩放。"),
                    ["active"] = new JObject
                    {
                        ["type"] = "boolean", ["description"] = "可选激活状态。"
                    }
                },
                ["oneOf"] = new JArray(
                    new JObject { ["not"] = new JObject { ["required"] = new JArray("kind") } },
                    KindRequirement("empty"),
                    KindRequirement("primitive", "primitive"),
                    KindRequirement("prefab", "prefabPath"))
            };
        }

        private static JObject KindRequirement(string kind, params string[] extraRequired)
        {
            var required = new JArray("kind");
            foreach (var field in extraRequired)
            {
                required.Add(field);
            }
            return new JObject
            {
                ["properties"] = new JObject
                {
                    ["kind"] = new JObject { ["const"] = kind }
                },
                ["required"] = required
            };
        }
    }
}
