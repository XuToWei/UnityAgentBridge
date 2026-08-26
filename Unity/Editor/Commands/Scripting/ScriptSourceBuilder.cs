using System;

namespace AgentBridge
{
    internal static class ScriptSourceBuilder
    {
        internal const string BodyTypeName = "AgentBridgeGeneratedScript";
        internal const string VirtualFileName = "AgentBridgeSnippet.cs";

        private const string Imports = @"using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;
using AgentBridge;
using Newtonsoft.Json.Linq;
";

        internal static string Build(string code, string mode)
        {
            if (string.Equals(mode, "body", StringComparison.Ordinal))
            {
                return Imports + @"
public sealed class AgentBridgeGeneratedScript : IAgentScript
{
    public void Execute(AgentBridgeScriptContext context)
    {
#line 1 ""AgentBridgeSnippet.cs""
" + (code ?? string.Empty) + @"
#line default
#line hidden
    }
}
";
            }

            if (string.Equals(mode, "class", StringComparison.Ordinal))
            {
                return Imports + "\n#line 1 \"" + VirtualFileName + "\"\n" +
                       (code ?? string.Empty) + "\n#line default\n#line hidden\n";
            }

            throw new ArgumentException($"unknown script mode '{mode}'", nameof(mode));
        }
    }
}