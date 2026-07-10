using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AgentBridge
{
    /// <summary>
    /// Agent → Unity 的请求信封。
    /// 文件名约定 requests/{id}.request.json；文件名 id 是传输层规范身份。
    /// 请求体必须显式提供 v、id、command，且 id 与文件名完全一致。
    /// </summary>
    public sealed class Request
    {
        [JsonProperty("v")] public int V { get; set; }
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("command")] public string Command { get; set; }
        [JsonProperty("params")] public JObject Params { get; set; }
    }
}
