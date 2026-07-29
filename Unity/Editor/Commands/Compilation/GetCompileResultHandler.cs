using System;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace AgentBridge
{
    /// <summary>
    /// get_compile_result(只读):返回最近一次编译结果。读 CompileMonitor 收集进 SessionState 的快照,
    /// 按 type 把消息拆成 errors[]/warnings[]。尚无编译 → compiledAt:null、空数组;编译中 → compiling:true。
    /// 对应 cmd-compile-check design D5。
    /// </summary>
    public sealed class GetCompileResultHandler : ICommandHandler
    {
        public string Command => "get_compile_result";
        public string Description =>
            "读最近一次编译结果:包含错误/警告总数、有界明细、省略数量与截断状态";
        public string Group => "Compilation";
        public bool CanDisable => true;
        public CommandBatchMode BatchMode => CommandBatchMode.Allowed;

        public Task<object> ExecuteAsync(JObject @params)
        {
            var result = CompileMonitor.Read();
            var errors = result.Messages.Where(m => m.Type == "error").ToArray();
            var warnings = result.Messages.Where(m => m.Type == "warning").ToArray();
            var omittedErrorCount = Math.Max(0, result.ErrorCount - errors.Length);
            var omittedWarningCount = Math.Max(0, result.WarningCount - warnings.Length);
            return Task.FromResult<object>(new
            {
                compiling = result.Compiling,
                generation = result.Generation,
                requestedAt = result.RequestedAt,
                compiledAt = result.CompiledAt,
                requestFailed = result.RequestFailed,
                requestError = result.RequestError,
                errorCount = result.ErrorCount,
                warningCount = result.WarningCount,
                storedErrorCount = errors.Length,
                storedWarningCount = warnings.Length,
                omittedErrorCount,
                omittedWarningCount,
                diagnosticsTruncated = result.DiagnosticsTruncated ||
                                       omittedErrorCount > 0 ||
                                       omittedWarningCount > 0,
                storedDiagnosticBytes = result.StoredDiagnosticBytes,
                diagnosticByteBudget = CompileDiagnosticCollector.MaxStoredDiagnosticBytes,
                errors,
                warnings
            });
        }

        public JObject ParamsSchema { get; } = new JObject(); // 无参 → 空 schema {}
    }
}
