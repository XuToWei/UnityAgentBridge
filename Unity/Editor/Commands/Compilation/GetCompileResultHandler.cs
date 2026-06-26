using System.Linq;
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
        public string Description => "读最近一次编译结果:compiling/compiledAt/errorCount/warningCount/errors[]/warnings[]";

        public object Execute(JObject @params)
        {
            var result = CompileMonitor.Read();
            var errors = result.Messages.Where(m => m.Type == "error").ToArray();
            var warnings = result.Messages.Where(m => m.Type == "warning").ToArray();
            return new
            {
                compiling = result.Compiling,
                compiledAt = result.CompiledAt,
                errorCount = errors.Length,
                warningCount = warnings.Length,
                errors,
                warnings
            };
        }

        public JObject GetParamsSchema()
        {
            return new JObject(); // 无参 → 空 schema {}
        }
    }
}
