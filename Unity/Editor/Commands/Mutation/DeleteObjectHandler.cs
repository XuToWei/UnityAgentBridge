using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

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
        public string Description => "删除 GameObject;EditMode 记录 Undo/标 dirty,PlayMode 删除运行时副本并返回 persistent=false";
        public string Group => "Mutation";
        public bool CanDisable => true;
        public CommandBatchMode BatchMode => CommandBatchMode.AllowedWithUndoCollapse;

        public Task<object> ExecuteAsync(JObject @params)
        {
            var persistent = ObjectMutationSupport.RequireStableState(Command);
            var objRef = @params?["object"]?.ToObject<ObjectRef>();
            var go = SceneObjectResolver.ResolveObject(objRef); // OBJECT_NOT_FOUND / INVALID_OBJECT_REF
            var scene = go.scene;

            using (var mutation = ObjectMutationSupport.BeginUndo(Command, persistent))
            {
                if (persistent)
                {
                    Undo.DestroyObjectImmediate(go);
                }
                else
                {
                    Object.DestroyImmediate(go);
                }
                if (persistent && scene.IsValid())
                {
                    UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
                }
                mutation.Complete();
            }

            return Task.FromResult<object>(new { deleted = true, persistent });
        }

        public JObject ParamsSchema { get; } = CreateParamsSchema();

        private static JObject CreateParamsSchema()
        {
            var objectRef = SceneObjectResolver.CreateObjectRefSchema();
            objectRef["description"] = "GameObject 引用;path 或 instanceId 至少提供一个。";
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject { ["object"] = objectRef },
                ["required"] = new JArray("object")
            };
        }
    }
}
