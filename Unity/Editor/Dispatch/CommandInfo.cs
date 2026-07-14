using Newtonsoft.Json.Linq;

namespace AgentBridge
{
    /// <summary>
    /// 单条命令的元数据,用于 list_commands 输出和命令集版本计算。
    /// CommandRegistry 每次投影时都会克隆 ParamsSchema,不会暴露注册快照内部 JObject。
    /// </summary>
    public struct CommandInfo
    {
        public string Command;
        public string Description;
        public JObject ParamsSchema;
        public bool BatchAllowed;
        public bool SupportsUndoCollapse;
    }
}
