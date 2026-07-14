using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace AgentBridge
{
    public sealed class GetAssetHandler : ICommandHandler
    {
        public string Command => "get_asset";
        public string Description => "按 Assets/Packages path 或 guid 查询资产、子资产、labels、dependencyHash 与可选序列化属性";
        public string Group => "Inspection";
        public bool CanDisable => true;
        public CommandBatchMode BatchMode => CommandBatchMode.Allowed;

        public object Execute(JObject @params)
        {
            var path = AssetReadSupport.Resolve(@params);
            var main = AssetDatabase.LoadMainAssetAtPath(path);
            var includeProperties = SceneCommandSupport.ReadBool(@params, "includeProperties", false);
            var limit = @params?["subAssetLimit"]?.Value<int>() ?? 100;
            var all = AssetDatabase.IsValidFolder(path)
                ? new UnityEngine.Object[0]
                : AssetDatabase.LoadAllAssetsAtPath(path).Where(asset => asset != null).ToArray();
            var ordered = all.Select(asset => new
                {
                    asset,
                    key = GetLocalId(asset)
                })
                .OrderBy(entry => entry.key)
                .ThenBy(entry => entry.asset.name, StringComparer.Ordinal)
                .ToArray();
            var subAssetEntries = ordered.Where(entry => entry.asset != main).ToArray();
            var subAssets = subAssetEntries.Take(limit)
                .Select(entry => AssetReadSupport.DescribeObject(entry.asset)).ToArray();
            var importer = AssetImporter.GetAtPath(path);

            return new
            {
                asset = AssetReadSupport.Describe(path),
                main = AssetReadSupport.DescribeObject(main),
                labels = main == null ? new string[0] : AssetDatabase.GetLabels(main),
                dependencyHash = AssetDatabase.GetAssetDependencyHash(path).ToString(),
                subAssets,
                subAssetCount = subAssetEntries.Length,
                subAssetsTruncated = subAssetEntries.Length > limit,
                properties = includeProperties && main != null ? PropertySerializer.SerializeTopLevel(main) : null,
                importer = importer == null ? null : new
                {
                    type = importer.GetType().FullName,
                    assetPath = importer.assetPath,
                    properties = includeProperties ? PropertySerializer.SerializeTopLevel(importer) : null
                }
            };
        }

        private static long GetLocalId(UnityEngine.Object asset)
        {
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out _, out long localId);
            return localId;
        }

        public JObject ParamsSchema { get; } = JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""path"": { ""type"": ""string"" },
    ""guid"": { ""type"": ""string"", ""minLength"": 32, ""maxLength"": 32 },
    ""includeProperties"": { ""type"": ""boolean"", ""default"": false },
    ""subAssetLimit"": { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 1000, ""default"": 100 }
  },
  ""anyOf"": [{ ""required"": [""path""] }, { ""required"": [""guid""] }]
}");
    }
}
