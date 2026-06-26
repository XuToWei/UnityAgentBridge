using Newtonsoft.Json.Linq;

namespace AgentBridge
{
    /// <summary>单条命令的元数据,供 list_commands 输出 + commandsVersion hash 输入。对应 file-bridge roadmap 4.7。</summary>
    public struct CommandInfo
    {
        public string Command;
        public string Description;
        public JObject ParamsSchema; // null 表示该命令未声明 schema
    }
}
