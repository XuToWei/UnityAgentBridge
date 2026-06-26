using System.IO;
using UnityEditor;

namespace AgentBridge
{
    /// <summary>
    /// 扩展卸载(extension-manager EM3,纯本地)。扩展由用户放入 Assets/AgentBridgeExtensions/{id}/,
    /// 本类只负责卸载(删目录 + Refresh);安装/获取不在范围(2026-06-25 纯本地重订)。
    /// </summary>
    public static class ExtensionInstaller
    {
        public const string InstallRoot = "Assets/AgentBridgeExtensions";

        /// <summary>卸载:删安装目录 + Refresh。目录不存在返回 false(不抛)。</summary>
        public static bool Uninstall(string id)
        {
            var destRel = $"{InstallRoot}/{id}";
            if (!Directory.Exists(destRel))
            {
                return false;
            }
            AssetDatabase.DeleteAsset(destRel); // 连同 .meta 删除
            AssetDatabase.Refresh();
            return true;
        }
    }
}
