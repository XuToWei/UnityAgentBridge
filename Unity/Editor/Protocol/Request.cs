using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AgentBridge
{
    /// <summary>
    /// Agent → Unity 的请求信封。
    /// Agent 原子发布到固定 request.json，Unity Claim 后移动为 processing.json。
    /// 请求体必须显式提供 v、id、command；id 仅用于信封关联，不参与文件命名。
    /// </summary>
    internal sealed class Request
    {
        [JsonProperty("v")] public int V { get; set; }
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("command")] public string Command { get; set; }
        [JsonProperty("params")] public JObject Params { get; set; }
    }
}
