using Newtonsoft.Json.Linq;
using UnityEditor;

namespace AgentBridge
{
    /// <summary>
    /// delete_asset(资源写):文件默认移入系统回收站;permanent=true 永久删除。
    /// 文件夹的系统回收站调用可能无限阻塞 Unity 主线程,因此必须显式 permanent=true。
    /// </summary>
    public sealed class DeleteAssetHandler : ICommandHandler
    {
        public string Command => "delete_asset";
        public string Description =>
            "删除资产(path 限 Assets/ 下);文件默认进回收站,permanent=true 永久删除;文件夹必须 permanent=true";
        public string Group => "Assets";
        public bool CanDisable => true;
        public CommandBatchMode BatchMode => CommandBatchMode.NotAllowed;

        public object Execute(JObject @params)
        {
            var path = AssetSupport.RequireAssetChildPath(@params?["path"]?.Value<string>());
            var permanent = @params?["permanent"]?.Value<bool>() ?? false;

            if (string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(path)))
            {
                throw new CommandException(AssetErrorCodes.AssetNotFound, $"资产不存在:'{path}'");
            }

            if (AssetDatabase.IsValidFolder(path) && !permanent)
            {
                throw new CommandException(AssetErrorCodes.AssetDirectoryDeleteRequiresPermanent,
                    $"文件夹不会同步移入系统回收站(该 API 可能阻塞编辑器);确认后请传 permanent=true:'{path}'");
            }

            var deleted = permanent
                ? AssetDatabase.DeleteAsset(path)
                : AssetDatabase.MoveAssetToTrash(path);
            if (!deleted)
            {
                throw new CommandException(AssetErrorCodes.AssetDeleteFailed,
                    $"资产存在但删除失败(可能被占用、只读或受版本控制保护):'{path}'");
            }

            return new { deleted = true, permanent };
        }

        public JObject ParamsSchema { get; } = JObject.Parse(
            @"{""type"":""object"",""properties"":{""path"":{""type"":""string""},""permanent"":{""type"":""boolean"",""default"":false}},""required"":[""path""]}");
    }
}
