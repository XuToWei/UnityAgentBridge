using System.Threading.Tasks;
using System;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace AgentBridge
{
    public sealed class RemoveComponentHandler : ICommandHandler
    {
        public string Command => "remove_component";
        public string Description => "删除组件(Transform/RectTransform 除外);EditMode 记录 Undo,PlayMode 只改运行时副本";
        public string Group => "Mutation";
        public bool CanDisable => true;
        public CommandBatchMode BatchMode => CommandBatchMode.AllowedWithUndoCollapse;

        public Task<object> ExecuteAsync(JObject @params)
        {
            var persistent = ObjectMutationSupport.RequireStableState(Command);
            var componentRef = @params?["component"]?.ToObject<ComponentRef>();
            var component = SceneObjectResolver.ResolveComponent(componentRef);
            if (component is Transform)
            {
                throw new CommandException("COMPONENT_REMOVE_NOT_ALLOWED",
                    "不能从 GameObject 删除 Transform/RectTransform");
            }
            var go = component.gameObject;
            var removedType = component.GetType().FullName;
            var removedIndex = Array.IndexOf(
                SceneObjectResolver.GetExactComponents(go, component.GetType()), component);
            using (var mutation = ObjectMutationSupport.BeginUndo(Command, persistent))
            {
                try
                {
                    if (persistent)
                    {
                        Undo.DestroyObjectImmediate(component);
                    }
                    else
                    {
                        UnityEngine.Object.DestroyImmediate(component);
                    }
                }
                catch (Exception ex)
                {
                    throw new CommandException("COMPONENT_REMOVE_FAILED",
                        $"无法从 '{go.name}' 删除 {removedType}:{ex.Message}");
                }
                if (component != null)
                {
                    throw new CommandException("COMPONENT_REMOVE_FAILED",
                        $"Unity 拒绝从 '{go.name}' 删除 {removedType};可能仍有 RequireComponent 依赖");
                }
                ObjectMutationSupport.MarkSceneDirty(go, persistent);
                mutation.Complete();
            }
            return Task.FromResult<object>(new
            {
                removed = true,
                component = new { type = removedType, index = removedIndex },
                @object = SceneObjectResolver.Describe(go),
                persistent
            });
        }

        public JObject ParamsSchema { get; } = new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["component"] = SceneObjectResolver.CreateComponentRefSchema()
            },
            ["required"] = new JArray("component")
        };
    }
}
