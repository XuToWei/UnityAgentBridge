using Newtonsoft.Json.Linq;
using UnityEditor;

namespace AgentBridge
{
    /// <summary>refresh(资源写):触发 AssetDatabase.Refresh() 重扫工程,导入外部新增/改动的文件。无参,幂等。</summary>
    public sealed class RefreshHandler : ICommandHandler
    {
        public string Command => "refresh";
        public string Description => "触发 AssetDatabase.Refresh() 重扫工程(导入外部新增/改动文件)";

        public object Execute(JObject @params)
        {
            AssetDatabase.Refresh();
            return new { refreshed = true };
        }

        public JObject GetParamsSchema()
        {
            return JObject.Parse(@"{""type"":""object"",""properties"":{}}");
        }
    }
}
