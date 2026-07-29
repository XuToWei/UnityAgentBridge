using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Compilation;

namespace AgentBridge.Tests.ProductEditMode
{
    public sealed class CompileMonitorTests
    {
        [TearDown]
        public void TearDown()
        {
            CompileMonitor.FinishCompilation();
            SessionState.EraseString(CompileMonitor.StateKey);
        }

        [Test]
        public void AssemblyCompletionStaysInMemoryUntilCompilationFinishes()
        {
            SessionState.EraseString(CompileMonitor.StateKey);
            CompileMonitor.BeginCompilation();
            var startState = SessionState.GetString(CompileMonitor.StateKey, "");

            CompileMonitor.CollectAssemblyMessages(
                new[]
                {
                    Message(CompilerMessageType.Error, "compile error")
                });

            Assert.That(CompileMonitor.Read().Messages, Has.Count.EqualTo(1));
            Assert.That(
                SessionState.GetString(CompileMonitor.StateKey, ""),
                Is.EqualTo(startState));

            CompileMonitor.FinishCompilation();

            var terminal = JsonConvert.DeserializeObject<CompileResult>(
                SessionState.GetString(CompileMonitor.StateKey, ""));
            Assert.That(terminal.Messages, Has.Count.EqualTo(1));
            Assert.That(terminal.Compiling, Is.False);
        }

        [Test]
        public void DiagnosticsAreBoundedErrorFirstAndExposeOmittedCounts()
        {
            SessionState.EraseString(CompileMonitor.StateKey);
            CompileMonitor.BeginCompilation();
            var messages = Enumerable.Range(0, 100)
                .Select(index => Message(
                    CompilerMessageType.Warning,
                    new string('w', 6000),
                    new string('p', 2000)))
                .Concat(Enumerable.Range(0, 250)
                    .Select(index => Message(
                        CompilerMessageType.Error,
                        new string('e', 6000),
                        new string('p', 2000))))
                .ToArray();

            CompileMonitor.CollectAssemblyMessages(messages);
            CompileMonitor.FinishCompilation();

            var payload = new GetCompileResultHandler()
                .ExecuteAsync(null)
                .GetAwaiter()
                .GetResult();
            var result = JObject.FromObject(payload);
            var errors = (JArray)result["errors"];
            var warnings = (JArray)result["warnings"];
            var responseBytes = Encoding.UTF8.GetByteCount(
                JsonConvert.SerializeObject(payload, Formatting.None));

            Assert.That(result["errorCount"]?.Value<int>(), Is.EqualTo(250));
            Assert.That(result["warningCount"]?.Value<int>(), Is.EqualTo(100));
            Assert.That(result["storedErrorCount"]?.Value<int>(), Is.EqualTo(errors.Count));
            Assert.That(result["storedWarningCount"]?.Value<int>(), Is.EqualTo(warnings.Count));
            Assert.That(result["omittedErrorCount"]?.Value<int>(),
                Is.EqualTo(250 - errors.Count));
            Assert.That(result["omittedWarningCount"]?.Value<int>(),
                Is.EqualTo(100 - warnings.Count));
            Assert.That(result["diagnosticsTruncated"]?.Value<bool>(), Is.True);
            Assert.That(errors.Count, Is.GreaterThan(0));
            Assert.That(warnings, Is.Empty);
            Assert.That(errors.All(item =>
                item["message"]?.Value<string>().Length <= 4096 &&
                item["file"]?.Value<string>().Length <= 1024), Is.True);
            Assert.That(responseBytes, Is.LessThan(FileChannel.MaxFileBytes));
        }

        [Test]
        public void NextCompilationClearsPreviousDiagnostics()
        {
            SessionState.EraseString(CompileMonitor.StateKey);
            CompileMonitor.BeginCompilation();
            CompileMonitor.CollectAssemblyMessages(
                new[] { Message(CompilerMessageType.Error, "previous") });
            CompileMonitor.FinishCompilation();
            Assert.That(CompileMonitor.Read().Messages, Has.Count.EqualTo(1));

            CompileMonitor.BeginCompilation();

            var current = CompileMonitor.Read();
            Assert.That(current.Messages, Is.Empty);
            Assert.That(current.ErrorCount, Is.Zero);
            Assert.That(current.WarningCount, Is.Zero);
        }

        [Test]
        public void ReadBoundsLegacySnapshotWithoutBudgetMetadata()
        {
            SessionState.EraseString(CompileMonitor.StateKey);
            var legacy = new CompileResult
            {
                Generation = 7,
                Messages = Enumerable.Range(0, 150)
                    .Select(index => new CompileMessage
                    {
                        File = "Assets/Legacy.cs",
                        Message = "warning",
                        Type = "warning"
                    })
                    .Concat(Enumerable.Range(0, 250)
                        .Select(index => new CompileMessage
                        {
                            File = "Assets/Legacy.cs",
                            Message = "error",
                            Type = "error"
                        }))
                    .ToList()
            };
            SessionState.SetString(
                CompileMonitor.StateKey,
                JsonConvert.SerializeObject(legacy));

            var loaded = CompileMonitor.Read();

            Assert.That(loaded.ErrorCount, Is.EqualTo(250));
            Assert.That(loaded.WarningCount, Is.EqualTo(150));
            Assert.That(loaded.StoredErrorCount, Is.EqualTo(200));
            Assert.That(loaded.StoredWarningCount, Is.EqualTo(100));
            Assert.That(loaded.OmittedErrorCount, Is.EqualTo(50));
            Assert.That(loaded.OmittedWarningCount, Is.EqualTo(50));
            Assert.That(loaded.DiagnosticsTruncated, Is.True);
        }

        private static CompilerMessage Message(
            CompilerMessageType type,
            string text,
            string file = "Assets/Test.cs")
        {
            return new CompilerMessage
            {
                file = file,
                line = 1,
                column = 1,
                message = text,
                type = type
            };
        }
    }
}
