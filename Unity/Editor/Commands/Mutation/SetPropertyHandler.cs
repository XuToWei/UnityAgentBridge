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
        public string Description => "改某组件属性(component+propertyPath+value,支持嵌套路径);记录 Undo、标 dirty 不自动保存";
        public string Group => "Mutation";
        public bool CanDisable => true;

        public object Execute(JObject @params)
        {
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
                so.ApplyModifiedProperties();                     // 内建 Undo 注册(一条命令一条撤销)
            }

            EditorUtility.SetDirty(comp);
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
                },
                component = comp.GetType().FullName,
                propertyPath,
                applied = true
            };
        }

        public JObject GetParamsSchema()
        {
            return JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""component"": {
      ""type"": ""object"",
      ""description"": ""组件引用;可直接使用 get_object 返回的 components[] 项。"",
      ""properties"": {
        ""object"": {
          ""type"": ""object"",
          ""properties"": {
            ""path"": { ""type"": ""string"" },
            ""instanceId"": { ""type"": ""integer"" }
          }
        },
        ""type"": { ""type"": ""string"" },
        ""index"": { ""type"": ""integer"", ""minimum"": 0, ""default"": 0 }
      },
      ""required"": [""object"", ""type""]
    },
    ""propertyPath"": { ""type"": ""string"" },
    ""value"": {}
  },
  ""required"": [""component"", ""propertyPath""]
}");
        }
    }
}
