using Newtonsoft.Json.Linq;
using UnityEditor;

namespace AgentBridge
{
    /// <summary>
    /// set_property(写):改某组件的一个属性(支持 SerializedProperty 嵌套路径,如 "m_LocalPosition.x")。
    /// params.component 必填(ComponentRef);params.propertyPath 必填;params.value 为新值。
    /// 记录 Undo(经 SerializedObject.ApplyModifiedProperties 内建撤销),标记 dirty,不自动保存。
    /// </summary>
    public sealed class SetPropertyHandler : ICommandHandler
    {
        public string Command => "set_property";
        public string Description => "改组件属性;EditMode 记录 Undo/标 dirty,PlayMode 修改运行时副本并返回 persistent=false";
        public string Group => "Mutation";
        public bool CanDisable => true;
        public CommandBatchMode BatchMode => CommandBatchMode.AllowedWithUndoCollapse;

        public object Execute(JObject @params)
        {
            var persistent = ObjectMutationSupport.RequireStableState(Command);
            var compRef = @params?["component"]?.ToObject<ComponentRef>();
            var propertyPath = @params?["propertyPath"]?.Value<string>();
            if (string.IsNullOrEmpty(propertyPath))
            {
                throw new CommandException(ErrorCodes.InvalidParams, "缺 propertyPath");
            }
            var value = @params?["value"];

            var comp = SceneObjectResolver.ResolveComponent(compRef); // OBJECT/COMPONENT_NOT_FOUND/INVALID_OBJECT_REF
            var go = comp.gameObject;

            using (var so = new SerializedObject(comp))
            {
                var prop = so.FindProperty(propertyPath);
                if (prop == null)
                {
                    throw new CommandException(MutationErrorCodes.PropertyNotFound,
                        $"'{comp.GetType().Name}' 上无属性 '{propertyPath}'");
                }

                PropertyDeserializer.Apply(prop, value);          // 类型不符 → PROPERTY_TYPE_MISMATCH
                using (var mutation = ObjectMutationSupport.BeginUndo(Command, persistent))
                {
                    mutation.Record(comp);
                    so.ApplyModifiedPropertiesWithoutUndo();
                    if (persistent)
                    {
                        EditorUtility.SetDirty(comp);
                        PrefabUtility.RecordPrefabInstancePropertyModifications(comp);
                    }
                    ObjectMutationSupport.MarkSceneDirty(go, persistent);
                    mutation.Complete();
                }
            }

            return new
            {
                @object = SceneObjectResolver.Describe(go),
                component = comp.GetType().FullName,
                propertyPath,
                applied = true,
                persistent
            };
        }

        public JObject ParamsSchema { get; } = CreateParamsSchema();

        private static JObject CreateParamsSchema()
        {
            var component = SceneObjectResolver.CreateComponentRefSchema();
            component["description"] =
                "组件引用;可直接使用 get_object 返回的 components[] 项。";
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["component"] = component,
                    ["propertyPath"] = new JObject { ["type"] = "string", ["minLength"] = 1 },
                    ["value"] = new JObject()
                },
                ["required"] = new JArray("component", "propertyPath", "value")
            };
        }
    }
}
