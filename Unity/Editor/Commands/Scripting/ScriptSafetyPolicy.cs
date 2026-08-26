using System;
using System.Text.RegularExpressions;

namespace AgentBridge
{
    internal static class ScriptSafetyPolicy
    {
        private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

        private static readonly SafetyRule[] BaseRules =
        {
            Rule(@"\bFile\.Delete\b", "File.Delete is blocked by safety checks"),
            Rule(@"\bDirectory\.Delete\b", "Directory.Delete is blocked by safety checks"),
            Rule(@"\bSystem\.IO\.File\.Delete\b", "System.IO.File.Delete is blocked by safety checks"),
            Rule(@"\bProcess\.Start\b", "Process.Start is blocked by safety checks"),
            Rule(@"\bSystem\.Diagnostics\.Process\b", "System.Diagnostics.Process is blocked by safety checks"),
            Rule(@"\bEnvironment\.Exit\b", "Environment.Exit is blocked by safety checks"),
            Rule(@"\bApplication\.Quit\b", "Application.Quit is blocked by safety checks"),
            Rule(@"\bAssetDatabase\.DeleteAsset\b", "AssetDatabase.DeleteAsset is blocked by safety checks"),
            Rule(@"\bwhile\s*\(\s*true\s*\)", "literal while(true) loop is blocked by safety checks"),
            Rule(@"\bfor\s*\(\s*;\s*;\s*\)", "literal for(;;) loop is blocked by safety checks")
        };

        private static readonly SafetyRule[] StrictRules =
        {
            Rule(@"(?<![\w.])(?:System\.IO\.)?File\.(?:WriteAllText|WriteAllBytes|WriteAllLines|AppendAllText|AppendAllLines|Copy|Create|CreateText|OpenWrite|Move|Replace|SetAttributes|SetCreationTime|SetLastAccessTime|SetLastWriteTime)\b", "file write or move operation is blocked by strict filesystem checks"),
            Rule(@"(?<![\w.])(?:System\.IO\.)?Directory\.(?:CreateDirectory|Delete|Move)\b", "directory write operation is blocked by strict filesystem checks"),
            Rule(@"(?<![\w.])(?:System\.IO\.)?FileInfo\.(?:CopyTo|Create|CreateText|Delete|MoveTo|Replace)\b", "FileInfo write operation is blocked by strict filesystem checks"),
            Rule(@"(?<![\w.])(?:System\.IO\.)?DirectoryInfo\.(?:Create|CreateSubdirectory|Delete|MoveTo)\b", "DirectoryInfo write operation is blocked by strict filesystem checks"),
            Rule(@"(?<![\w.])(?:System\.IO\.)?FileStream\s*\(", "FileStream construction is blocked by strict filesystem checks"),
            Rule(@"(?<![\w.])(?:System\.IO\.)?StreamWriter\s*\(", "StreamWriter construction is blocked by strict filesystem checks"),
            Rule(@"(?<![\w.])(?:System\.IO\.)?StreamReader\s*\(", "StreamReader construction is blocked by strict filesystem checks"),
            Rule("\"(?:~|%USERPROFILE%|%APPDATA%|%LOCALAPPDATA%|\\$HOME)(?:/|\\\\|\\\\\\\\|\"|$)", "user home or configuration path is blocked by strict filesystem checks"),
            Rule("\"(?:[A-Za-z]:\\\\|\\\\\\\\|/Users/|/home/|/root/|/System/|/Library/|/Applications/|/bin/|/sbin/|/usr/|/etc/|/var/|/private/|/tmp/)", "absolute or system path is blocked by strict filesystem checks"),
            Rule("\"[^\"]*(?:\\.\\./|\\.\\.\\\\)[^\"]*\"", "path traversal is blocked by strict filesystem checks")
        };

        internal static bool TryFindViolation(
            string code,
            bool strictFilesystemChecks,
            out string reason)
        {
            code = code ?? string.Empty;
            if (TryFind(code, BaseRules, out reason))
            {
                return true;
            }
            if (strictFilesystemChecks && TryFind(code, StrictRules, out reason))
            {
                return true;
            }
            reason = null;
            return false;
        }

        private static bool TryFind(string code, SafetyRule[] rules, out string reason)
        {
            foreach (var rule in rules)
            {
                try
                {
                    if (!rule.Regex.IsMatch(code))
                    {
                        continue;
                    }
                }
                catch (RegexMatchTimeoutException)
                {
                    reason = "safety inspection timed out";
                    return true;
                }

                reason = rule.Reason;
                return true;
            }
            reason = null;
            return false;
        }

        private static SafetyRule Rule(string pattern, string reason)
        {
            return new SafetyRule(
                new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.Compiled, MatchTimeout),
                reason);
        }

        private sealed class SafetyRule
        {
            internal SafetyRule(Regex regex, string reason)
            {
                Regex = regex;
                Reason = reason;
            }

            internal Regex Regex { get; }
            internal string Reason { get; }
        }
    }
}