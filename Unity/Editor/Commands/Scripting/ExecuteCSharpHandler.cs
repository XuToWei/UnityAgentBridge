using System;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace AgentBridge
{
    /// <summary>
    /// execute_csharp:不写入 Assets、不请求项目重编译，使用 Unity 自带编译器动态编译并执行 C#。
    /// Roslyn 会短暂使用 OS 临时目录，最终程序集从字节加载，直到下一次 domain reload 才释放。
    /// </summary>
    public sealed class ExecuteCSharpHandler : ICommandHandler
    {
        internal const int MaxSourceBytes = 128 * 1024;
        internal const int MaxReturnValueBytes = 64 * 1024;
        internal const int MaxLoadedAssemblies = 128;

        private static int s_LoadedAssemblyCount;

        public string Command => "execute_csharp";
        public string Description => "无需创建工程脚本或触发 domain reload，动态编译并在 Unity Editor 主线程执行 C#；mode=body 时 code 是可直接使用 context 的方法体语句，mode=class 时 code 必须包含唯一的 IAgentScript 实现；context 提供 JSON 返回值、结构化日志及带 Undo 的对象变更追踪；安全检查仅为防御性 blocklist，不是沙箱";
        public string Group => "Scripting";
        public bool CanDisable => true;
        public CommandBatchMode BatchMode => CommandBatchMode.NotAllowed;

        public async Task<object> ExecuteAsync(JObject @params)
        {
            var mainThreadId = Thread.CurrentThread.ManagedThreadId;
            var code = @params?["code"]?.Value<string>() ?? string.Empty;
            var mode = @params?["mode"]?.Value<string>() ?? "body";
            var safetyChecks = @params?["safetyChecks"]?.Value<bool>() ?? true;
            var strictFilesystemChecks =
                @params?["strictFilesystemChecks"]?.Value<bool>() ?? true;

            if (string.IsNullOrWhiteSpace(code))
            {
                throw new CommandException(ErrorCodes.InvalidParams,
                    "code must contain non-whitespace C# source");
            }
            if (Encoding.UTF8.GetByteCount(code) > MaxSourceBytes)
            {
                throw new CommandException(ErrorCodes.InvalidParams,
                    $"code exceeds the {MaxSourceBytes}-byte UTF-8 limit");
            }
            if (safetyChecks && ScriptSafetyPolicy.TryFindViolation(
                    code, strictFilesystemChecks, out var reason))
            {
                throw new CommandException(ScriptErrorCodes.SafetyBlocked, reason);
            }

            var persistent = RequireReadyState();
            if (s_LoadedAssemblyCount >= MaxLoadedAssemblies)
            {
                throw new CommandException(
                    ScriptErrorCodes.AssemblyLimitReached,
                    $"execute_csharp loaded-assembly limit ({MaxLoadedAssemblies}) reached; " +
                    "complete a normal recompile exchange or restart Unity before compiling more snippets");
            }

            var source = ScriptSourceBuilder.Build(code, mode);
            var environment = ScriptCompilerEnvironment.Capture();
            var compilation = await Task.Run(
                () => ScriptCompilerPipeline.Compile(source, environment));

            if (Thread.CurrentThread.ManagedThreadId != mainThreadId)
            {
                throw new CommandException(
                    ScriptErrorCodes.InternalThreadError,
                    "dynamic compilation did not resume on the Unity Editor thread");
            }
            persistent = RequireReadyState();
            if (!IsCompilationSuccessful(compilation))
            {
                return BuildCompilationFailureResult(compilation, mode, persistent);
            }

            if (s_LoadedAssemblyCount >= MaxLoadedAssemblies)
            {
                throw new CommandException(
                    ScriptErrorCodes.AssemblyLimitReached,
                    $"execute_csharp loaded-assembly limit ({MaxLoadedAssemblies}) reached while compiling");
            }

            Assembly assembly;
            try
            {
                assembly = Assembly.Load(compilation.AssemblyBytes);
                s_LoadedAssemblyCount++;
            }
            catch (Exception ex)
            {
                throw new CommandException(
                    ScriptErrorCodes.AssemblyLoadFailed,
                    $"failed to load compiled assembly: {ex.GetType().Name}: {ex.Message}");
            }

            var scriptType = ResolveScriptType(assembly);
            IAgentScript script;
            try
            {
                script = (IAgentScript)Activator.CreateInstance(scriptType);
            }
            catch (Exception ex)
            {
                var root = Unwrap(ex);
                throw new CommandException(
                    ScriptErrorCodes.InstantiationFailed,
                    $"failed to instantiate {scriptType.FullName}: {root.GetType().Name}: {root.Message}");
            }

            AgentBridgeScriptContext context;
            JToken returnValue;
            using (var undo = ObjectMutationSupport.BeginUndo(Command, persistent))
            {
                context = new AgentBridgeScriptContext(persistent, undo);
                try
                {
                    script.Execute(context);
                }
                catch (ScriptContextException ex)
                {
                    throw new CommandException(ex.Code, ex.Message);
                }
                catch (Exception ex)
                {
                    var root = Unwrap(ex);
                    throw new CommandException(
                        ScriptErrorCodes.RuntimeException,
                        $"{root.GetType().FullName}: {root.Message}");
                }

                returnValue = NormalizeReturnValue(context.ReturnValue);
                undo.Complete();
            }

            var warnings = compilation.Diagnostics
                .Where(item => string.Equals(item.severity, "warning", StringComparison.Ordinal))
                .ToArray();
            return new
            {
                executed = true,
                compiler = compilation.CompilerName,
                compilerAttempts = compilation.Attempts,
                mode,
                persistent,
                warnings,
                warningsTruncated = compilation.DiagnosticsTruncated,
                logs = context.Logs,
                logsTruncated = context.LogsTruncated,
                created = context.Created,
                modified = context.Modified,
                destroyed = context.Destroyed,
                trackingTruncated = context.TrackingTruncated,
                returnValue
            };
        }

        private static bool RequireReadyState()
        {
            if (EditorApplication.isPlaying != EditorApplication.isPlayingOrWillChangePlaymode)
            {
                throw new CommandException(
                    ScriptErrorCodes.EditorBusy,
                    "Unity is transitioning into or out of Play Mode; retry when the state is stable");
            }
            var compileState = CompileMonitor.Read();
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || compileState.Compiling)
            {
                throw new CommandException(
                    ScriptErrorCodes.EditorBusy,
                    "Unity is compiling or importing; wait for get_compile_result.compiling=false and retry");
            }
            return !EditorApplication.isPlaying;
        }

        private static bool IsCompilationSuccessful(ScriptCompilationResult compilation)
        {
            return compilation != null &&
                   compilation.Status == ScriptCompilationStatus.Success &&
                   compilation.AssemblyBytes != null;
        }

        private static object BuildCompilationFailureResult(
            ScriptCompilationResult compilation,
            string mode,
            bool persistent)
        {
            var status = compilation?.Status ?? ScriptCompilationStatus.Unavailable;
            string code;
            switch (status)
            {
                case ScriptCompilationStatus.CompilationFailed:
                    code = ScriptErrorCodes.CompilationFailed;
                    break;
                case ScriptCompilationStatus.TimedOut:
                    code = ScriptErrorCodes.CompilerTimeout;
                    break;
                case ScriptCompilationStatus.OutputTooLarge:
                    code = ScriptErrorCodes.OutputTooLarge;
                    break;
                default:
                    code = ScriptErrorCodes.CompilerUnavailable;
                    break;
            }

            return new
            {
                executed = false,
                code,
                message = compilation?.Message ?? "dynamic compiler returned no result",
                compiler = compilation?.CompilerName,
                compilerAttempts = compilation?.Attempts ?? new System.Collections.Generic.List<ScriptCompilerAttempt>(),
                mode,
                persistent,
                diagnostics = compilation?.Diagnostics ?? new System.Collections.Generic.List<ScriptCompilationDiagnostic>(),
                diagnosticsTruncated = compilation?.DiagnosticsTruncated ?? false
            };
        }

        private static Type ResolveScriptType(Assembly assembly)
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(type => type != null).ToArray();
            }

            var candidates = types
                .Where(type => typeof(IAgentScript).IsAssignableFrom(type) &&
                               !type.IsInterface && !type.IsAbstract)
                .ToArray();
            if (candidates.Length == 0)
            {
                throw new CommandException(
                    ScriptErrorCodes.ContractNotFound,
                    "compiled assembly contains no IAgentScript implementation");
            }
            if (candidates.Length > 1)
            {
                throw new CommandException(
                    ScriptErrorCodes.ContractAmbiguous,
                    "compiled assembly contains multiple IAgentScript implementations: " +
                    string.Join(", ", candidates.Select(type => type.FullName)));
            }

            var candidate = candidates[0];
            if (!(candidate.IsPublic || candidate.IsNestedPublic) || candidate.ContainsGenericParameters ||
                candidate.GetConstructor(Type.EmptyTypes) == null)
            {
                throw new CommandException(
                    ScriptErrorCodes.ContractInvalid,
                    $"{candidate.FullName} must be public, non-generic, and have a public parameterless constructor");
            }
            return candidate;
        }

        private static JToken NormalizeReturnValue(object value)
        {
            if (value == null)
            {
                return JValue.CreateNull();
            }
            if (value is UnityEngine.Object unityObject)
            {
                throw new CommandException(
                    ScriptErrorCodes.ResultUnsupported,
                    $"returning UnityEngine.Object ({unityObject.GetType().FullName}) is unsupported; " +
                    "return a SceneObjectResolver description or JSON value instead");
            }

            JToken token;
            try
            {
                if (value is JToken existing)
                {
                    token = existing.DeepClone();
                }
                else
                {
                    var serializer = JsonSerializer.Create(new JsonSerializerSettings
                    {
                        ReferenceLoopHandling = ReferenceLoopHandling.Error,
                        MaxDepth = 32
                    });
                    token = JToken.FromObject(value, serializer);
                }
                ValidateJsonToken(token);
            }
            catch (CommandException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new CommandException(
                    ScriptErrorCodes.ResultUnsupported,
                    $"returnValue is not safely JSON-serializable: {ex.GetType().Name}: {ex.Message}");
            }

            var bytes = Encoding.UTF8.GetByteCount(token.ToString(Formatting.None));
            if (bytes > MaxReturnValueBytes)
            {
                throw new CommandException(
                    ScriptErrorCodes.ResultUnsupported,
                    $"returnValue exceeds the {MaxReturnValueBytes}-byte JSON limit");
            }
            return token;
        }

        private static void ValidateJsonToken(JToken token)
        {
            switch (token.Type)
            {
                case JTokenType.Object:
                case JTokenType.Array:
                    foreach (var child in token.Children())
                    {
                        ValidateJsonToken(child is JProperty property ? property.Value : child);
                    }
                    return;
                case JTokenType.Integer:
                case JTokenType.String:
                case JTokenType.Boolean:
                case JTokenType.Null:
                    return;
                case JTokenType.Float:
                    var number = token.Value<double>();
                    if (!double.IsNaN(number) && !double.IsInfinity(number))
                    {
                        return;
                    }
                    break;
            }
            throw new CommandException(
                ScriptErrorCodes.ResultUnsupported,
                $"returnValue contains unsupported JSON token type {token.Type}");
        }

        private static Exception Unwrap(Exception exception)
        {
            while (exception is TargetInvocationException invocation && invocation.InnerException != null)
            {
                exception = invocation.InnerException;
            }
            return exception;
        }

        internal static void ResetAssemblyCountForTests()
        {
            s_LoadedAssemblyCount = 0;
        }

        public JObject ParamsSchema { get; } = JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""code"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 131072 },
    ""mode"": { ""type"": ""string"", ""enum"": [""body"", ""class""], ""default"": ""body"" },
    ""safetyChecks"": { ""type"": ""boolean"", ""default"": true },
    ""strictFilesystemChecks"": { ""type"": ""boolean"", ""default"": true }
  },
  ""required"": [""code""],
  ""additionalProperties"": false
}");
    }
}
