namespace AgentBridge
{
    /// <summary>框架级错误码。handler 可通过 CommandException 追加自有码(建议带前缀)。对应 file-bridge roadmap 4.1。</summary>
    public static class ErrorCodes
    {
        /// <summary>command 未注册。</summary>
        public const string UnknownCommand = "UNKNOWN_COMMAND";

        /// <summary>params 缺字段 / 类型错。</summary>
        public const string InvalidParams = "INVALID_PARAMS";

        /// <summary>handler 执行抛未分类异常(message 带堆栈摘要)。</summary>
        public const string HandlerException = "HANDLER_EXCEPTION";

        /// <summary>命令被扩展管理器禁用(已注册但在禁用名单内,不执行)。对应 extension-manager 4.6。</summary>
        public const string CommandDisabled = "COMMAND_DISABLED";

        /// <summary>请求处理中途遇 domain reload,重启后补发。</summary>
        public const string Interrupted = "INTERRUPTED";

        /// <summary>框架内部错误(解析失败等)。</summary>
        public const string InternalError = "INTERNAL_ERROR";
    }
}
