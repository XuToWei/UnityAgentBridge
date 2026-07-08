using Newtonsoft.Json;

namespace AgentBridge
{
    /// <summary>通过层级路径或 instanceId 指向一个 GameObject。</summary>
    public sealed class ObjectRef
    {
        [JsonProperty("path")] public string Path { get; set; }            // 层级路径 "Parent/Child/Leaf"
        [JsonProperty("instanceId")] public int? InstanceId { get; set; }  // GetInstanceID();优先于 path
    }
}
