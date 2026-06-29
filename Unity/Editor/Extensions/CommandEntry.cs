namespace AgentBridge
{
    /// <summary>命令管理器里一条命令的统一条目(命令管理器 EM1)。内置+扩展共用。</summary>
    public sealed class CommandEntry
    {
        public string Name { get; set; }          // ICommandHandler.Command
        public string Description { get; set; }
        public string Group { get; set; }         // ICommandHandler.Group(功能分组)
        public bool CanDisable { get; set; }      // ICommandHandler.CanDisable(能否被禁用)
        public string Assembly { get; set; }      // Type.Assembly 名
        public bool IsBuiltin { get; set; }       // Assembly == "AgentBridge.Editor"
        public bool Enabled { get; set; }         // 不在全局禁用名单
    }
}
