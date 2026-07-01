using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;

namespace AgentBridge
{
    /// <summary>
    /// 只读读取编辑器 Console 面板当前的日志条目。Unity 未公开 Console API,故反射内部
    /// <c>UnityEditor.LogEntries</c> / <c>UnityEditor.LogEntry</c>(见 UnityCsReference
    /// Editor/Mono/LogEntries.bindings.cs)。字段名/存在性按版本容错——反射失败即抛
    /// CommandException(CONSOLE_UNAVAILABLE),不让整个命令崩掉。
    /// </summary>
    internal static class ConsoleLogReader
    {
        // LogEntry.mode 位标志(UnityCsReference 的 LogMessageFlags,internal)。只取判定 error/warning 所需的位。
        private const int ModeError = 1 << 0;            // kError
        private const int ModeFatal = 1 << 4;            // kFatal
        private const int ModeAssetImportError = 1 << 6; // kAssetImportError
        private const int ModeAssetImportWarning = 1 << 7; // kAssetImportWarning
        private const int ModeScriptingError = 1 << 8;   // kScriptingError
        private const int ModeScriptingWarning = 1 << 9; // kScriptingWarning
        private const int ModeScriptCompileError = 1 << 11; // kScriptCompileError
        private const int ModeScriptCompileWarning = 1 << 12; // kScriptCompileWarning
        private const int ModeScriptingException = 1 << 17; // kScriptingException

        private const int ErrorModes = ModeError | ModeFatal | ModeAssetImportError
            | ModeScriptingError | ModeScriptCompileError | ModeScriptingException;
        private const int WarningModes = ModeAssetImportWarning | ModeScriptingWarning | ModeScriptCompileWarning;

        public struct Entry
        {
            public string Message;
            public string Type;   // "error" / "warning" / "log"
            public string File;
            public int Line;
        }

        private static bool s_Init;
        private static Type s_LogEntriesType;
        private static Type s_LogEntryType;
        private static MethodInfo s_StartGettingEntries;
        private static MethodInfo s_EndGettingEntries;
        private static MethodInfo s_GetEntryInternal;
        private static FieldInfo s_MessageField; // message(旧版可能是 condition)
        private static FieldInfo s_ModeField;
        private static FieldInfo s_FileField;
        private static FieldInfo s_LineField;

        // 反射一次并缓存;失败返回 false(此时应答 CONSOLE_UNAVAILABLE)。
        private static bool EnsureReflection()
        {
            if (s_Init)
            {
                return s_LogEntriesType != null;
            }
            s_Init = true;

            var editorAssembly = typeof(Editor).Assembly;
            s_LogEntriesType = editorAssembly.GetType("UnityEditor.LogEntries")
                ?? editorAssembly.GetType("UnityEditorInternal.LogEntries");
            s_LogEntryType = editorAssembly.GetType("UnityEditor.LogEntry")
                ?? editorAssembly.GetType("UnityEditorInternal.LogEntry");
            if (s_LogEntriesType == null || s_LogEntryType == null)
            {
                s_LogEntriesType = null;
                return false;
            }

            const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            s_StartGettingEntries = s_LogEntriesType.GetMethod("StartGettingEntries", Flags);
            s_EndGettingEntries = s_LogEntriesType.GetMethod("EndGettingEntries", Flags);
            s_GetEntryInternal = s_LogEntriesType.GetMethod("GetEntryInternal", Flags);

            const BindingFlags FieldFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            s_MessageField = s_LogEntryType.GetField("message", FieldFlags)
                ?? s_LogEntryType.GetField("condition", FieldFlags);
            s_ModeField = s_LogEntryType.GetField("mode", FieldFlags);
            s_FileField = s_LogEntryType.GetField("file", FieldFlags);
            s_LineField = s_LogEntryType.GetField("line", FieldFlags);

            if (s_StartGettingEntries == null || s_EndGettingEntries == null
                || s_GetEntryInternal == null || s_MessageField == null || s_ModeField == null)
            {
                s_LogEntriesType = null; // 关键成员缺失 → 视为不可用
                return false;
            }
            return true;
        }

        /// <summary>抓取 Console 当前全部条目的快照。必须在主线程调用(handler 已保证)。</summary>
        public static List<Entry> ReadAll()
        {
            if (!EnsureReflection())
            {
                throw new CommandException("CONSOLE_UNAVAILABLE",
                    "无法访问编辑器 Console(内部 LogEntries 反射失败,可能是 Unity 版本不兼容)。");
            }

            var result = new List<Entry>();
            var entry = Activator.CreateInstance(s_LogEntryType, nonPublic: true);
            var args = new object[2];
            // StartGettingEntries / EndGettingEntries 必须严格配对,否则触发原生断言,故用 try/finally。
            int count = (int)s_StartGettingEntries.Invoke(null, null);
            try
            {
                for (int i = 0; i < count; i++)
                {
                    args[0] = i;
                    args[1] = entry;
                    var ok = (bool)s_GetEntryInternal.Invoke(null, args);
                    if (!ok)
                    {
                        continue;
                    }

                    var mode = (int)s_ModeField.GetValue(entry);
                    result.Add(new Entry
                    {
                        Message = s_MessageField.GetValue(entry) as string ?? "",
                        Type = ClassifyMode(mode),
                        File = s_FileField != null ? s_FileField.GetValue(entry) as string ?? "" : "",
                        Line = s_LineField != null ? (int)s_LineField.GetValue(entry) : 0
                    });
                }
            }
            finally
            {
                s_EndGettingEntries.Invoke(null, null);
            }
            return result;
        }

        private static string ClassifyMode(int mode)
        {
            if ((mode & ErrorModes) != 0)
            {
                return "error";
            }
            if ((mode & WarningModes) != 0)
            {
                return "warning";
            }
            return "log";
        }
    }
}
