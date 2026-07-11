using Newtonsoft.Json.Linq;
using UnityEditor;

namespace AgentBridge
{
    /// <summary>
    /// delete_object(写):删除一个 GameObject。params.object 必填(ObjectRef)。
    /// 经 Undo.DestroyObjectImmediate 可撤销,标记所在场景 dirty,不自动保存。
    /// 对象不存在(含重复删除)→ OBJECT_NOT_FOUND。
    /// </summary>
    public sealed class DeleteObjectHandler : ICommandHandler
    {
        public string Command => "delete_object";
        public string Description => "删除一个 GameObject(params.object);记录 Undo、标 dirty 不自动保存";
        public string Group => "Mutation";
        public bool CanDisable => true;

        public object Execute(JObject @params)
        {
            var objRef = @params?["object"]?.ToObject<ObjectRef>();
            var go = SceneObjectResolver.ResolveObject(objRef); // OBJECT_NOT_FOUND / INVALID_OBJECT_REF
            var scene = go.scene;

            Undo.DestroyObjectImmediate(go);
            if (scene.IsValid())
            {
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
            }

            return new { deleted = true };
        }

        public JObject GetParamsSchema()
        {
            return JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""object"": {
      ""type"": ""object"",
      ""description"": ""GameObject 引用;path 或 instanceId 至少提供一个。"",
      ""properties"": {
        ""path"": { ""type"": ""string"" },
        ""instanceId"": { ""type"": ""integer"" }
      }
    }
  },
  ""required"": [""object""]
}");
        }
    }
}
