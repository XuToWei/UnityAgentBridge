using System;
using UnityEngine;

namespace AgentBridge
{
    /// <summary>cmd-assets 内部支撑:写路径守卫 + ScriptableObject 类型名解析。</summary>
    internal static class AssetSupport
    {
        /// <summary>
        /// 写路径守卫:必须工程相对、落在 Assets/ 下,不含 '..'。返回规范化(正斜杠、去尾斜杠)的路径。
        /// 越界 / 缺失 → INVALID_ASSET_PATH。
        /// </summary>
        public static string RequireProjectPath(string path, string field = "path")
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new CommandException(AssetErrorCodes.InvalidAssetPath, $"缺 {field}");
            }

            var p = path.Replace('\\', '/').TrimEnd('/');
            if (p != "Assets" && !p.StartsWith("Assets/"))
            {
                throw new CommandException(AssetErrorCodes.InvalidAssetPath, $"{field} 必须在 Assets/ 下:'{path}'");
            }
            if (p.Contains(".."))
            {
                throw new CommandException(AssetErrorCodes.InvalidAssetPath, $"{field} 不允许包含 '..':'{path}'");
            }
            return p;
        }

        /// <summary>按类型名解析 ScriptableObject 子类:先全名,再各程序集全名,最后短名。无命中返回 null。</summary>
        public static Type ResolveScriptableObjectType(string typeName)
        {
            return TypeFinder.Find(typeName, t => typeof(ScriptableObject).IsAssignableFrom(t));
        }
    }
}
