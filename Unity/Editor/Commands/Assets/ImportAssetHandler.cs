using System.Threading.Tasks;
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
        public CommandBatchMode BatchMode => CommandBatchMode.NotAllowed;

        public Task<object> ExecuteAsync(JObject @params)
        {
            var source = @params?["source"]?.Value<string>();
            if (string.IsNullOrEmpty(source))
            {
                throw new CommandException(ErrorCodes.InvalidParams, "缺 source");
            }
            var destination = AssetSupport.RequireProjectPath(@params?["destination"]?.Value<string>(), "destination");
            destination = AssetSupport.RequireFilePath(destination, "destination");
            var overwrite = @params?["overwrite"]?.Value<bool>() ?? false;

            if (!File.Exists(source))
            {
                throw new CommandException(AssetErrorCodes.AssetSourceNotFound, $"源文件不存在:'{source}'");
            }

            var dir = Path.GetDirectoryName(destination)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(dir) || !AssetDatabase.IsValidFolder(dir))
            {
                throw new CommandException(AssetErrorCodes.InvalidAssetPath, $"目标目录不存在:'{dir}'");
            }

            try
            {
                var published = AssetSupport.PublishExternalAsset(source, destination, overwrite);
                return Task.FromResult<object>(new
                {
                    path = published.Path,
                    guid = published.Guid,
                    type = published.Type != null ? published.Type.FullName : null
                });
            }
            catch (CommandException)
            {
                throw;
            }
            catch (System.Exception ex) when (ex is IOException || ex is System.UnauthorizedAccessException)
            {
                throw new CommandException(AssetErrorCodes.AssetCreateFailed,
                    $"导入文件失败:'{destination}':{ex.Message}");
            }
        }

        public JObject ParamsSchema { get; } = JObject.Parse(
            @"{""type"":""object"",""properties"":{""source"":{""type"":""string""},""destination"":{""type"":""string""},""overwrite"":{""type"":""boolean"",""default"":false}},""required"":[""source"",""destination""]}");
    }
}
