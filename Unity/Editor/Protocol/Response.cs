using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AgentBridge
{
    /// <summary>
    /// Unity → Agent 的响应信封。
    /// 文件名约定 responses/{id}.response.json，id 由已认领请求的文件名规范化。
    /// status=ok 时 error 为 null;status=error 时 result 为 null。
    /// </summary>
    public sealed class Response
    {
        [JsonProperty("v")] public int V { get; set; }
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("status")] public string Status { get; set; }
        [JsonProperty("result")] public JToken Result { get; set; }
        [JsonProperty("error")] public ErrorInfo Error { get; set; }
        [JsonProperty("commandsVersion")] public string CommandsVersion { get; set; }
        [JsonProperty("timestamp")] public string Timestamp { get; set; }
    }
}
