using Newtonsoft.Json;

namespace AgentBridge
{
    /// <summary>引用一个组件。对应 file-bridge roadmap 4.5。inspection 与 mutation 共享。</summary>
    public sealed class ComponentRef
    {
        [JsonProperty("object")] public ObjectRef Object { get; set; }
        [JsonProperty("type")] public string Type { get; set; }   // 组件类型名(全名或短名)
        [JsonProperty("index")] public int Index { get; set; }    // 同类型多个时的序号,默认 0
    }
}
