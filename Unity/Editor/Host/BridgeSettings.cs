using System.IO;
using UnityEngine;

namespace AgentBridge
{
    /// <summary>桥接使用的固定配置。</summary>
    public static class BridgeSettings
    {
        /// <summary>轮询间隔(毫秒),固定 200。</summary>
        public const int PollIntervalMs = 200;

        /// <summary>文件通讯根目录,固定为 &lt;UnityProject&gt;/.agentbridge。</summary>
        public static string RootDir { get; } = Path.Combine(
            Directory.GetParent(Application.dataPath).FullName,
            ".agentbridge");
    }
}
