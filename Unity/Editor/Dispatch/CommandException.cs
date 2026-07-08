using System;

namespace AgentBridge
{
    /// <summary>命令处理器抛此异常以产生指定错误码的响应。</summary>
    public sealed class CommandException : Exception
    {
        public string Code { get; }

        public CommandException(string code, string message) : base(message)
        {
            Code = code;
        }
    }
}
