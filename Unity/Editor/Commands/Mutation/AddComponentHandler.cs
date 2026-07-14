using System;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace AgentBridge
{
    public sealed class AddComponentHandler : ICommandHandler
    {
        public string Command => "add_component";
        public string Description => "给场景 GameObject 添加组件;EditMode 记录 Undo,PlayMode 只改运行时副本";
        public string Group => "Mutation";
        public bool CanDisable => true;
        public CommandBatchMode BatchMode => CommandBatchMode.AllowedWithUndoCollapse;

        public object Execute(JObject @params)
        {
            var persistent = ObjectMutationSupport.RequireStableState(Command);
            var go = SceneObjectResolver.ResolveObject(@params?["object"]?.ToObject<ObjectRef>());
            var typeName = @params?["type"]?.Value<string>();
            var type = ObjectMutationSupport.ResolveComponentType(typeName);
            if (type == typeof(Transform) || type == typeof(RectTransform))
            {
                throw new CommandException("COMPONENT_TYPE_NOT_ADDABLE",
                    $"{type.Name} 由 GameObject 创建方式决定,不能后加");
            }

            Component component;
            using (var mutation = ObjectMutationSupport.BeginUndo(Command, persistent))
            {
                try
                {
                    component = persistent
                        ? Undo.AddComponent(go, type)
                        : go.AddComponent(type);
                }
                catch (Exception ex)
                {
                    throw new CommandException("COMPONENT_ADD_FAILED",
                        $"无法给 '{go.name}' 添加 {type.FullName}:{ex.Message}");
                }
                if (component == null)
                {
                    throw new CommandException("COMPONENT_ADD_FAILED",
                        $"Unity 未能给 '{go.name}' 添加 {type.FullName}");
                }
                if (persistent)
                {
                    PrefabUtility.RecordPrefabInstancePropertyModifications(component);
                }
                ObjectMutationSupport.MarkSceneDirty(go, persistent);
                mutation.Complete();
            }
            return new
            {
                added = true,
                component = SceneObjectResolver.Describe(component),
                persistent
            };
        }

        public JObject ParamsSchema { get; } = new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["object"] = SceneObjectResolver.CreateObjectRefSchema(),
                ["type"] = new JObject { ["type"] = "string", ["minLength"] = 1 }
            },
            ["required"] = new JArray("object", "type")
        };
    }
}
