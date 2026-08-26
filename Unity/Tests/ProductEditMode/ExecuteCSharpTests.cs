using System;
using System.Collections;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace AgentBridge.Tests.ProductEditMode
{
    public sealed class ExecuteCSharpTests
    {
        [SetUp]
        public void SetUp()
        {
            ExecuteCSharpHandler.ResetAssemblyCountForTests();
            CommandRegistry.Rebuild();
        }

        [Test]
        public void CommandIsDiscoveredAndCannotRunInBatch()
        {
            var info = CommandRegistry.GetAll().Single(item => item.Command == "execute_csharp");
            Assert.That(info.BatchAllowed, Is.False);
            Assert.That(info.SupportsUndoCollapse, Is.False);

            Assert.That(CommandDispatcher.TryPrepare(
                "execute_csharp",
                new JObject { ["code"] = "context.ReturnValue = 1;" },
                CommandInvocationPolicy.BatchStep,
                out _,
                out var error), Is.False);
            Assert.That(error.Error.Code, Is.EqualTo("BATCH_COMMAND_NOT_ALLOWED"));
        }

        [Test]
        public void SchemaRejectsMissingAndUnknownParameters()
        {
            Assert.That(CommandDispatcher.TryPrepare(
                "execute_csharp",
                new JObject(),
                CommandInvocationPolicy.Single,
                out _,
                out var missing), Is.False);
            Assert.That(missing.Error.Code, Is.EqualTo(ErrorCodes.InvalidParams));

            Assert.That(CommandDispatcher.TryPrepare(
                "execute_csharp",
                new JObject { ["code"] = "context.ReturnValue = 1;", ["unexpected"] = true },
                CommandInvocationPolicy.Single,
                out _,
                out var unknown), Is.False);
            Assert.That(unknown.Error.Code, Is.EqualTo(ErrorCodes.InvalidParams));
        }

        [Test]
        public void SourceBuilderCreatesDeterministicModesAndLineMapping()
        {
            var body = ScriptSourceBuilder.Build("context.ReturnValue = 7;", "body");
            StringAssert.Contains("AgentBridgeGeneratedScript : IAgentScript", body);
            StringAssert.Contains("#line 1 \"AgentBridgeSnippet.cs\"", body);
            StringAssert.Contains("context.ReturnValue = 7;", body);

            var @class = ScriptSourceBuilder.Build(
                "public sealed class CustomScript : IAgentScript { public void Execute(AgentBridgeScriptContext context) {} }",
                "class");
            StringAssert.Contains("public sealed class CustomScript", @class);
            Assert.That(@class.Contains("AgentBridgeGeneratedScript"), Is.False);
        }

        [Test]
        public void SafetyPolicyBlocksBaseAndStrictRules()
        {
            Assert.That(ScriptSafetyPolicy.TryFindViolation(
                "System.IO.File.Delete(\"x\");", false, out var baseReason), Is.True);
            StringAssert.Contains("Delete", baseReason);

            Assert.That(ScriptSafetyPolicy.TryFindViolation(
                "System.IO.File.WriteAllText(\"Assets/x\", \"x\");", true, out var strictReason), Is.True);
            StringAssert.Contains("write", strictReason.ToLowerInvariant());

            Assert.That(ScriptSafetyPolicy.TryFindViolation(
                "System.IO.File.WriteAllText(\"Assets/x\", \"x\");", false, out _), Is.False);
        }

        [Test]
        public void CompilerPipelineFallsBackOnlyWhenUnavailable()
        {
            var environment = new ScriptCompilerEnvironment();
            var fallback = ScriptCompilerPipeline.Compile(
                "ignored",
                environment,
                new IScriptCompiler[]
                {
                    new FakeCompiler("Roslyn", ScriptCompilationStatus.Unavailable),
                    new FakeCompiler("CodeDom", ScriptCompilationStatus.Success)
                });
            Assert.That(fallback.Status, Is.EqualTo(ScriptCompilationStatus.Success));
            Assert.That(fallback.CompilerName, Is.EqualTo("CodeDom"));
            Assert.That(fallback.Attempts.Count, Is.EqualTo(2));

            var compileFailure = ScriptCompilerPipeline.Compile(
                "ignored",
                environment,
                new IScriptCompiler[]
                {
                    new FakeCompiler("Roslyn", ScriptCompilationStatus.CompilationFailed),
                    new ThrowingCompiler()
                });
            Assert.That(compileFailure.Status, Is.EqualTo(ScriptCompilationStatus.CompilationFailed));
            Assert.That(compileFailure.Attempts.Count, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator ExecuteBodyUsesRoslynAndReturnsStructuredData()
        {
            var task = CommandDispatcher.DispatchAsync(new Request
            {
                V = 1,
                Id = "execute-body",
                Command = "execute_csharp",
                Params = new JObject
                {
                    ["code"] = @"System.Collections.Generic.List<string> values = new() { ""a"", ""b"" };
context.Log(""count={0}"", values.Count);
context.ReturnValue = new JObject { [""value""] = values.Count switch { 2 => ""two"", _ => ""other"" } };"
                }
            });
            while (!task.IsCompleted)
            {
                yield return null;
            }
            var response = task.GetAwaiter().GetResult();

            Assert.That(response.Status, Is.EqualTo("ok"), response.Error?.Message);
            Assert.That(response.Result?["compiler"]?.Value<string>(), Is.EqualTo("Roslyn"));
            Assert.That(response.Result?["returnValue"]?["value"]?.Value<string>(), Is.EqualTo("two"));
            Assert.That(response.Result?["logs"]?[0]?["message"]?.Value<string>(), Is.EqualTo("count=2"));
        }

        [UnityTest]
        public IEnumerator CompilationFailureReturnsStructuredDiagnostics()
        {
            var task = CommandDispatcher.DispatchAsync(new Request
            {
                V = 1,
                Id = "execute-bad",
                Command = "execute_csharp",
                Params = new JObject { ["code"] = "context.ReturnValue = ;" }
            });
            while (!task.IsCompleted)
            {
                yield return null;
            }
            var response = task.GetAwaiter().GetResult();

            Assert.That(response.Status, Is.EqualTo("ok"));
            Assert.That(response.Result?["executed"]?.Value<bool>(), Is.False);
            Assert.That(response.Result?["code"]?.Value<string>(),
                Is.EqualTo(ScriptErrorCodes.CompilationFailed));
            Assert.That(response.Result?["compiler"]?.Value<string>(), Is.EqualTo("Roslyn"));
            Assert.That(response.Result?["diagnostics"]?.Any(), Is.True);
            Assert.That(response.Result?["diagnostics"]?[0]?["line"]?.Value<int>(), Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator RuntimeFailureUsesTypedError()
        {
            var task = CommandDispatcher.DispatchAsync(new Request
            {
                V = 1,
                Id = "execute-runtime",
                Command = "execute_csharp",
                Params = new JObject
                {
                    ["code"] = "throw new System.InvalidOperationException(\"boom\");"
                }
            });
            while (!task.IsCompleted)
            {
                yield return null;
            }
            var response = task.GetAwaiter().GetResult();

            Assert.That(response.Status, Is.EqualTo("error"));
            Assert.That(response.Error.Code, Is.EqualTo(ScriptErrorCodes.RuntimeException));
            StringAssert.Contains("boom", response.Error.Message);
        }

        [UnityTest]
        public IEnumerator ContextTracksCreationAndUndo()
        {
            var objectName = "AgentBridgeExecuteCSharp_" + Guid.NewGuid().ToString("N");
            var task = CommandDispatcher.DispatchAsync(new Request
            {
                V = 1,
                Id = "execute-track",
                Command = "execute_csharp",
                Params = new JObject
                {
                    ["code"] = $@"var go = new GameObject(""{objectName}"");
context.RegisterObjectCreation(go);
context.ReturnValue = SceneObjectResolver.Describe(go);"
                }
            });
            while (!task.IsCompleted)
            {
                yield return null;
            }
            var response = task.GetAwaiter().GetResult();

            Assert.That(response.Status, Is.EqualTo("ok"), response.Error?.Message);
            Assert.That(response.Result?["persistent"]?.Value<bool>(), Is.True);
            Assert.That(response.Result?["created"]?.Count(), Is.EqualTo(1));
            Assert.That(response.Result?["returnValue"]?["name"]?.Value<string>(), Is.EqualTo(objectName));
            var go = GameObject.Find(objectName);
            Assert.That(go, Is.Not.Null);

            Undo.PerformUndo();
            Assert.That(GameObject.Find(objectName), Is.Null);
        }

        [UnityTest]
        public IEnumerator InvalidReturnValueRollsBackTrackedCreation()
        {
            var objectName = "AgentBridgeExecuteCSharpBadResult_" + Guid.NewGuid().ToString("N");
            var task = CommandDispatcher.DispatchAsync(new Request
            {
                V = 1,
                Id = "execute-bad-result",
                Command = "execute_csharp",
                Params = new JObject
                {
                    ["code"] = $@"var go = new GameObject(""{objectName}"");
context.RegisterObjectCreation(go);
context.ReturnValue = go;"
                }
            });
            while (!task.IsCompleted)
            {
                yield return null;
            }
            var response = task.GetAwaiter().GetResult();

            Assert.That(response.Status, Is.EqualTo("error"));
            Assert.That(response.Error.Code, Is.EqualTo(ScriptErrorCodes.ResultUnsupported));
            Assert.That(GameObject.Find(objectName), Is.Null);
        }

        [UnityTest]
        public IEnumerator SafetyChecksAreEnabledByDefault()
        {
            var task = CommandDispatcher.DispatchAsync(new Request
            {
                V = 1,
                Id = "execute-blocked",
                Command = "execute_csharp",
                Params = new JObject { ["code"] = "System.IO.File.Delete(\"Assets/x\");" }
            });
            while (!task.IsCompleted)
            {
                yield return null;
            }
            var response = task.GetAwaiter().GetResult();

            Assert.That(response.Status, Is.EqualTo("error"));
            Assert.That(response.Error.Code, Is.EqualTo(ScriptErrorCodes.SafetyBlocked));
        }

        private sealed class FakeCompiler : IScriptCompiler
        {
            private readonly ScriptCompilationStatus m_Status;

            internal FakeCompiler(string name, ScriptCompilationStatus status)
            {
                Name = name;
                m_Status = status;
            }

            public string Name { get; }

            public ScriptCompilationResult Compile(string code, ScriptCompilerEnvironment environment)
            {
                return m_Status == ScriptCompilationStatus.Success
                    ? ScriptCompilationResult.Success(Name, new byte[] { 1 }, null, false)
                    : ScriptCompilationResult.Failure(m_Status, Name, m_Status.ToString());
            }
        }

        private sealed class ThrowingCompiler : IScriptCompiler
        {
            public string Name => "must-not-run";

            public ScriptCompilationResult Compile(string code, ScriptCompilerEnvironment environment)
            {
                throw new AssertionException("fallback must not run after a real compilation failure");
            }
        }
    }
}
