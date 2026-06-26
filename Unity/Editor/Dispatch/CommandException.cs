using System;

namespace AgentBridge
{
    /// <summary>handler 抛此异常以产生指定错误码的响应。对应 file-bridge roadmap 4.3。</summary>
    public sealed class CommandException : Exception
    {
        public string Code { get; }

        public CommandException(string code, string message) : base(message)
        {
            Code = code;
        }
    }
}
