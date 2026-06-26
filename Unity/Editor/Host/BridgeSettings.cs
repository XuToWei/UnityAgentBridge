using System.IO;
using UnityEditor;
using UnityEngine;

namespace AgentBridge
{
    /// <summary>桥接配置(M4),持久化在 EditorPrefs。对应 file-bridge roadmap 4.4。</summary>
    public static class BridgeSettings
    {
        private const string PollKey = "AgentBridge.PollIntervalMs";
        private const string RootKey = "AgentBridge.RootDir";
        private const int DefaultPollMs = 200;

        /// <summary>轮询间隔(毫秒),默认 200。</summary>
        public static int PollIntervalMs
        {
            get => EditorPrefs.GetInt(PollKey, DefaultPollMs);
            set => EditorPrefs.SetInt(PollKey, value);
        }

        /// <summary>文件通讯根目录,默认 &lt;UnityProject&gt;/AgentBridge。</summary>
        public static string RootDir
        {
            get => EditorPrefs.GetString(RootKey, DefaultRootDir());
            set => EditorPrefs.SetString(RootKey, value);
        }

        private static string DefaultRootDir()
        {
            // Application.dataPath = <project>/Assets,其父目录即工程根。
            var projectRoot = Directory.GetParent(Application.dataPath).FullName;
            return Path.Combine(projectRoot, "AgentBridge");
        }
    }
}
