using System.Collections.Generic;
using Newtonsoft.Json;

namespace AgentBridge
{
    /// <summary>
    /// 一次编译的快照,持久化进 SessionState(跨 domain reload 存活)。
    /// get_compile_result 输出时把 messages 按 type 拆成 errors/warnings 两数组。对应 cmd-compile-check design 2.1。
    /// </summary>
    public sealed class CompileResult
    {
        [JsonProperty("compiling")] public bool Compiling { get; set; }
        [JsonProperty("generation")] public int Generation { get; set; }
        [JsonProperty("requestedAt")] public string RequestedAt { get; set; }
        [JsonProperty("compiledAt")] public string CompiledAt { get; set; }
        [JsonProperty("requestFailed")] public bool RequestFailed { get; set; }
        [JsonProperty("requestError")] public string RequestError { get; set; }
        [JsonProperty("errorCount")] public int ErrorCount { get; set; }
        [JsonProperty("warningCount")] public int WarningCount { get; set; }
        [JsonProperty("storedErrorCount")] public int StoredErrorCount { get; set; }
        [JsonProperty("storedWarningCount")] public int StoredWarningCount { get; set; }
        [JsonProperty("omittedErrorCount")] public int OmittedErrorCount { get; set; }
        [JsonProperty("omittedWarningCount")] public int OmittedWarningCount { get; set; }
        [JsonProperty("storedDiagnosticBytes")] public int StoredDiagnosticBytes { get; set; }
        [JsonProperty("diagnosticsTruncated")] public bool DiagnosticsTruncated { get; set; }
        [JsonProperty("messages")] public List<CompileMessage> Messages { get; set; } = new List<CompileMessage>();
    }
}
