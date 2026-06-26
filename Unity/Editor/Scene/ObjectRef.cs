using Newtonsoft.Json;

namespace AgentBridge
{
    /// <summary>引用一个 GameObject。对应 file-bridge roadmap 4.5。inspection 与 mutation 共享。</summary>
    public sealed class ObjectRef
    {
        [JsonProperty("path")] public string Path { get; set; }            // 层级路径 "Parent/Child/Leaf"
        [JsonProperty("instanceId")] public int? InstanceId { get; set; }  // GetInstanceID();优先于 path
    }
}
