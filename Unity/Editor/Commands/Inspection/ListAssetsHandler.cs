using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace AgentBridge
{
    /// <summary>
    /// list_assets(只读):按条件查工程资产。params 可带 type / folder / query(底层 AssetDatabase.FindAssets)。
    /// 无任何 filter 时限数(NoFilterCap)并标 truncated,避免大工程一次倒出上万条。
    /// </summary>
    public sealed class ListAssetsHandler : ICommandHandler
    {
        private const int NoFilterCap = 1000;

        public string Command => "list_assets";
        public string Description => "按条件查工程资产(type/folder/query),返回 path+guid+type;无 filter 时限数";

        public object Execute(JObject @params)
        {
            var type = @params?["type"]?.Value<string>();
            var folder = @params?["folder"]?.Value<string>();
            var query = @params?["query"]?.Value<string>();

            var parts = new List<string>();
            if (!string.IsNullOrEmpty(query))
            {
                parts.Add(query);
            }
            if (!string.IsNullOrEmpty(type))
            {
                parts.Add("t:" + type);
            }
            var filter = string.Join(" ", parts);

            var guids = !string.IsNullOrEmpty(folder)
                ? AssetDatabase.FindAssets(filter, new[] { folder })
                : AssetDatabase.FindAssets(filter);

            var noFilter = string.IsNullOrEmpty(filter) && string.IsNullOrEmpty(folder);
            var truncated = false;
            IEnumerable<string> use = guids;
            if (noFilter && guids.Length > NoFilterCap)
            {
                use = guids.Take(NoFilterCap);
                truncated = true;
            }

            var items = use.Select(g =>
            {
                var path = AssetDatabase.GUIDToAssetPath(g);
                return new { guid = g, path, t = AssetDatabase.GetMainAssetTypeAtPath(path) };
            });

            // Unity 的 t: 过滤不精确(会带进 shadergraph 等);type 指定时按实际主资产类型精确过滤。
            if (!string.IsNullOrEmpty(type))
            {
                items = items.Where(x => x.t != null && (x.t.Name == type || x.t.FullName == type));
            }

            var assets = items
                .Select(x => new { path = x.path, guid = x.guid, type = x.t != null ? x.t.FullName : null })
                .ToArray();

            return new { assets, count = assets.Length, truncated };
        }

        public JObject GetParamsSchema()
        {
            return JObject.Parse(
                @"{""type"":""object"",""properties"":{""type"":{""type"":""string""},""folder"":{""type"":""string""},""query"":{""type"":""string""}}}");
        }
    }
}
