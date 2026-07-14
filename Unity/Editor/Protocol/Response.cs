using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AgentBridge
{
    /// <summary>
    /// Unity → Agent 的响应信封。
    /// Unity 原子发布到固定 response.json；Agent 完整读取后删除该文件作为确认。
    /// id 来自有效请求信封；请求无法提供有效 id 时返回空字符串。
    /// status=ok 时 error 为 null;status=error 时 result 为 null。
    /// </summary>
    internal sealed class Response
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
