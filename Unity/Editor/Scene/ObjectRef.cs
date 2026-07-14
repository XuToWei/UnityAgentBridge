using Newtonsoft.Json;

namespace AgentBridge
{
    /// <summary>通过层级路径或 instanceId 指向一个 GameObject。</summary>
    public sealed class ObjectRef
    {
        [JsonProperty("path")] public string Path { get; set; }            // 路径段内 ~ 和 / 编码为 ~0/~1
        [JsonProperty("instanceId")] public int? InstanceId { get; set; }  // 有 path/scenePath 时交叉校验
        [JsonProperty("scenePath")] public string ScenePath { get; set; }  // 可选,用于 path 跨已加载场景消歧
    }
}
