using Newtonsoft.Json.Linq;
using UnityEditor;

namespace AgentBridge
{
    /// <summary>
    /// delete_asset(资源写):把资产移入系统回收站(可恢复)。params.path 必填(限 Assets/ 下)。
    /// 资产操作不进 Ctrl-Z 撤销栈,回收站是恢复手段。不存在 → ASSET_NOT_FOUND。
    /// </summary>
    public sealed class DeleteAssetHandler : ICommandHandler
    {
        public string Command => "delete_asset";
        public string Description => "把资产移入系统回收站(params.path,限 Assets/ 下);可从回收站恢复";
        public string Group => "Assets";
        public bool CanDisable => true;

        public object Execute(JObject @params)
        {
            var path = AssetSupport.RequireProjectPath(@params?["path"]?.Value<string>());

            if (string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(path)))
            {
                throw new CommandException(AssetErrorCodes.AssetNotFound, $"资产不存在:'{path}'");
            }

            if (!AssetDatabase.MoveAssetToTrash(path))
            {
                throw new CommandException(AssetErrorCodes.AssetNotFound, $"删除失败(资产不存在或被占用):'{path}'");
            }

            return new { deleted = true };
        }

        public JObject GetParamsSchema()
        {
            return JObject.Parse(
                @"{""type"":""object"",""properties"":{""path"":{""type"":""string""}},""required"":[""path""]}");
        }
    }
}
