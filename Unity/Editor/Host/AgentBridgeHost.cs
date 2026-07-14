using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace AgentBridge
{
    /// <summary>
    /// Unity Editor 侧桥接宿主。已有 Bridge root 时，[InitializeOnLoad] 在 domain reload 后恢复轮询。
    /// 轮询在 EditorApplication.update 主线程内同步分发,因此命令处理器天然运行在主线程。
    /// </summary>
    [InitializeOnLoad]
    public static class AgentBridgeHost
    {
        private static FileChannel s_Channel;
        private static double s_LastPollTime;

        public static bool IsRunning =>
            s_Channel != null && Directory.Exists(s_Channel.RootDir);

        static AgentBridgeHost()
        {
            // 首次加载不创建目录；仅恢复已经启用过的 Bridge root。
            if (FileChannel.TryOpenExisting(BridgeSettings.RootDir, out var channel))
            {
                Activate(channel);
            }
        }

        public static void Start()
        {
            if (IsRunning)
            {
                return;
            }

            // Start 只打开现有目录；目录创建由 AgentBridgeWindow 的启用按钮负责。
            EditorApplication.update -= Tick;
            s_Channel = null;
            if (FileChannel.TryOpenExisting(BridgeSettings.RootDir, out var channel))
            {
                Activate(channel);
            }
        }

        public static void Stop()
        {
            EditorApplication.update -= Tick;
            if (s_Channel == null)
            {
                return;
            }

            s_Channel = null;
            Debug.Log("[AgentBridge] stopped.");
        }

        private static void Tick()
        {
            if (!IsRunning)
            {
                Stop();
                return;
            }

            var now = EditorApplication.timeSinceStartup;
            if ((now - s_LastPollTime) * 1000.0 < BridgeSettings.PollIntervalMs)
            {
                return;
            }
            s_LastPollTime = now;

            try
            {
                // FileChannel 在一个入口内完成 Claim、恢复、分发与终态响应发布。
                s_Channel.TryProcessOne(
                    CommandDispatcher.Dispatch,
                    CurrentCommandsVersion);
            }
            catch (Exception e)
            {
                // response commit point 前失败时 processing.json 保留，下轮返回 INTERRUPTED。
                Debug.LogError($"[AgentBridge] failed to process exchange: {e.Message}");
            }
        }

        private static void Activate(FileChannel channel)
        {
            s_Channel = channel ?? throw new ArgumentNullException(nameof(channel));
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
            s_LastPollTime = 0;
            Debug.Log(
                $"[AgentBridge] started. root={s_Channel.RootDir} poll={BridgeSettings.PollIntervalMs}ms");
        }

        private static string CurrentCommandsVersion()
        {
            try
            {
                return CommandRegistry.Version;
            }
            catch (Exception e)
            {
                // 命令发现异常不能再导致已认领请求无响应；空串仍保留字段。
                Debug.LogError($"[AgentBridge] failed to compute commandsVersion: {e.Message}");
                return "";
            }
        }
    }
}
