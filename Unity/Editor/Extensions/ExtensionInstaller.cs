using System.IO;
using UnityEditor;

namespace AgentBridge
{
    /// <summary>
    /// 本地扩展卸载器。扩展目录约定为 Assets/AgentBridgeExtensions/{id}/。
    /// 这里只负责删除已安装目录并刷新 AssetDatabase,不负责下载或安装。
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
