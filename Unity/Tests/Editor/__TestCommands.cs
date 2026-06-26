using Newtonsoft.Json.Linq;

namespace AgentBridge.Tests
{
    // 测试专用命令 handler(`__` 前缀,仅测试程序集加载时存在)。
    // 用于注册/目录/启停/错误码测试,避免拿真命令(如 ping)做禁用测试污染状态。

    /// <summary>回显 params.msg。</summary>
    public sealed class TestEchoHandler : ICommandHandler
    {
        public string Command => "__test_echo";
        public string Description => "test: echo params.msg";
        public object Execute(JObject @params) => new { echo = @params?["msg"]?.Value<string>() ?? "" };
        public JObject GetParamsSchema() =>
            JObject.Parse(@"{""type"":""object"",""properties"":{""msg"":{""type"":""string""}}}");
    }

    /// <summary>抛普通异常 → 验证 HANDLER_EXCEPTION。</summary>
    public sealed class TestThrowHandler : ICommandHandler
    {
        public string Command => "__test_throw";
        public string Description => "test: throws plain exception";
        public object Execute(JObject @params) => throw new System.InvalidOperationException("boom");
        public JObject GetParamsSchema() => new JObject();
    }

    /// <summary>抛 CommandException → 验证自定义错误码透传。</summary>
    public sealed class TestCmdExHandler : ICommandHandler
    {
        public const string Code = "__TEST_CODE";
        public string Command => "__test_cmdex";
        public string Description => "test: throws CommandException";
        public object Execute(JObject @params) => throw new CommandException(Code, "deliberate");
        public JObject GetParamsSchema() => new JObject();
    }
}
