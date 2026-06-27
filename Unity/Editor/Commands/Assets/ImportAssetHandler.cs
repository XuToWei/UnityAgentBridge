using System.IO;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace AgentBridge
{
    /// <summary>
    /// import_asset(资源写):把磁盘上的外部文件复制进工程并导入。
    /// params.source = 任意磁盘路径(只读);params.destination = Assets/ 下目标路径。
    /// source 不存在 → ASSET_SOURCE_NOT_FOUND;destination 越界 → INVALID_ASSET_PATH。
    /// </summary>
    public sealed class ImportAssetHandler : ICommandHandler
    {
        public string Command => "import_asset";
        public string Description => "把外部磁盘文件(source)复制进工程(destination,限 Assets/ 下)并导入,返回 path+guid+type";
        public string Group => "Assets";
        public bool CanDisable => true;

        public object Execute(JObject @params)
        {
            var source = @params?["source"]?.Value<string>();
            if (string.IsNullOrEmpty(source))
            {
                throw new CommandException(ErrorCodes.InvalidParams, "缺 source");
            }
            var destination = AssetSupport.RequireProjectPath(@params?["destination"]?.Value<string>(), "destination");

            if (!File.Exists(source))
            {
                throw new CommandException(AssetErrorCodes.AssetSourceNotFound, $"源文件不存在:'{source}'");
            }

            var dir = Path.GetDirectoryName(destination)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(dir) || !AssetDatabase.IsValidFolder(dir))
            {
                throw new CommandException(AssetErrorCodes.InvalidAssetPath, $"目标目录不存在:'{dir}'");
            }

            File.Copy(source, destination, overwrite: true);
            AssetDatabase.ImportAsset(destination);

            var type = AssetDatabase.GetMainAssetTypeAtPath(destination);
            return new
            {
                path = destination,
                guid = AssetDatabase.AssetPathToGUID(destination),
                type = type != null ? type.FullName : null
            };
        }

        public JObject GetParamsSchema()
        {
            return JObject.Parse(
                @"{""type"":""object"",""properties"":{""source"":{""type"":""string""},""destination"":{""type"":""string""}},""required"":[""source"",""destination""]}");
        }
    }
}
