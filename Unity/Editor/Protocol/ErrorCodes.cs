namespace AgentBridge
{
    /// <summary>框架级错误码。命令处理器可通过 CommandException 追加自定义错误码(建议带前缀)。</summary>
    public static class ErrorCodes
    {
        /// <summary>command 未注册。</summary>
        public const string UnknownCommand = "UNKNOWN_COMMAND";

        /// <summary>params 缺字段 / 类型错。</summary>
        public const string InvalidParams = "INVALID_PARAMS";

        /// <summary>请求信封违反协议（格式错误、缺字段、版本不支持或 id 不一致）。</summary>
        public const string InvalidRequest = "INVALID_REQUEST";

        /// <summary>handler 执行抛未分类异常(message 带堆栈摘要)。</summary>
        public const string HandlerException = "HANDLER_EXCEPTION";

        /// <summary>命令被命令管理器禁用(已注册但在禁用名单内,不执行)。</summary>
        public const string CommandDisabled = "COMMAND_DISABLED";

        /// <summary>请求已认领但响应未提交，执行结果未知。</summary>
        public const string Interrupted = "INTERRUPTED";

        /// <summary>命令结果序列化后超过文件通道允许的响应字节上限。</summary>
        public const string ResponseTooLarge = "RESPONSE_TOO_LARGE";

        /// <summary>框架内部错误。</summary>
        public const string InternalError = "INTERNAL_ERROR";
    }
}
