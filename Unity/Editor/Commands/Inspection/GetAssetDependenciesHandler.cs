using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace AgentBridge
{
    public sealed class GetAssetDependenciesHandler : ICommandHandler
    {
        public string Command => "get_asset_dependencies";
        public string Description => "查询资产直接或递归依赖;支持 Assets/Packages path 或 guid,稳定排序并限量";
        public string Group => "Inspection";
        public bool CanDisable => true;
        public CommandBatchMode BatchMode => CommandBatchMode.Allowed;

        public async CommandTask<object> ExecuteAsync(JObject @params)
        {
            var path = AssetReadSupport.Resolve(@params, true);
            var recursive = SceneCommandSupport.ReadBool(@params, "recursive", true);
            var limit = @params?["limit"]?.Value<int>() ?? 1000;
            var comparison = Application.platform == RuntimePlatform.WindowsEditor ||
                             Application.platform == RuntimePlatform.OSXEditor
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            var dependencies = AssetDatabase.GetDependencies(path, recursive)
                .Where(item => !string.Equals(item.Replace('\\', '/'), path, comparison))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToArray();
            return new
            {
                source = AssetReadSupport.Describe(path),
                recursive,
                total = dependencies.Length,
                truncated = dependencies.Length > limit,
                dependencies = dependencies.Take(limit).Select(AssetReadSupport.Describe).ToArray()
            };
        }

        public JObject ParamsSchema { get; } = JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""path"": { ""type"": ""string"" },
    ""guid"": { ""type"": ""string"", ""minLength"": 32, ""maxLength"": 32 },
    ""recursive"": { ""type"": ""boolean"", ""default"": true },
    ""limit"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 1000, ""default"": 1000 }
  },
  ""anyOf"": [{ ""required"": [""path""] }, { ""required"": [""guid""] }]
}");
    }
}
