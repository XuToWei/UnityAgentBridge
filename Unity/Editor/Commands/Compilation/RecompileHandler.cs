using Newtonsoft.Json.Linq;
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

        public object Execute(JObject @params)
        {
            CompilationPipeline.RequestScriptCompilation();
            return new { requested = true };
        }

        public JObject GetParamsSchema()
        {
            return new JObject(); // 无参 → 空 schema {}
        }
    }
}
