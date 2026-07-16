using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace AgentBridge
{
    /// <summary>
    /// list_assets(只读):按条件查工程资产。params 可带 type / folder / query(底层 AssetDatabase.FindAssets)。
    /// 默认最多返回 DefaultLimit 条，可用 limit 调整，避免大工程一次倒出过多 JSON。
    /// </summary>
    public sealed class ListAssetsHandler : ICommandHandler
    {
        private const int DefaultLimit = 200;
        private const int MaxLimit = 1000;

        public string Command => "list_assets";
        public string Description => "按条件查工程资产(type/folder/query/limit),默认最多返回 200 条";
        public string Group => "Inspection";
        public bool CanDisable => true;
        public CommandBatchMode BatchMode => CommandBatchMode.Allowed;

        public async CommandTask<object> ExecuteAsync(JObject @params)
        {
            var type = @params?["type"]?.Value<string>();
            var folder = @params?["folder"]?.Value<string>();
            var query = @params?["query"]?.Value<string>();
            var limit = @params?["limit"]?.ToObject<int?>() ?? DefaultLimit;
            if (limit <= 0)
            {
                throw new CommandException(ErrorCodes.InvalidParams, "limit 必须是正整数");
            }
            limit = Math.Min(limit, MaxLimit);

            var parts = new List<string>();
            if (!string.IsNullOrEmpty(query))
            {
                parts.Add(query);
            }
            if (!string.IsNullOrEmpty(type))
            {
                parts.Add($"t:{type}");
            }
            var filter = string.Join(" ", parts);

            var guids = !string.IsNullOrEmpty(folder)
                ? AssetDatabase.FindAssets(filter, new[] { folder })
                : AssetDatabase.FindAssets(filter);

            var items = guids.Select(g =>
            {
                var path = AssetDatabase.GUIDToAssetPath(g);
                return new { guid = g, path, t = AssetDatabase.GetMainAssetTypeAtPath(path) };
            });

            // Unity 的 t: 过滤不精确(会带进 shadergraph 等);type 指定时按实际主资产类型精确过滤。
            if (!string.IsNullOrEmpty(type))
            {
                items = items.Where(x => x.t != null && (x.t.Name == type || x.t.FullName == type));
            }

            var selected = items
                .Select(x => new { path = x.path, guid = x.guid, type = x.t != null ? x.t.FullName : null })
                .Take(limit + 1)
                .ToArray();
            var truncated = selected.Length > limit;
            var assets = truncated ? selected.Take(limit).ToArray() : selected;

            return new { assets, count = assets.Length, truncated };
        }

        public JObject ParamsSchema { get; } = JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""type"": { ""type"": ""string"" },
    ""folder"": { ""type"": ""string"" },
    ""query"": { ""type"": ""string"" },
    ""limit"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 1000, ""default"": 200 }
  }
}");
    }
}
