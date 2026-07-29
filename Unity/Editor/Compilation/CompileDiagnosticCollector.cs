using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using UnityEditor.Compilation;

namespace AgentBridge
{
    /// <summary>在编译消息进入时执行错误优先的数量、文本与序列化字节预算。</summary>
    internal static class CompileDiagnosticCollector
    {
        internal const int MaxStoredErrors = 200;
        internal const int MaxStoredWarnings = 100;
        internal const int MaxFileCharacters = 1024;
        internal const int MaxMessageCharacters = 4096;
        internal const int MaxStoredDiagnosticBytes = 768 * 1024;

        private const string TruncatedSuffix = "\n...[truncated]";
        private const string TruncatedPathPrefix = "...[truncated]...";
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        internal static void Reset(CompileResult result)
        {
            result.Messages = result.Messages ?? new List<CompileMessage>();
            result.Messages.Clear();
            result.ErrorCount = 0;
            result.WarningCount = 0;
            result.StoredErrorCount = 0;
            result.StoredWarningCount = 0;
            result.OmittedErrorCount = 0;
            result.OmittedWarningCount = 0;
            result.StoredDiagnosticBytes = 0;
            result.DiagnosticsTruncated = false;
        }

        internal static void Append(
            CompileResult result,
            CompilerMessage[] compilerMessages)
        {
            if (compilerMessages == null)
            {
                return;
            }

            foreach (var compilerMessage in compilerMessages)
            {
                var isError = compilerMessage.type == CompilerMessageType.Error;
                if (!isError && compilerMessage.type != CompilerMessageType.Warning)
                {
                    continue;
                }

                if (isError)
                {
                    result.ErrorCount++;
                }
                else
                {
                    result.WarningCount++;
                }

                if ((isError && result.StoredErrorCount >= MaxStoredErrors) ||
                    (!isError && result.StoredWarningCount >= MaxStoredWarnings))
                {
                    continue;
                }

                var message = CompileMonitor.Map(compilerMessage);
                var textTruncated = Normalize(message);
                var messageBytes = SerializedBytes(message);
                if (isError)
                {
                    StoreError(result, message, messageBytes);
                }
                else
                {
                    StoreWarning(result, message, messageBytes);
                }

                if (textTruncated)
                {
                    result.DiagnosticsTruncated = true;
                }
            }

            UpdateOmittedCounts(result);
        }

        internal static void NormalizeLoaded(CompileResult result)
        {
            var original = (result.Messages ?? new List<CompileMessage>())
                .Where(item => item != null &&
                               (item.Type == "error" || item.Type == "warning"))
                .ToArray();
            var errorCount = Math.Max(
                result.ErrorCount,
                original.Count(item => item.Type == "error"));
            var warningCount = Math.Max(
                result.WarningCount,
                original.Count(item => item.Type == "warning"));
            var diagnosticsTruncated = result.DiagnosticsTruncated;

            Reset(result);
            foreach (var message in original)
            {
                var textTruncated = Normalize(message);
                var messageBytes = SerializedBytes(message);
                if (message.Type == "error")
                {
                    StoreError(result, message, messageBytes);
                }
                else
                {
                    StoreWarning(result, message, messageBytes);
                }
                diagnosticsTruncated |= textTruncated;
            }

            result.ErrorCount = errorCount;
            result.WarningCount = warningCount;
            result.DiagnosticsTruncated = diagnosticsTruncated;
            UpdateOmittedCounts(result);
        }

        internal static string TruncateMessage(string value)
        {
            return Truncate(value, MaxMessageCharacters, false);
        }

        private static void StoreError(
            CompileResult result,
            CompileMessage message,
            int messageBytes)
        {
            if (result.StoredErrorCount >= MaxStoredErrors)
            {
                return;
            }

            while (result.StoredDiagnosticBytes + messageBytes >
                   MaxStoredDiagnosticBytes &&
                   result.StoredWarningCount > 0)
            {
                EvictLastWarning(result);
            }

            if (result.StoredDiagnosticBytes + messageBytes >
                MaxStoredDiagnosticBytes)
            {
                return;
            }

            result.Messages.Add(message);
            result.StoredErrorCount++;
            result.StoredDiagnosticBytes += messageBytes;
        }

        private static void StoreWarning(
            CompileResult result,
            CompileMessage message,
            int messageBytes)
        {
            if (result.StoredWarningCount >= MaxStoredWarnings ||
                result.StoredDiagnosticBytes + messageBytes >
                MaxStoredDiagnosticBytes)
            {
                return;
            }

            result.Messages.Add(message);
            result.StoredWarningCount++;
            result.StoredDiagnosticBytes += messageBytes;
        }

        private static void EvictLastWarning(CompileResult result)
        {
            for (var index = result.Messages.Count - 1; index >= 0; index--)
            {
                var message = result.Messages[index];
                if (message.Type != "warning")
                {
                    continue;
                }

                result.Messages.RemoveAt(index);
                result.StoredWarningCount--;
                result.StoredDiagnosticBytes -= SerializedBytes(message);
                return;
            }
        }

        private static bool Normalize(CompileMessage message)
        {
            var file = message.File ?? "";
            var text = message.Message ?? "";
            message.File = Truncate(file, MaxFileCharacters, true);
            message.Message = Truncate(text, MaxMessageCharacters, false);
            return message.File.Length != file.Length ||
                   message.Message.Length != text.Length;
        }

        private static string Truncate(string value, int maxLength, bool keepEnd)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            {
                return value ?? "";
            }

            var marker = keepEnd ? TruncatedPathPrefix : TruncatedSuffix;
            var remainingLength = Math.Max(0, maxLength - marker.Length);
            return keepEnd
                ? $"{marker}{value.Substring(value.Length - remainingLength)}"
                : $"{value.Substring(0, remainingLength)}{marker}";
        }

        private static int SerializedBytes(CompileMessage message)
        {
            return Utf8NoBom.GetByteCount(
                JsonConvert.SerializeObject(message, Formatting.None)) + 1;
        }

        private static void UpdateOmittedCounts(CompileResult result)
        {
            result.OmittedErrorCount = Math.Max(
                0,
                result.ErrorCount - result.StoredErrorCount);
            result.OmittedWarningCount = Math.Max(
                0,
                result.WarningCount - result.StoredWarningCount);
            if (result.OmittedErrorCount > 0 || result.OmittedWarningCount > 0)
            {
                result.DiagnosticsTruncated = true;
            }
        }
    }
}
