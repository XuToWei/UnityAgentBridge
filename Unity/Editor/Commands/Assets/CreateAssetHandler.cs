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

        public object Execute(JObject @params)
        {
            var kind = (@params?["kind"]?.Value<string>() ?? "").ToLowerInvariant();
            var path = AssetSupport.RequireProjectPath(@params?["path"]?.Value<string>());

            switch (kind)
            {
                case "folder": return CreateFolder(path);
                case "text": return CreateText(path, @params?["content"]?.Value<string>() ?? "");
                case "scriptableobject": return CreateScriptableObject(path, @params?["type"]?.Value<string>());
                default:
                    throw new CommandException(AssetErrorCodes.AssetCreateFailed,
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

        private static object CreateText(string path, string content)
        {
            var dir = ParentDir(path);
            if (!AssetDatabase.IsValidFolder(dir))
            {
                throw new CommandException(AssetErrorCodes.AssetCreateFailed, $"父目录不存在:'{dir}'");
            }

            File.WriteAllText(path, content);
            AssetDatabase.ImportAsset(path);
            return new { path, guid = AssetDatabase.AssetPathToGUID(path) };
        }

        private static object CreateScriptableObject(string path, string typeName)
        {
            var type = AssetSupport.ResolveScriptableObjectType(typeName);
            if (type == null)
            {
                throw new CommandException(AssetErrorCodes.UnknownAssetType,
                    $"无法解析为 ScriptableObject 子类:'{typeName}'");
            }

            var dir = ParentDir(path);
            if (!AssetDatabase.IsValidFolder(dir))
            {
                throw new CommandException(AssetErrorCodes.AssetCreateFailed, $"父目录不存在:'{dir}'");
            }

            var so = ScriptableObject.CreateInstance(type);
            AssetDatabase.CreateAsset(so, path);
            AssetDatabase.SaveAssets();
            return new { path, guid = AssetDatabase.AssetPathToGUID(path), type = type.FullName };
        }

        private static string ParentDir(string projectPath)
        {
            var dir = Path.GetDirectoryName(projectPath);
            return string.IsNullOrEmpty(dir) ? "Assets" : dir.Replace('\\', '/');
        }

        public JObject GetParamsSchema()
        {
            return JObject.Parse(
                @"{""type"":""object"",""properties"":{""kind"":{""type"":""string"",""enum"":[""folder"",""text"",""scriptableObject""]},""path"":{""type"":""string""},""content"":{""type"":""string""},""type"":{""type"":""string""}},""required"":[""kind"",""path""]}");
        }
    }
}
