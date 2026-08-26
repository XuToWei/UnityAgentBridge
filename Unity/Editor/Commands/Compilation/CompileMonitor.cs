using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.Compilation;

namespace AgentBridge
{
    /// <summary>
    /// 编译消息收集器(cmd-compile-check)。[InitializeOnLoad] 订阅 CompilationPipeline 三事件,
    /// 编译期间在内存有界累计 error/warning，开始与结束时写入 SessionState
    /// (跨 domain reload 存活、编辑器重启清)。命令侧只读 Read()。
    /// 对应 cmd-compile-check design D2/D4。
    /// </summary>
    [InitializeOnLoad]
    public static class CompileMonitor
    {
        public const string StateKey = "AgentBridge.CompileResult";
        private static CompileResult s_ActiveResult;

        static CompileMonitor()
        {
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
            CompilationPipeline.compilationFinished += OnCompilationFinished;
        }

        /// <summary>读最近一次编译快照(无记录返回空快照,Compiling=false)。</summary>
        public static CompileResult Read()
        {
            return s_ActiveResult == null
                ? ReadPersisted()
                : Clone(s_ActiveResult);
        }

        private static CompileResult ReadPersisted()
        {
            var json = SessionState.GetString(StateKey, "");
            if (string.IsNullOrEmpty(json))
            {
                return new CompileResult();
            }
            try
            {
                var result = JsonConvert.DeserializeObject<CompileResult>(json) ?? new CompileResult();
                CompileDiagnosticCollector.NormalizeLoaded(result);
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
            CompileDiagnosticCollector.Reset(result);
            s_ActiveResult = result;
            Write(result);
            return Clone(result);
        }

        public static void MarkRequestFailed(int generation, string error)
        {
            var result = s_ActiveResult ?? ReadPersisted();
            if (result.Generation != generation)
            {
                return;
            }
            result.Compiling = false;
            result.CompiledAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            result.RequestFailed = true;
            result.RequestError = CompileDiagnosticCollector.TruncateMessage(error);
            Write(result);
            s_ActiveResult = null;
        }

        private static void OnCompilationStarted(object context)
        {
            BeginCompilation();
        }

        internal static void BeginCompilation()
        {
            var result = s_ActiveResult ?? ReadPersisted();
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
            CompileDiagnosticCollector.Reset(result);
            s_ActiveResult = result;
            Write(result);
        }

        private static void OnAssemblyCompilationFinished(string assemblyPath, CompilerMessage[] messages)
        {
            CollectAssemblyMessages(messages);
        }

        internal static void CollectAssemblyMessages(CompilerMessage[] messages)
        {
            var result = s_ActiveResult ?? ReadPersisted();
            if (!result.Compiling)
            {
                return;
            }

            s_ActiveResult = result;
            CompileDiagnosticCollector.Append(result, messages);
        }

        private static void OnCompilationFinished(object context)
        {
            FinishCompilation();
        }

        internal static void FinishCompilation()
        {
            var result = s_ActiveResult ?? ReadPersisted();
            result.Compiling = false;
            result.CompiledAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            Write(result);
            s_ActiveResult = null;
        }

        private static CompileResult Clone(CompileResult source)
        {
            var messages = new List<CompileMessage>(source.Messages.Count);
            foreach (var message in source.Messages)
            {
                messages.Add(new CompileMessage
                {
                    File = message.File,
                    Line = message.Line,
                    Column = message.Column,
                    Message = message.Message,
                    Type = message.Type
                });
            }

            return new CompileResult
            {
                Compiling = source.Compiling,
                Generation = source.Generation,
                RequestedAt = source.RequestedAt,
                CompiledAt = source.CompiledAt,
                RequestFailed = source.RequestFailed,
                RequestError = source.RequestError,
                ErrorCount = source.ErrorCount,
                WarningCount = source.WarningCount,
                StoredErrorCount = source.StoredErrorCount,
                StoredWarningCount = source.StoredWarningCount,
                OmittedErrorCount = source.OmittedErrorCount,
                OmittedWarningCount = source.OmittedWarningCount,
                StoredDiagnosticBytes = source.StoredDiagnosticBytes,
                DiagnosticsTruncated = source.DiagnosticsTruncated,
                Messages = messages
            };
        }
    }
}
