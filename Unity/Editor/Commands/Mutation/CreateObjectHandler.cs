using System;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace AgentBridge
{
    /// <summary>
    /// create_object(写):创建 GameObject。params.kind = empty(默认) / primitive / prefab。
    /// primitive 需 params.primitive(Cube/Sphere/...);prefab 需 params.prefabPath(资源路径)。
    /// 可选 params.name、params.parent(ObjectRef)。记录 Undo、标 dirty 不自动保存。返回新对象 ObjectRef。
    /// </summary>
    public sealed class CreateObjectHandler : ICommandHandler
    {
        public string Command => "create_object";
        public string Description => "创建 GameObject(kind=empty/primitive/prefab,可选 name/parent);记录 Undo、返回新对象 ObjectRef";
        public string Group => "Mutation";
        public bool CanDisable => true;

        public object Execute(JObject @params)
        {
            var kind = (@params?["kind"]?.Value<string>() ?? "empty").ToLowerInvariant();
            var name = @params?["name"]?.Value<string>();

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

        public JObject GetParamsSchema()
        {
            return JObject.Parse(
                @"{""type"":""object"",""properties"":{""kind"":{""type"":""string"",""enum"":[""empty"",""primitive"",""prefab""]},""primitive"":{""type"":""string""},""prefabPath"":{""type"":""string""},""name"":{""type"":""string""},""parent"":{""type"":""object""}}}");
        }
    }
}
