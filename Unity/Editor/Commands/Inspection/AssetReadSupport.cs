using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace AgentBridge
{
    internal static class AssetReadSupport
    {
        public static string Resolve(JObject @params, bool requireFile = false)
        {
            var pathToken = @params?["path"];
            var guidToken = @params?["guid"];
            var hasPath = pathToken != null && pathToken.Type != JTokenType.Null;
            var hasGuid = guidToken != null && guidToken.Type != JTokenType.Null;
            if (!hasPath && !hasGuid)
            {
                throw new CommandException(ErrorCodes.InvalidParams, "缺 path 或 guid");
            }

            var path = hasPath ? NormalizeAndValidate(pathToken.Value<string>()) : null;
            var guid = hasGuid ? guidToken.Value<string>() : null;
            if (hasGuid && (string.IsNullOrEmpty(guid) || guid.Length != 32 ||
                            guid.Any(c => !Uri.IsHexDigit(c))))
            {
                throw new CommandException(ErrorCodes.InvalidParams, "guid 必须是 32 位十六进制字符串");
            }
            var guidPath = hasGuid ? AssetDatabase.GUIDToAssetPath(guid) : null;
            if (hasGuid && string.IsNullOrEmpty(guidPath))
            {
                throw new CommandException(AssetErrorCodes.AssetNotFound, $"GUID 未找到资产:'{guid}'");
            }
            if (hasGuid)
            {
                guidPath = NormalizeAndValidate(guidPath);
            }
            if (hasPath && hasGuid && !PathEquals(path, guidPath))
            {
                throw new CommandException("ASSET_REF_STALE",
                    $"path '{path}' 与 guid '{guid}' 当前指向 '{guidPath}',引用不一致");
            }
            path = hasPath ? path : guidPath;

            var exists = AssetDatabase.IsValidFolder(path) ||
                         !string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(path));
            if (!exists)
            {
                throw new CommandException(AssetErrorCodes.AssetNotFound, $"资产不存在:'{path}'");
            }
            if (requireFile && AssetDatabase.IsValidFolder(path))
            {
                throw new CommandException(ErrorCodes.InvalidParams, $"path 必须是资产文件,不能是文件夹:'{path}'");
            }
            return path;
        }

        public static object Describe(string path)
        {
            var main = AssetDatabase.LoadMainAssetAtPath(path);
            var type = AssetDatabase.GetMainAssetTypeAtPath(path);
            return new
            {
                path,
                guid = AssetDatabase.AssetPathToGUID(path),
                name = main != null ? main.name : System.IO.Path.GetFileName(path),
                type = type?.FullName,
                folder = AssetDatabase.IsValidFolder(path)
            };
        }

        public static object DescribeObject(UnityEngine.Object asset)
        {
            if (asset == null)
            {
                return null;
            }
            long localId = 0;
            string guid = null;
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out guid, out localId);
            return new
            {
                name = asset.name,
                type = asset.GetType().FullName,
                instanceId = asset.GetInstanceID(),
                guid,
                localId
            };
        }

        private static string NormalizeAndValidate(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new CommandException(AssetErrorCodes.InvalidAssetPath, "资产 path 不能为空白");
            }
            var normalized = path.Replace('\\', '/').TrimEnd('/');
            if (!string.Equals(path, path.Trim(), StringComparison.Ordinal) ||
                normalized.StartsWith("/", StringComparison.Ordinal) || normalized.Contains(":") ||
                normalized.Split('/').Any(segment => segment.Length == 0 || segment == "." || segment == "..") ||
                !(normalized == "Assets" || normalized.StartsWith("Assets/", StringComparison.Ordinal) ||
                  normalized == "Packages" || normalized.StartsWith("Packages/", StringComparison.Ordinal)))
            {
                throw new CommandException(AssetErrorCodes.InvalidAssetPath,
                    $"资产 path 必须位于 Assets/ 或 Packages/ 且不能包含相对路径段:'{path}'");
            }
            return normalized;
        }

        private static bool PathEquals(string left, string right)
        {
            var comparison = Application.platform == RuntimePlatform.WindowsEditor ||
                             Application.platform == RuntimePlatform.OSXEditor
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return string.Equals(left, right, comparison);
        }
    }
}
