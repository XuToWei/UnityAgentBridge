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
        public string Description => "创建 GameObject;可选 name/parent 和本地 position/rotation/scale/active,返回新对象 ObjectRef";
        public string Group => "Mutation";
        public bool CanDisable => true;

        public object Execute(JObject @params)
        {
            var kind = (@params?["kind"]?.Value<string>() ?? "empty").ToLowerInvariant();
            var name = @params?["name"]?.Value<string>();
            var position = ReadVector3(@params?["position"], "position");
            var rotation = ReadVector3(@params?["rotation"], "rotation");
            var scale = ReadVector3(@params?["scale"], "scale");
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
                    if (string.IsNullOrEmpty(primName) || !Enum.TryParse<PrimitiveType>(primName, true, out var prim))
                    {
                        throw new CommandException(MutationErrorCodes.CreateFailed,
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
                        throw new CommandException(MutationErrorCodes.CreateFailed, "kind=prefab 需 prefabPath");
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
                    throw new CommandException(MutationErrorCodes.CreateFailed,
                        $"未知 kind '{kind}'(应为 empty/primitive/prefab)");
            }

            // 先设父级(随之进入父级所在场景),再注册创建撤销 → 一条命令一条撤销,undo 删除整个新对象。
            if (parent != null)
            {
                go.transform.SetParent(parent.transform, true);
            }
            ApplyInitialState(go, position, rotation, scale, active);
            Undo.RegisterCreatedObjectUndo(go, "AgentBridge create_object");

            if (go.scene.IsValid())
            {
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(go.scene);
            }

            return new
            {
                @object = new
                {
                    name = go.name,
                    path = SceneObjectResolver.GetPath(go.transform),
                    instanceId = go.GetInstanceID(),
                    active = go.activeSelf
                }
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

        private static Vector3? ReadVector3(JToken token, string name)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }
            if (token.Type != JTokenType.Object)
            {
                throw new CommandException(ErrorCodes.InvalidParams, name + " 必须是包含 x/y/z 的对象");
            }

            return new Vector3(
                ReadNumber(token["x"], name + ".x"),
                ReadNumber(token["y"], name + ".y"),
                ReadNumber(token["z"], name + ".z"));
        }

        private static float ReadNumber(JToken token, string name)
        {
            if (token == null || (token.Type != JTokenType.Integer && token.Type != JTokenType.Float))
            {
                throw new CommandException(ErrorCodes.InvalidParams, name + " 必须是数字");
            }
            return token.Value<float>();
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

        public JObject GetParamsSchema()
        {
            return JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""kind"": { ""type"": ""string"", ""enum"": [""empty"", ""primitive"", ""prefab""], ""default"": ""empty"" },
    ""primitive"": { ""type"": ""string"", ""description"": ""kind=primitive 时使用,如 Cube/Sphere/Capsule。"" },
    ""prefabPath"": { ""type"": ""string"", ""description"": ""kind=prefab 时使用的 Assets/ 路径。"" },
    ""name"": { ""type"": ""string"" },
    ""parent"": {
      ""type"": ""object"",
      ""description"": ""可选父对象;path 或 instanceId 至少提供一个。"",
      ""properties"": {
        ""path"": { ""type"": ""string"" },
        ""instanceId"": { ""type"": ""integer"" }
      }
    },
    ""position"": {
      ""type"": ""object"",
      ""description"": ""可选本地坐标。"",
      ""properties"": {
        ""x"": { ""type"": ""number"" }, ""y"": { ""type"": ""number"" }, ""z"": { ""type"": ""number"" }
      },
      ""required"": [""x"", ""y"", ""z""]
    },
    ""rotation"": {
      ""type"": ""object"",
      ""description"": ""可选本地欧拉角(度)。"",
      ""properties"": {
        ""x"": { ""type"": ""number"" }, ""y"": { ""type"": ""number"" }, ""z"": { ""type"": ""number"" }
      },
      ""required"": [""x"", ""y"", ""z""]
    },
    ""scale"": {
      ""type"": ""object"",
      ""description"": ""可选本地缩放。"",
      ""properties"": {
        ""x"": { ""type"": ""number"" }, ""y"": { ""type"": ""number"" }, ""z"": { ""type"": ""number"" }
      },
      ""required"": [""x"", ""y"", ""z""]
    },
    ""active"": { ""type"": ""boolean"", ""description"": ""可选激活状态。"" }
  }
}");
        }
    }
}
