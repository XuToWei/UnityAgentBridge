using Newtonsoft.Json;

namespace AgentBridge
{
    /// <summary>响应中的错误体,status=error 时存在。</summary>
    public sealed class ErrorInfo
    {
        [JsonProperty("code")] public string Code { get; set; }
        [JsonProperty("message")] public string Message { get; set; }
    }
}
