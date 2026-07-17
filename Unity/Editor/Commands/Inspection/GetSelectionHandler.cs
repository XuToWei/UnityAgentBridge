using System.Threading.Tasks;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace AgentBridge
{
    /// <summary>get_selection(只读):返回编辑器当前选中的 GameObject(ObjectRef 列表)。空选中返回 []。</summary>
    public sealed class GetSelectionHandler : ICommandHandler
    {
        public string Command => "get_selection";
        public string Description => "返回编辑器当前选中的场景 GameObject 列表、active 对象与数量";
        public string Group => "Inspection";
        public bool CanDisable => true;
        public CommandBatchMode BatchMode => CommandBatchMode.Allowed;

        public Task<object> ExecuteAsync(JObject @params)
        {
            var all = Selection.gameObjects;
            var sceneObjects = all.Where(go => go != null && !EditorUtility.IsPersistent(go) && go.scene.IsValid()).ToArray();
            var selection = sceneObjects.Select(go => new
            {
                name = go.name,
                path = SceneObjectResolver.GetPath(go.transform),
                instanceId = go.GetInstanceID(),
                scenePath = go.scene.path
            }).ToArray();
            var active = Selection.activeGameObject;
            var activeRef = active != null && !EditorUtility.IsPersistent(active) && active.scene.IsValid()
                ? SceneObjectResolver.Describe(active)
                : null;
            return Task.FromResult<object>(new
            {
                count = selection.Length,
                selection,
                active = activeRef,
                ignoredPersistentCount = all.Length - sceneObjects.Length
            });
        }

        public JObject ParamsSchema { get; } =
            JObject.Parse(@"{""type"":""object"",""properties"":{}}");
    }
}
