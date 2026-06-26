using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AgentBridge
{
    /// <summary>
    /// Unity → Agent 响应信封。对应 file-bridge roadmap 4.1。
    /// 文件名约定 responses/{id}.response.json。
    /// status=ok 时 error 为 null;status=error 时 result 为 null。
    /// </summary>
    public sealed class Response
    {
        [JsonProperty("v")] public int V { get; set; } = 1;
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("status")] public string Status { get; set; }
        [JsonProperty("result")] public JToken Result { get; set; }
        [JsonProperty("error")] public ErrorInfo Error { get; set; }
        [JsonProperty("commandsVersion")] public string CommandsVersion { get; set; } // 由 AgentBridgeHost 写响应前盖(见 4.7)
        [JsonProperty("timestamp")] public string Timestamp { get; set; }

        public static Response MakeOk(string id, object result)
        {
            return new Response
            {
                V = 1,
                Id = id,
                Status = "ok",
                Result = result == null ? JValue.CreateNull() : JToken.FromObject(result),
                Error = null,
                Timestamp = Now()
            };
        }

        public static Response MakeError(string id, string code, string message)
        {
            return new Response
            {
                V = 1,
                Id = id,
                Status = "error",
                Result = null,
                Error = new ErrorInfo { Code = code, Message = message },
                Timestamp = Now()
            };
        }

        private static string Now()
        {
            return DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        }
    }
}
