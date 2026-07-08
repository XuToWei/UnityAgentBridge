using Newtonsoft.Json.Linq;

namespace AgentBridge
{
    /// <summary>单条命令的元数据,用于 list_commands 输出和命令集版本计算。</summary>
    public struct CommandInfo
    {
        public string Command;
        public string Description;
        public JObject ParamsSchema; // null 表示该命令未声明 schema
    }
}
