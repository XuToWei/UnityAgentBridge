namespace AgentBridge
{
    /// <summary>命令管理器里一条命令的统一条目(命令管理器 EM1)。内置+扩展共用。</summary>
    public sealed class CommandEntry
    {
        public string Name { get; set; }          // ICommandHandler.Command
        public string Description { get; set; }
        public string Assembly { get; set; }      // Type.Assembly 名
        public bool IsBuiltin { get; set; }       // Assembly == "AgentBridge.Editor"
        public string ExtensionId { get; set; }   // 折射到的扩展 id(命令∈manifest.commands);无则 null
        public bool Enabled { get; set; }         // 不在全局禁用名单
    }
}
