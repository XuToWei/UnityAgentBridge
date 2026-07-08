using System.IO;
using UnityEngine;

namespace AgentBridge
{
    /// <summary>桥接使用的固定配置。</summary>
    public static class BridgeSettings
    {
        private const int FixedPollMs = 200;
        private const string BridgeRootFolder = ".agentbridge";

        /// <summary>轮询间隔(毫秒),固定 200。</summary>
        public static int PollIntervalMs => FixedPollMs;

        /// <summary>文件通讯根目录,固定为 &lt;UnityProject&gt;/.agentbridge。</summary>
        public static string RootDir { get; } = Path.Combine(Directory.GetParent(Application.dataPath).FullName, BridgeRootFolder);
    }
}
