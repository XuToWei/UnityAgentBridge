using Newtonsoft.Json;

namespace AgentBridge
{
    /// <summary>指向某个 GameObject 上指定类型、指定序号的组件。</summary>
    public sealed class ComponentRef
    {
        [JsonProperty("object")] public ObjectRef Object { get; set; }
        [JsonProperty("type")] public string Type { get; set; }   // 组件类型名(全名或短名)
        [JsonProperty("index")] public int Index { get; set; }    // 同类型多个时的序号,默认 0
    }
}
