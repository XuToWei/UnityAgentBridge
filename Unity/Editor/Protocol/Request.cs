using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AgentBridge
{
    /// <summary>
    /// Agent → Unity 请求信封。对应 file-bridge roadmap 4.1。
    /// 文件名约定 requests/{id}.request.json。
    /// </summary>
    public sealed class Request
    {
        [JsonProperty("v")] public int V { get; set; } = 1;
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("command")] public string Command { get; set; }
        [JsonProperty("params")] public JObject Params { get; set; }
        [JsonProperty("timestamp")] public string Timestamp { get; set; }
    }
}
