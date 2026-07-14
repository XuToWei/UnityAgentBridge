using System;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.Compilation;

namespace AgentBridge
{
    /// <summary>
    /// 编译消息收集器(cmd-compile-check)。[InitializeOnLoad] 订阅 CompilationPipeline 三事件,
    /// 把编译 error/warning 收进 SessionState(跨 domain reload 存活、编辑器重启清)。命令侧只读 Read()。
    /// 对应 cmd-compile-check design D2/D4。
    /// </summary>
    [InitializeOnLoad]
    public static class CompileMonitor
    {
        public const string StateKey = "AgentBridge.CompileResult";

        static CompileMonitor()
        {
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
            CompilationPipeline.compilationFinished += OnCompilationFinished;
        }

        /// <summary>读最近一次编译快照(无记录返回空快照,Compiling=false)。</summary>
        public static CompileResult Read()
        {
            var json = SessionState.GetString(StateKey, "");
            if (string.IsNullOrEmpty(json))
            {
                return new CompileResult();
            }
            try
            {
                var result = JsonConvert.DeserializeObject<CompileResult>(json) ?? new CompileResult();
                if (result.Messages == null)
                {
                    result.Messages = new System.Collections.Generic.List<CompileMessage>();
                }
                return result;
            }
            catch
            {
                return new CompileResult();
            }
        }

        /// <summary>CompilerMessage → CompileMessage 映射(纯函数,供单测)。</summary>
        public static CompileMessage Map(CompilerMessage m)
        {
            return new CompileMessage
            {
                File = m.file,
                Line = m.line,
                Column = m.column,
                Message = m.message,
                Type = m.type == CompilerMessageType.Error ? "error" : "warning"
            };
        }

        private static void Write(CompileResult result)
        {
            SessionState.SetString(StateKey, JsonConvert.SerializeObject(result));
        }

        /// <summary>
        /// 在 RequestScriptCompilation 前建立新 generation,消除“请求后事件前”仍显示旧结果的窗口。
        /// </summary>
        public static CompileResult MarkRequested()
        {
            var previous = Read();
            var result = new CompileResult
            {
                Compiling = true,
                Generation = previous.Generation + 1,
                RequestedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                CompiledAt = null,
                RequestFailed = false,
                RequestError = null
            };
            Write(result);
            return result;
        }

        public static void MarkRequestFailed(int generation, string error)
        {
            var result = Read();
            if (result.Generation != generation)
            {
                return;
            }
            result.Compiling = false;
            result.CompiledAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            result.RequestFailed = true;
            result.RequestError = error;
            Write(result);
        }

        private static void OnCompilationStarted(object context)
        {
            var result = Read();
            if (!result.Compiling)
            {
                result = new CompileResult
                {
                    Generation = result.Generation + 1,
                    RequestedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
                };
            }
            result.Compiling = true;
            result.CompiledAt = null;
            result.RequestFailed = false;
            result.RequestError = null;
            result.Messages.Clear();
            Write(result);
        }

        private static void OnAssemblyCompilationFinished(string assemblyPath, CompilerMessage[] messages)
        {
            var result = Read();
            foreach (var m in messages)
            {
                if (m.type != CompilerMessageType.Error && m.type != CompilerMessageType.Warning)
                {
                    continue; // 只收 error/warning,忽略 info 等
                }
                result.Messages.Add(Map(m));
            }
            Write(result);
        }

        private static void OnCompilationFinished(object context)
        {
            var result = Read();
            result.Compiling = false;
            result.CompiledAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            Write(result);
        }
    }
}
