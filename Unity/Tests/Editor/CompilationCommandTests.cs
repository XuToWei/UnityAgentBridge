using System.Linq;
using NUnit.Framework;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;

namespace AgentBridge.Tests
{
    /// <summary>
    /// 编译自检命令:recompile / get_compile_result。
    /// D6 边界:真实编译+domain reload 不在 EditMode 驱动(recompile 执行走活体验证);
    /// 这里测 CompilerMessage→CompileMessage 映射 + get_compile_result 读预置 SessionState(errors/warnings 拆分)。
    /// </summary>
    public sealed class CompilationCommandTests : BridgeTestBase
    {
        public override void TearDown()
        {
            SessionState.EraseString(CompileMonitor.StateKey); // 清掉预置的编译态
            base.TearDown();
        }

        private static void Seed(CompileResult r)
        {
            SessionState.SetString(CompileMonitor.StateKey, JsonConvert.SerializeObject(r));
        }

        [Test]
        public void Map_ErrorAndWarning()
        {
            var err = CompileMonitor.Map(new CompilerMessage
            {
                message = "; expected", file = "Assets/Foo.cs", line = 12, column = 9, type = CompilerMessageType.Error
            });
            Assert.AreEqual("error", err.Type);
            Assert.AreEqual("Assets/Foo.cs", err.File);
            Assert.AreEqual(12, err.Line);
            Assert.AreEqual(9, err.Column);
            Assert.AreEqual("; expected", err.Message);

            var warn = CompileMonitor.Map(new CompilerMessage { type = CompilerMessageType.Warning, message = "w" });
            Assert.AreEqual("warning", warn.Type);
        }

        [Test]
        public void GetCompileResult_SplitsErrorsAndWarnings()
        {
            Seed(new CompileResult
            {
                Compiling = false,
                CompiledAt = "2026-06-26T00:00:00.000Z",
                Messages =
                {
                    new CompileMessage { Type = "error", File = "Assets/A.cs", Line = 1, Message = "e" },
                    new CompileMessage { Type = "warning", File = "Assets/B.cs", Line = 2, Message = "w" }
                }
            });

            var r = Dispatch("get_compile_result");
            Assert.AreEqual("ok", r.Status);
            Assert.IsFalse(r.Result["compiling"].Value<bool>());
            Assert.AreEqual(1, r.Result["errorCount"].Value<int>());
            Assert.AreEqual(1, r.Result["warningCount"].Value<int>());
            Assert.AreEqual(1, ((JArray)r.Result["errors"]).Count);
            Assert.AreEqual(1, ((JArray)r.Result["warnings"]).Count);
            Assert.AreEqual("error", r.Result["errors"][0]["type"].Value<string>());
            Assert.AreEqual("warning", r.Result["warnings"][0]["type"].Value<string>());
            Assert.AreEqual("Assets/A.cs", r.Result["errors"][0]["file"].Value<string>());
        }

        [Test]
        public void GetCompileResult_EmptyWhenNoCompile()
        {
            SessionState.EraseString(CompileMonitor.StateKey);
            var r = Dispatch("get_compile_result");
            Assert.AreEqual("ok", r.Status);
            Assert.IsFalse(r.Result["compiling"].Value<bool>());
            Assert.AreEqual(JTokenType.Null, r.Result["compiledAt"].Type);
            Assert.AreEqual(0, r.Result["errorCount"].Value<int>());
            Assert.AreEqual(0, ((JArray)r.Result["errors"]).Count);
            Assert.AreEqual(0, ((JArray)r.Result["warnings"]).Count);
        }

        [Test]
        public void GetCompileResult_CompilingFlag()
        {
            Seed(new CompileResult { Compiling = true, CompiledAt = null });
            var r = Dispatch("get_compile_result");
            Assert.AreEqual("ok", r.Status);
            Assert.IsTrue(r.Result["compiling"].Value<bool>());
        }

        [Test]
        public void Commands_Registered()
        {
            var cmds = CommandRegistry.Commands.ToArray();
            Assert.Contains("recompile", cmds);
            Assert.Contains("get_compile_result", cmds);
        }
    }
}
