namespace AgentBridge
{
    /// <summary>execute_csharp 的结构化脚本入口。</summary>
    public interface IAgentScript
    {
        void Execute(AgentBridgeScriptContext context);
    }
}