using Newtonsoft.Json;

namespace AgentBridge
{
    /// <summary>一条编译消息(error/warning)。对应 cmd-compile-check design 2.1。</summary>
    public sealed class CompileMessage
    {
        [JsonProperty("file")] public string File { get; set; }
        [JsonProperty("line")] public int Line { get; set; }
        [JsonProperty("column")] public int Column { get; set; }
        [JsonProperty("message")] public string Message { get; set; }
        [JsonProperty("type")] public string Type { get; set; } // "error" / "warning"
    }
}
