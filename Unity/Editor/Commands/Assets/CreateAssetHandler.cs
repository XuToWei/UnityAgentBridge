using System.IO;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace AgentBridge
{
    /// <summary>
    /// create_asset(资源写):在工程内创建资产。params.kind = folder / text / scriptableObject。
    /// folder → CreateFolder;text → 写文件+导入(需 params.content);scriptableObject → 按 params.type 名创建。
    /// 路径限 Assets/ 下;即时落盘。
    /// </summary>
    public sealed class CreateAssetHandler : ICommandHandler
    {
        public string Command => "create_asset";
        public string Description => "创建资产(kind=folder/text/scriptableObject,path 限 Assets/ 下);text 需 content、SO 需 type";
        public string Group => "Assets";
        public bool CanDisable => true;
        public CommandBatchMode BatchMode => CommandBatchMode.NotAllowed;

        public object Execute(JObject @params)
        {
            var kind = (@params?["kind"]?.Value<string>() ?? "").ToLowerInvariant();
            var path = AssetSupport.RequireProjectPath(@params?["path"]?.Value<string>());
            var overwrite = @params?["overwrite"]?.Value<bool>() ?? false;

            switch (kind)
            {
                case "folder": return CreateFolder(path);
                case "text":
                    if (@params?["content"] == null)
                    {
                        throw new CommandException(ErrorCodes.InvalidParams, "kind=text 需 content(可为空字符串)");
                    }
                    return CreateText(AssetSupport.RequireFilePath(path), @params["content"].Value<string>(), overwrite);
                case "scriptableobject":
                    return CreateScriptableObject(AssetSupport.RequireFilePath(path),
                        @params?["type"]?.Value<string>(), overwrite);
                default:
                    throw new CommandException(ErrorCodes.InvalidParams,
                        $"未知 kind '{kind}'(应为 folder/text/scriptableObject)");
            }
        }

        private static object CreateFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path))
            {
                return new { path }; // 幂等:已存在
            }

            var parent = ParentDir(path);
            var name = Path.GetFileName(path);
            if (!AssetDatabase.IsValidFolder(parent))
            {
                throw new CommandException(AssetErrorCodes.AssetCreateFailed, $"父目录不存在:'{parent}'");
            }

            var guid = AssetDatabase.CreateFolder(parent, name);
            if (string.IsNullOrEmpty(guid))
            {
                throw new CommandException(AssetErrorCodes.AssetCreateFailed, $"创建文件夹失败:'{path}'");
            }
            return new { path = AssetDatabase.GUIDToAssetPath(guid) };
        }

        private static object CreateText(string path, string content, bool overwrite)
        {
            var dir = ParentDir(path);
            if (!AssetDatabase.IsValidFolder(dir))
            {
                throw new CommandException(AssetErrorCodes.AssetCreateFailed, $"父目录不存在:'{dir}'");
            }

            try
            {
                var published = AssetSupport.PublishTextAsset(path, content, overwrite);
                return new { path = published.Path, guid = published.Guid };
            }
            catch (CommandException)
            {
                throw;
            }
            catch (System.Exception ex) when (ex is IOException || ex is System.UnauthorizedAccessException)
            {
                throw new CommandException(AssetErrorCodes.AssetCreateFailed,
                    $"写入文本资产失败:'{path}':{ex.Message}");
            }
        }

        private static object CreateScriptableObject(string path, string typeName, bool overwrite)
        {
            var type = AssetSupport.ResolveScriptableObjectType(typeName, out var ambiguous);
            if (type == null)
            {
                if (ambiguous)
                {
                    throw new CommandException(AssetErrorCodes.AmbiguousAssetType,
                        $"ScriptableObject 短类型名命中多个类型,请传完整命名空间或程序集限定名:'{typeName}'");
                }
                throw new CommandException(AssetErrorCodes.UnknownAssetType,
                    $"无法解析为 ScriptableObject 子类:'{typeName}'");
            }

            var dir = ParentDir(path);
            if (!AssetDatabase.IsValidFolder(dir))
            {
                throw new CommandException(AssetErrorCodes.AssetCreateFailed, $"父目录不存在:'{dir}'");
            }

            if (AssetSupport.Exists(path))
            {
                if (!overwrite)
                {
                    throw new CommandException(AssetErrorCodes.AssetAlreadyExists,
                        $"目标已存在:'{path}'。如需覆盖请传 overwrite=true。");
                }
                return OverwriteScriptableObject(path, type);
            }

            ScriptableObject so = null;
            try
            {
                // CreateInstance can run user type initializers and throw. Keep it inside the
                // rollback boundary so an overwritten asset is never stranded at backupPath.
                so = ScriptableObject.CreateInstance(type);
                AssetDatabase.CreateAsset(so, path);
                AssetDatabase.SaveAssetIfDirty(so);
                var guid = AssetDatabase.AssetPathToGUID(path);
                if (string.IsNullOrEmpty(guid))
                {
                    throw new CommandException(AssetErrorCodes.AssetCreateFailed,
                        $"创建 ScriptableObject 后未获得 GUID:'{path}'");
                }

                return new { path, guid, type = type.FullName };
            }
            catch (CommandException)
            {
                if (!string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(path)))
                {
                    AssetDatabase.DeleteAsset(path);
                }
                else if (so != null)
                {
                    Object.DestroyImmediate(so);
                }
                throw;
            }
            catch (System.Exception ex)
            {
                if (!string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(path)))
                {
                    AssetDatabase.DeleteAsset(path);
                }
                else if (so != null)
                {
                    Object.DestroyImmediate(so);
                }
                throw new CommandException(AssetErrorCodes.AssetCreateFailed,
                    $"创建 ScriptableObject 失败:'{path}':{ex.Message}");
            }
        }

        private static object OverwriteScriptableObject(string path, System.Type type)
        {
            var originalGuid = AssetDatabase.AssetPathToGUID(path);
            var originalFullPath = AssetSupport.ToAbsolutePath(path);
            if (string.IsNullOrEmpty(originalGuid) || !File.Exists(originalFullPath))
            {
                throw new CommandException(AssetErrorCodes.AssetCreateFailed,
                    $"覆盖目标不是可替换的文件资产:'{path}'");
            }

            var extension = Path.GetExtension(path);
            var tempPath = AssetDatabase.GenerateUniqueAssetPath(
                ParentDir(path) + "/__AgentBridgeReplacement_" +
                System.Guid.NewGuid().ToString("N") + extension);
            ScriptableObject replacement = null;
            try
            {
                replacement = ScriptableObject.CreateInstance(type);
                AssetDatabase.CreateAsset(replacement, tempPath);
                AssetDatabase.SaveAssetIfDirty(replacement);

                var tempFullPath = AssetSupport.ToAbsolutePath(tempPath);
                if (!File.Exists(tempFullPath))
                {
                    throw new CommandException(AssetErrorCodes.AssetCreateFailed,
                        $"临时 ScriptableObject 未落盘:'{tempPath}'");
                }
                var replacementBytes = File.ReadAllBytes(tempFullPath);
                if (!AssetDatabase.DeleteAsset(tempPath))
                {
                    throw new CommandException(AssetErrorCodes.AssetCreateFailed,
                        $"无法清理临时 ScriptableObject:'{tempPath}'");
                }
                replacement = null;

                // Replace only the asset payload. Keeping the original .meta preserves its
                // GUID, so every existing project reference still targets this asset path.
                var published = AssetSupport.PublishBytesAsset(
                    path,
                    replacementBytes,
                    true,
                    _ =>
                    {
                        var loaded = AssetDatabase.LoadMainAssetAtPath(path);
                        if (loaded == null || !type.IsInstanceOfType(loaded))
                        {
                            throw new CommandException(AssetErrorCodes.AssetCreateFailed,
                                $"覆盖后资产类型验证失败:'{path}',期望 {type.FullName}");
                        }
                    });
                return new { path, guid = published.Guid, type = type.FullName };
            }
            catch (System.Exception ex)
            {
                if (!string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(tempPath)))
                {
                    AssetDatabase.DeleteAsset(tempPath);
                }
                else if (replacement != null)
                {
                    Object.DestroyImmediate(replacement);
                }
                if (ex is CommandException)
                {
                    throw;
                }
                throw new CommandException(AssetErrorCodes.AssetCreateFailed,
                    $"覆盖 ScriptableObject 失败:'{path}':{ex.Message}");
            }
        }

        private static string ParentDir(string projectPath)
        {
            var dir = Path.GetDirectoryName(projectPath);
            return string.IsNullOrEmpty(dir) ? "Assets" : dir.Replace('\\', '/');
        }

        public JObject ParamsSchema { get; } = JObject.Parse(
            @"{""type"":""object"",""properties"":{""kind"":{""type"":""string"",""enum"":[""folder"",""text"",""scriptableObject""]},""path"":{""type"":""string""},""content"":{""type"":""string""},""type"":{""type"":""string""},""overwrite"":{""type"":""boolean"",""default"":false}},""required"":[""kind"",""path""],""oneOf"":[{""properties"":{""kind"":{""const"":""folder""}},""required"":[""kind""]},{""properties"":{""kind"":{""const"":""text""}},""required"":[""kind"",""content""]},{""properties"":{""kind"":{""const"":""scriptableObject""}},""required"":[""kind"",""type""]}]}");
    }
}
