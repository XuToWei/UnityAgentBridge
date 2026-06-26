using System.Linq;
using NUnit.Framework;
using Newtonsoft.Json.Linq;

namespace AgentBridge.Tests
{
    /// <summary>file-bridge 框架:CommandDispatcher 错误码映射、CommandRegistry 注册/Version/禁用名单。</summary>
    public sealed class DispatchTests : BridgeTestBase
    {
        public override void TearDown()
        {
            // 复原可能被测试禁用的命令(避免污染 EditorPrefs 全局禁用名单)。
            CommandToggle.SetEnabled("__test_echo", true);
            base.TearDown();
        }

        [Test]
        public void Dispatch_Ping_Ok() // sanity
        {
            var r = Dispatch("ping");
            Assert.AreEqual("ok", r.Status);
            Assert.AreEqual("pong", r.Result?["message"]?.Value<string>());
            Assert.IsNull(r.Error);
        }

        [Test]
        public void Dispatch_UnknownCommand()
        {
            var r = Dispatch("__no_such_cmd");
            Assert.AreEqual("error", r.Status);
            Assert.AreEqual(ErrorCodes.UnknownCommand, r.Error.Code);
        }

        [Test]
        public void Dispatch_MissingCommand_InvalidParams()
        {
            var r = CommandDispatcher.Dispatch(new Request { Id = "x", Command = "", Params = new JObject() });
            Assert.AreEqual("error", r.Status);
            Assert.AreEqual(ErrorCodes.InvalidParams, r.Error.Code);
        }

        [Test]
        public void Dispatch_HandlerException_Mapped()
        {
            var r = Dispatch("__test_throw");
            Assert.AreEqual("error", r.Status);
            Assert.AreEqual(ErrorCodes.HandlerException, r.Error.Code);
        }

        [Test]
        public void Dispatch_CommandException_PassesCode()
        {
            var r = Dispatch("__test_cmdex");
            Assert.AreEqual("error", r.Status);
            Assert.AreEqual(TestCmdExHandler.Code, r.Error.Code);
        }

        [Test]
        public void Dispatch_DisabledCommand_Rejected()
        {
            CommandToggle.SetEnabled("__test_echo", false);
            try
            {
                var r = Dispatch("__test_echo");
                Assert.AreEqual("error", r.Status);
                Assert.AreEqual(ErrorCodes.CommandDisabled, r.Error.Code);
            }
            finally { CommandToggle.SetEnabled("__test_echo", true); }
        }

        [Test]
        public void Registry_KnownCommandsPresent()
        {
            var cmds = CommandRegistry.Commands;
            Assert.Contains("ping", cmds.ToArray());
            Assert.Contains("list_commands", cmds.ToArray());
            Assert.Contains("__test_echo", cmds.ToArray());
        }

        [Test]
        public void Registry_IsDisabled_HandlerStillResolvable()
        {
            CommandToggle.SetEnabled("__test_echo", false);
            try
            {
                Assert.IsTrue(CommandRegistry.IsDisabled("__test_echo"));
                Assert.IsTrue(CommandRegistry.TryGet("__test_echo", out _), "禁用命令 handler 仍可 TryGet 到");
            }
            finally { CommandToggle.SetEnabled("__test_echo", true); }
        }

        [Test]
        public void Version_DeterministicAndChangesOnDisable()
        {
            var v1 = CommandRegistry.Version;
            Assert.AreEqual(v1, CommandRegistry.Version, "同命令集 Version 应确定");

            CommandToggle.SetEnabled("__test_echo", false);
            try
            {
                var v2 = CommandRegistry.Version;
                Assert.AreNotEqual(v1, v2, "禁用命令应改变 Version(可见集变)");
            }
            finally { CommandToggle.SetEnabled("__test_echo", true); }

            Assert.AreEqual(v1, CommandRegistry.Version, "启用后 Version 应恢复");
        }

        [Test]
        public void ListCommands_ExcludesDisabled()
        {
            bool Has(Response r, string name) =>
                r.Result["commands"].Any(c => c["command"]?.Value<string>() == name);

            var before = Dispatch("list_commands");
            Assert.IsTrue(Has(before, "__test_echo"));

            CommandToggle.SetEnabled("__test_echo", false);
            try
            {
                var after = Dispatch("list_commands");
                Assert.IsFalse(Has(after, "__test_echo"), "禁用命令应从 list_commands 剔除");
                Assert.AreNotEqual(
                    before.Result["commandsVersion"]?.Value<string>(),
                    after.Result["commandsVersion"]?.Value<string>(),
                    "禁用应改变 commandsVersion");
            }
            finally { CommandToggle.SetEnabled("__test_echo", true); }
        }
    }
}
