using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;

namespace AgentBridge
{
    /// <summary>refresh(资源写):保存打开场景和资产后触发 AssetDatabase.Refresh()。</summary>
    public sealed class RefreshHandler : ICommandHandler
    {
        public string Command => "refresh";
        public string Description => "保存所有打开场景和资产后执行 AssetDatabase.Refresh()";
        public string Group => "Assets";
        public bool CanDisable => true;

        public object Execute(JObject @params)
        {
            EditorSceneManager.SaveOpenScenes();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return new { saved = true, refreshed = true };
        }

        public JObject GetParamsSchema()
        {
            return JObject.Parse(@"{""type"":""object"",""properties"":{}}");
        }
    }
}
