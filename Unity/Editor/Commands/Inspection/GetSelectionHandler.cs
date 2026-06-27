using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace AgentBridge
{
    /// <summary>get_selection(只读):返回编辑器当前选中的 GameObject(ObjectRef 列表)。空选中返回 []。</summary>
    public sealed class GetSelectionHandler : ICommandHandler
    {
        public string Command => "get_selection";
        public string Description => "返回编辑器当前选中的 GameObject(name/path/instanceId 列表),空选中返回 []";
        public string Group => "Inspection";
        public bool CanDisable => true;

        public object Execute(JObject @params)
        {
            var selection = Selection.gameObjects.Select(go => new
            {
                name = go.name,
                path = SceneObjectResolver.GetPath(go.transform),
                instanceId = go.GetInstanceID()
            }).ToArray();
            return new { selection };
        }

        public JObject GetParamsSchema()
        {
            return JObject.Parse(@"{""type"":""object"",""properties"":{}}");
        }
    }
}
