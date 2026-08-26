using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace AgentBridge
{
    internal enum ScriptCompilationStatus
    {
        Success,
        CompilationFailed,
        Unavailable,
        TimedOut,
        OutputTooLarge
    }

    internal sealed class ScriptCompilationDiagnostic
    {
        public int line;
        public int column;
        public string severity;
        public string code;
        public string message;
    }

    internal sealed class ScriptCompilerAttempt
    {
        public string compiler;
        public string status;
        public string message;
    }

    internal sealed class ScriptCompilationResult
    {
        internal ScriptCompilationStatus Status { get; private set; }
        internal string CompilerName { get; private set; }
        internal byte[] AssemblyBytes { get; private set; }
        internal List<ScriptCompilationDiagnostic> Diagnostics { get; private set; }
        internal bool DiagnosticsTruncated { get; private set; }
        internal string Message { get; private set; }
        internal List<ScriptCompilerAttempt> Attempts { get; private set; }

        internal static ScriptCompilationResult Success(
            string compiler,
            byte[] bytes,
            List<ScriptCompilationDiagnostic> diagnostics,
            bool diagnosticsTruncated)
        {
            return new ScriptCompilationResult
            {
                Status = ScriptCompilationStatus.Success,
                CompilerName = compiler,
                AssemblyBytes = bytes,
                Diagnostics = diagnostics ?? new List<ScriptCompilationDiagnostic>(),
                DiagnosticsTruncated = diagnosticsTruncated,
                Message = "Compiled successfully."
            };
        }

        internal static ScriptCompilationResult Failure(
            ScriptCompilationStatus status,
            string compiler,
            string message,
            List<ScriptCompilationDiagnostic> diagnostics = null,
            bool diagnosticsTruncated = false)
        {
            return new ScriptCompilationResult
            {
                Status = status,
                CompilerName = compiler,
                Diagnostics = diagnostics ?? new List<ScriptCompilationDiagnostic>(),
                DiagnosticsTruncated = diagnosticsTruncated,
                Message = message
            };
        }

        internal ScriptCompilationResult WithAttempts(List<ScriptCompilerAttempt> attempts)
        {
            Attempts = attempts ?? new List<ScriptCompilerAttempt>();
            return this;
        }
    }

    internal sealed class ScriptCompilerEnvironment
    {
        internal string CompilerHostPath;
        internal string CscPath;
        internal string MonoLibRoot;
        internal string[] References;
        internal string ToolchainError;

        internal static ScriptCompilerEnvironment Capture()
        {
            var environment = new ScriptCompilerEnvironment
            {
                References = CaptureReferences()
            };

            if (!TryResolveToolchain(
                    out environment.CompilerHostPath,
                    out environment.CscPath,
                    out environment.MonoLibRoot,
                    out environment.ToolchainError))
            {
                environment.CompilerHostPath = null;
                environment.CscPath = null;
            }
            return environment;
        }

        private static string[] CaptureReferences()
        {
            var references = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly == null || assembly.IsDynamic)
                {
                    continue;
                }
                try
                {
                    var location = assembly.Location;
                    var identity = assembly.GetName().FullName;
                    if (!string.IsNullOrEmpty(location) && File.Exists(location) &&
                        !string.IsNullOrEmpty(identity) && !references.ContainsKey(identity))
                    {
                        references.Add(identity, location);
                    }
                }
                catch
                {
                    // Some editor assemblies do not expose a physical location.
                }
            }

            return references.Values
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static bool TryResolveToolchain(
            out string compilerHostPath,
            out string cscPath,
            out string monoLibRoot,
            out string error)
        {
            compilerHostPath = null;
            cscPath = null;
            monoLibRoot = FindFirstExisting(GetMonoLibRootCandidates());

            foreach (var root in GetUnityToolRoots())
            {
                var mono = Path.Combine(root, "MonoBleedingEdge", "bin", MonoExecutableName());
                var csc = Path.Combine(root, "MonoBleedingEdge", "lib", "mono", "msbuild",
                    "Current", "bin", "Roslyn", "csc.exe");
                if (File.Exists(mono) && File.Exists(csc))
                {
                    compilerHostPath = mono;
                    cscPath = csc;
                    break;
                }
            }

            if (compilerHostPath == null || cscPath == null)
            {
                compilerHostPath = FindFirstExisting(
                    GetUnityToolRoots().Select(root =>
                        Path.Combine(root, "NetCoreRuntime", DotnetExecutableName())));
                cscPath = FindFirstExisting(
                    GetUnityToolRoots().Select(root =>
                        Path.Combine(root, "DotNetSdkRoslyn", "csc.dll")));
            }

            if (string.IsNullOrEmpty(compilerHostPath))
            {
                error = "Unity compiler host executable was not found.";
                return false;
            }
            if (string.IsNullOrEmpty(cscPath))
            {
                error = "Unity Roslyn compiler was not found.";
                return false;
            }
            if (string.IsNullOrEmpty(monoLibRoot))
            {
                error = "Unity Mono reference profile was not found.";
                return false;
            }

            error = null;
            return true;
        }

        private static List<string> GetUnityToolRoots()
        {
            var roots = new List<string>();
            AddRoot(roots, EditorApplication.applicationContentsPath);
            try
            {
                var directory = new DirectoryInfo(EditorApplication.applicationContentsPath);
                if (string.Equals(directory.Name, "Resources", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(directory.Name, "Scripting", StringComparison.OrdinalIgnoreCase))
                {
                    AddRoot(roots, directory.Parent?.FullName);
                    AddRoot(roots, directory.Parent?.Parent?.FullName);
                }
            }
            catch
            {
            }

            foreach (var root in roots.ToArray())
            {
                AddRoot(roots, Path.Combine(root, "Resources", "Scripting"));
            }
            return roots;
        }

        private static IEnumerable<string> GetMonoLibRootCandidates()
        {
            var suffix = Application.platform == RuntimePlatform.WindowsEditor
                ? "win32"
                : Application.platform == RuntimePlatform.LinuxEditor ? "linux" : "macos";
            foreach (var root in GetUnityToolRoots())
            {
                var mono = Path.Combine(root, "MonoBleedingEdge", "lib", "mono");
                yield return Path.Combine(mono, "net_4_x-" + suffix);
                yield return Path.Combine(mono, "unityjit-" + suffix);
                yield return Path.Combine(mono, "unity");
                yield return Path.Combine(mono, "4.8-api");
                yield return Path.Combine(mono, "4.7.2-api");
                yield return Path.Combine(mono, "4.7.1-api");
            }
        }

        private static void AddRoot(List<string> roots, string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }
            try
            {
                var full = Path.GetFullPath(path);
                if (!roots.Any(existing =>
                        string.Equals(existing, full, StringComparison.OrdinalIgnoreCase)))
                {
                    roots.Add(full);
                }
            }
            catch
            {
            }
        }

        private static string FindFirstExisting(IEnumerable<string> candidates)
        {
            foreach (var candidate in candidates)
            {
                if (!string.IsNullOrEmpty(candidate) &&
                    (File.Exists(candidate) || Directory.Exists(candidate)))
                {
                    return candidate;
                }
            }
            return null;
        }

        private static string DotnetExecutableName()
        {
            return Application.platform == RuntimePlatform.WindowsEditor ? "dotnet.exe" : "dotnet";
        }

        private static string MonoExecutableName()
        {
            return Application.platform == RuntimePlatform.WindowsEditor ? "mono.exe" : "mono";
        }
    }

    internal interface IScriptCompiler
    {
        string Name { get; }
        ScriptCompilationResult Compile(string code, ScriptCompilerEnvironment environment);
    }

    internal static class ScriptCompilerPipeline
    {
        internal const int MaxAssemblyBytes = 8 * 1024 * 1024;

        internal static ScriptCompilationResult Compile(
            string code,
            ScriptCompilerEnvironment environment)
        {
            return Compile(code, environment, new IScriptCompiler[]
            {
                new RoslynCscScriptCompiler(),
                new CodeDomScriptCompiler()
            });
        }

        internal static ScriptCompilationResult Compile(
            string code,
            ScriptCompilerEnvironment environment,
            IEnumerable<IScriptCompiler> compilers)
        {
            var attempts = new List<ScriptCompilerAttempt>();
            ScriptCompilationResult lastUnavailable = null;
            foreach (var compiler in compilers ?? Array.Empty<IScriptCompiler>())
            {
                if (compiler == null)
                {
                    continue;
                }

                ScriptCompilationResult result;
                try
                {
                    result = compiler.Compile(code, environment);
                }
                catch (Exception ex)
                {
                    result = ScriptCompilationResult.Failure(
                        ScriptCompilationStatus.Unavailable,
                        compiler.Name,
                        ex.Message);
                }

                attempts.Add(new ScriptCompilerAttempt
                {
                    compiler = compiler.Name,
                    status = ToWireStatus(result.Status),
                    message = result.Message
                });

                if (result.Status == ScriptCompilationStatus.Success ||
                    result.Status == ScriptCompilationStatus.CompilationFailed ||
                    result.Status == ScriptCompilationStatus.OutputTooLarge)
                {
                    return result.WithAttempts(attempts);
                }

                lastUnavailable = result;
            }

            return (lastUnavailable ?? ScriptCompilationResult.Failure(
                    ScriptCompilationStatus.Unavailable,
                    "none",
                    "No script compiler was configured."))
                .WithAttempts(attempts);
        }

        private static string ToWireStatus(ScriptCompilationStatus status)
        {
            switch (status)
            {
                case ScriptCompilationStatus.Success: return "success";
                case ScriptCompilationStatus.CompilationFailed: return "compilationFailed";
                case ScriptCompilationStatus.TimedOut: return "timedOut";
                case ScriptCompilationStatus.OutputTooLarge: return "outputTooLarge";
                default: return "unavailable";
            }
        }
    }

    internal sealed class RoslynCscScriptCompiler : IScriptCompiler
    {
        private const int TimeoutMilliseconds = 15000;
        private const int MaxCompilerOutputChars = 65536;

        public string Name => "Roslyn";

        public ScriptCompilationResult Compile(string code, ScriptCompilerEnvironment environment)
        {
            if (environment == null || string.IsNullOrEmpty(environment.CompilerHostPath) ||
                string.IsNullOrEmpty(environment.CscPath) || string.IsNullOrEmpty(environment.MonoLibRoot))
            {
                return ScriptCompilationResult.Failure(
                    ScriptCompilationStatus.Unavailable,
                    Name,
                    environment?.ToolchainError ?? "Unity Roslyn toolchain is unavailable.");
            }

            var tempRoot = Path.Combine(
                Path.GetTempPath(),
                "AgentBridgeExecuteCSharp",
                Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(tempRoot);
                var sourcePath = Path.Combine(tempRoot, "Snippet.cs");
                var outputPath = Path.Combine(tempRoot, "Snippet.dll");
                var responsePath = Path.Combine(tempRoot, "csc.rsp");
                File.WriteAllText(sourcePath, code ?? string.Empty, new UTF8Encoding(false));
                File.WriteAllText(responsePath,
                    BuildResponseFile(sourcePath, outputPath, environment),
                    new UTF8Encoding(false));

                var stdout = new BoundedText(MaxCompilerOutputChars);
                var stderr = new BoundedText(MaxCompilerOutputChars);
                var startInfo = new ProcessStartInfo
                {
                    FileName = environment.CompilerHostPath,
                    Arguments = BuildCompilerArguments(environment.CscPath, responsePath),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = tempRoot
                };

                using (var process = new Process { StartInfo = startInfo })
                {
                    process.OutputDataReceived += (_, args) => stdout.AppendLine(args.Data);
                    process.ErrorDataReceived += (_, args) => stderr.AppendLine(args.Data);
                    try
                    {
                        process.Start();
                    }
                    catch (Exception ex)
                    {
                        return ScriptCompilationResult.Failure(
                            ScriptCompilationStatus.Unavailable,
                            Name,
                            $"Failed to start Unity Roslyn csc: {ex.Message}");
                    }

                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    if (!process.WaitForExit(TimeoutMilliseconds))
                    {
                        try { process.Kill(); }
                        catch { }
                        try { process.WaitForExit(1000); }
                        catch { }
                        return ScriptCompilationResult.Failure(
                            ScriptCompilationStatus.Unavailable,
                            Name,
                            $"Unity Roslyn csc timed out after {TimeoutMilliseconds} ms; " +
                            "CodeDOM fallback will be attempted.");
                    }
                    process.WaitForExit();

                    var compilerOutput = stdout.Value + stderr.Value;
                    var parsed = ScriptDiagnosticParser.Parse(compilerOutput);
                    if (process.ExitCode != 0)
                    {
                        if (parsed.Diagnostics.Count == 0)
                        {
                            parsed.Diagnostics.Add(new ScriptCompilationDiagnostic
                            {
                                severity = "error",
                                code = "CS0000",
                                line = 0,
                                column = 0,
                                message = string.IsNullOrWhiteSpace(compilerOutput)
                                    ? $"Unity Roslyn csc exited with code {process.ExitCode}."
                                    : Trim(compilerOutput.Trim(), 2048)
                            });
                        }
                        return ScriptCompilationResult.Failure(
                            ScriptCompilationStatus.CompilationFailed,
                            Name,
                            "Unity Roslyn csc reported compilation errors.",
                            parsed.Diagnostics,
                            parsed.Truncated || stdout.Truncated || stderr.Truncated);
                    }

                    if (!File.Exists(outputPath))
                    {
                        return ScriptCompilationResult.Failure(
                            ScriptCompilationStatus.Unavailable,
                            Name,
                            "Unity Roslyn csc completed without producing an assembly.");
                    }
                    var size = new FileInfo(outputPath).Length;
                    if (size > ScriptCompilerPipeline.MaxAssemblyBytes)
                    {
                        return ScriptCompilationResult.Failure(
                            ScriptCompilationStatus.OutputTooLarge,
                            Name,
                            $"Compiled assembly exceeded {ScriptCompilerPipeline.MaxAssemblyBytes} bytes.");
                    }

                    return ScriptCompilationResult.Success(
                        Name,
                        File.ReadAllBytes(outputPath),
                        parsed.Diagnostics,
                        parsed.Truncated || stdout.Truncated || stderr.Truncated);
                }
            }
            finally
            {
                try { Directory.Delete(tempRoot, true); }
                catch { }
            }
        }

        private static string BuildResponseFile(
            string sourcePath,
            string outputPath,
            ScriptCompilerEnvironment environment)
        {
            var references = new HashSet<string>(
                environment.References ?? Array.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
            AddProfileReference(references, environment.MonoLibRoot, "mscorlib.dll");
            AddProfileReference(references, environment.MonoLibRoot, "System.dll");
            AddProfileReference(references, environment.MonoLibRoot, "System.Core.dll");

            var builder = new StringBuilder();
            builder.AppendLine("-nologo");
            builder.AppendLine("-target:library");
            builder.AppendLine("-langversion:preview");
            builder.AppendLine("-nostdlib");
            builder.AppendLine("-optimize-");
            builder.AppendLine("-debug-");
            builder.AppendLine("-unsafe-");
            builder.AppendLine("-out:" + Quote(outputPath));
            foreach (var reference in references
                         .Where(File.Exists)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                builder.AppendLine("-r:" + Quote(reference));
            }
            builder.AppendLine(Quote(sourcePath));
            return builder.ToString();
        }

        private static void AddProfileReference(
            ISet<string> references,
            string monoLibRoot,
            string name)
        {
            if (references.Any(path =>
                    string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }
            var path = Path.Combine(monoLibRoot, name);
            if (File.Exists(path))
            {
                references.Add(path);
            }
        }

        private static string BuildCompilerArguments(string cscPath, string responsePath)
        {
            var shared = string.Equals(Path.GetFileName(cscPath), "csc.dll",
                StringComparison.OrdinalIgnoreCase)
                ? " /shared:false"
                : string.Empty;
            return $"{Quote(cscPath)} -noconfig{shared} @{Quote(responsePath)}";
        }

        private static string Quote(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }

        private static string Trim(string value, int max)
        {
            return value.Length <= max ? value : value.Substring(0, max - 1) + "…";
        }
    }

    internal sealed class CodeDomScriptCompiler : IScriptCompiler
    {
        public string Name => "CodeDom";

        public ScriptCompilationResult Compile(string code, ScriptCompilerEnvironment environment)
        {
            if (!TryResolveTypes(out var providerType, out var parametersType, out var error))
            {
                return ScriptCompilationResult.Failure(
                    ScriptCompilationStatus.Unavailable,
                    Name,
                    error);
            }

            var tempRoot = Path.Combine(
                Path.GetTempPath(),
                "AgentBridgeExecuteCSharp",
                Guid.NewGuid().ToString("N"));
            var outputPath = Path.Combine(tempRoot, "Snippet.dll");
            object provider = null;
            try
            {
                Directory.CreateDirectory(tempRoot);
                provider = Activator.CreateInstance(providerType);
                var parameters = Activator.CreateInstance(parametersType);
                parametersType.GetProperty("GenerateInMemory")?.SetValue(parameters, false, null);
                parametersType.GetProperty("GenerateExecutable")?.SetValue(parameters, false, null);
                parametersType.GetProperty("TreatWarningsAsErrors")?.SetValue(parameters, false, null);
                parametersType.GetProperty("OutputAssembly")?.SetValue(parameters, outputPath, null);

                var references = parametersType.GetProperty("ReferencedAssemblies")
                    ?.GetValue(parameters, null);
                var add = references?.GetType().GetMethod("Add", new[] { typeof(string) });
                foreach (var reference in environment?.References ?? Array.Empty<string>())
                {
                    if (File.Exists(reference))
                    {
                        add?.Invoke(references, new object[] { reference });
                    }
                }

                var compile = providerType.GetMethod(
                    "CompileAssemblyFromSource",
                    new[] { parametersType, typeof(string[]) });
                var results = compile?.Invoke(provider, new object[] { parameters, new[] { code } });
                if (results == null)
                {
                    return ScriptCompilationResult.Failure(
                        ScriptCompilationStatus.Unavailable,
                        Name,
                        "CodeDOM returned no compilation result.");
                }

                var errors = results.GetType().GetProperty("Errors")?.GetValue(results, null);
                var diagnostics = ParseCodeDomErrors(errors, out var truncated);
                var hasErrors = diagnostics.Any(item => item.severity == "error");
                if (hasErrors)
                {
                    return ScriptCompilationResult.Failure(
                        ScriptCompilationStatus.CompilationFailed,
                        Name,
                        "CodeDOM reported compilation errors.",
                        diagnostics,
                        truncated);
                }
                if (!File.Exists(outputPath))
                {
                    return ScriptCompilationResult.Failure(
                        ScriptCompilationStatus.Unavailable,
                        Name,
                        "CodeDOM completed without producing an assembly.");
                }
                if (new FileInfo(outputPath).Length > ScriptCompilerPipeline.MaxAssemblyBytes)
                {
                    return ScriptCompilationResult.Failure(
                        ScriptCompilationStatus.OutputTooLarge,
                        Name,
                        $"Compiled assembly exceeded {ScriptCompilerPipeline.MaxAssemblyBytes} bytes.");
                }

                return ScriptCompilationResult.Success(
                    Name,
                    File.ReadAllBytes(outputPath),
                    diagnostics,
                    truncated);
            }
            finally
            {
                if (provider is IDisposable disposable)
                {
                    disposable.Dispose();
                }
                try { Directory.Delete(tempRoot, true); }
                catch { }
            }
        }

        private static bool TryResolveTypes(
            out Type providerType,
            out Type parametersType,
            out string error)
        {
            providerType = Type.GetType("Microsoft.CSharp.CSharpCodeProvider, System");
            parametersType = Type.GetType("System.CodeDom.Compiler.CompilerParameters, System");
            if (providerType == null || parametersType == null)
            {
                try
                {
                    var assembly = Assembly.Load("System.CodeDom");
                    providerType = providerType ?? assembly.GetType("Microsoft.CSharp.CSharpCodeProvider");
                    parametersType = parametersType ?? assembly.GetType("System.CodeDom.Compiler.CompilerParameters");
                }
                catch
                {
                }
            }
            if (providerType == null || parametersType == null)
            {
                error = "CSharpCodeProvider or CompilerParameters is unavailable.";
                return false;
            }
            error = null;
            return true;
        }

        private static List<ScriptCompilationDiagnostic> ParseCodeDomErrors(
            object errors,
            out bool truncated)
        {
            var result = new List<ScriptCompilationDiagnostic>();
            truncated = false;
            if (!(errors is IEnumerable enumerable))
            {
                return result;
            }
            foreach (var error in enumerable)
            {
                if (result.Count >= ScriptDiagnosticParser.MaxDiagnostics)
                {
                    truncated = true;
                    break;
                }
                var type = error.GetType();
                var warning = (bool)(type.GetProperty("IsWarning")?.GetValue(error, null) ?? false);
                result.Add(new ScriptCompilationDiagnostic
                {
                    line = (int)(type.GetProperty("Line")?.GetValue(error, null) ?? 0),
                    column = (int)(type.GetProperty("Column")?.GetValue(error, null) ?? 0),
                    severity = warning ? "warning" : "error",
                    code = type.GetProperty("ErrorNumber")?.GetValue(error, null)?.ToString(),
                    message = type.GetProperty("ErrorText")?.GetValue(error, null)?.ToString()
                              ?? "Unknown compiler diagnostic"
                });
            }
            return result;
        }
    }

    internal static class ScriptDiagnosticParser
    {
        internal const int MaxDiagnostics = 20;
        private const int MaxDiagnosticChars = 2048;
        private const int MaxTotalDiagnosticChars = 32768;

        private static readonly Regex WithLocation = new Regex(
            @"^(?<file>.+?)\((?<line>\d+),(?<column>\d+)\):\s*(?<severity>error|warning)\s*(?<code>CS\d+):\s*(?<message>.*)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex WithoutLocation = new Regex(
            @"^(?<severity>error|warning)\s*(?<code>CS\d+):\s*(?<message>.*)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        internal static ParsedDiagnostics Parse(string output)
        {
            var result = new ParsedDiagnostics();
            var totalChars = 0;
            foreach (var raw in (output ?? string.Empty).Split(
                         new[] { '\r', '\n' },
                         StringSplitOptions.RemoveEmptyEntries))
            {
                var match = WithLocation.Match(raw.Trim());
                if (!match.Success)
                {
                    match = WithoutLocation.Match(raw.Trim());
                }
                if (!match.Success)
                {
                    continue;
                }
                if (result.Diagnostics.Count >= MaxDiagnostics || totalChars >= MaxTotalDiagnosticChars)
                {
                    result.Truncated = true;
                    break;
                }

                var message = match.Groups["message"].Value;
                var remaining = Math.Min(MaxDiagnosticChars, MaxTotalDiagnosticChars - totalChars);
                if (message.Length > remaining)
                {
                    message = message.Substring(0, Math.Max(0, remaining - 1)) + "…";
                    result.Truncated = true;
                }
                int.TryParse(match.Groups["line"].Value, out var line);
                int.TryParse(match.Groups["column"].Value, out var column);
                result.Diagnostics.Add(new ScriptCompilationDiagnostic
                {
                    line = line,
                    column = column,
                    severity = match.Groups["severity"].Value.ToLowerInvariant(),
                    code = match.Groups["code"].Value,
                    message = message
                });
                totalChars += message.Length;
            }
            return result;
        }

        internal sealed class ParsedDiagnostics
        {
            internal List<ScriptCompilationDiagnostic> Diagnostics { get; } =
                new List<ScriptCompilationDiagnostic>();
            internal bool Truncated { get; set; }
        }
    }

    internal sealed class BoundedText
    {
        private readonly object m_Gate = new object();
        private readonly StringBuilder m_Value = new StringBuilder();
        private readonly int m_MaxChars;

        internal BoundedText(int maxChars)
        {
            m_MaxChars = maxChars;
        }

        internal bool Truncated { get; private set; }

        internal string Value
        {
            get
            {
                lock (m_Gate)
                {
                    return m_Value.ToString();
                }
            }
        }

        internal void AppendLine(string value)
        {
            if (value == null)
            {
                return;
            }
            lock (m_Gate)
            {
                var remaining = m_MaxChars - m_Value.Length;
                if (remaining <= 0)
                {
                    Truncated = true;
                    return;
                }
                var line = value + Environment.NewLine;
                if (line.Length > remaining)
                {
                    m_Value.Append(line, 0, remaining);
                    Truncated = true;
                    return;
                }
                m_Value.Append(line);
            }
        }
    }
}
