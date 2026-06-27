using Newtonsoft.Json.Linq;
using UnityEditor;

namespace AgentBridge
{
    /// <summary>
    /// move_asset(资源写):工程内移动 / 重命名资产(同一 API 兼顾改名)。
    /// params.from / params.to 均限 Assets/ 下。AssetDatabase.MoveAsset 返回非空错误串 → ASSET_MOVE_FAILED。
    /// </summary>
    public sealed class MoveAssetHandler : ICommandHandler
    {
        public string Command => "move_asset";
        public string Description => "工程内移动/重命名资产(from→to,均限 Assets/ 下);失败 → ASSET_MOVE_FAILED";
        public string Group => "Assets";
        public bool CanDisable => true;

        public object Execute(JObject @params)
        {
            var from = AssetSupport.RequireProjectPath(@params?["from"]?.Value<string>(), "from");
            var to = AssetSupport.RequireProjectPath(@params?["to"]?.Value<string>(), "to");

            var err = AssetDatabase.MoveAsset(from, to); // "" 表示成功
            if (!string.IsNullOrEmpty(err))
            {
                throw new CommandException(AssetErrorCodes.AssetMoveFailed, err);
            }

            return new { from, to };
        }

        public JObject GetParamsSchema()
        {
            return JObject.Parse(
                @"{""type"":""object"",""properties"":{""from"":{""type"":""string""},""to"":{""type"":""string""}},""required"":[""from"",""to""]}");
        }
    }
}
