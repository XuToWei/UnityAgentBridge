using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;

namespace AgentBridge
{
    /// <summary>
    /// recompile(编译):触发 Unity 脚本重编译(CompilationPipeline.RequestScriptCompilation())。
    /// 立即返回 {requested:true}——编译会引发 domain reload,无法同步等结果;结果经 get_compile_result 读。
    /// 对应 cmd-compile-check design D1/D3。
    /// </summary>
    public sealed class RecompileHandler : ICommandHandler
    {
        public string Command => "recompile";
        public string Description => "触发 Unity 脚本重编译(会引发 domain reload);立即返回,编译结果经 get_compile_result 读";
        public string Group => "Compilation";
        public bool CanDisable => true;
        public CommandBatchMode BatchMode => CommandBatchMode.NotAllowed;

        public async CommandTask<object> ExecuteAsync(JObject @params)
        {
            var current = CompileMonitor.Read();
            if (EditorApplication.isCompiling || current.Compiling)
            {
                throw new CommandException("RECOMPILE_BUSY",
                    $"Unity 已在编译 generation={current.Generation};请等待 get_compile_result.compiling=false 后重试");
            }
            var state = CompileMonitor.MarkRequested();
            try
            {
                CompilationPipeline.RequestScriptCompilation();
            }
            catch (System.Exception ex)
            {
                CompileMonitor.MarkRequestFailed(state.Generation, ex.Message);
                throw new CommandException("RECOMPILE_REQUEST_FAILED",
                    $"请求脚本重编译失败:{ex.Message}");
            }
            return new
            {
                requested = true,
                generation = state.Generation,
                requestedGeneration = state.Generation,
                requestedAt = state.RequestedAt
            };
        }

        public JObject ParamsSchema { get; } = new JObject(); // 无参 → 空 schema {}
    }
}
