using System;
using UnityEditor;
using UnityEngine;

namespace AgentBridge
{
    /// <summary>
    /// Unity Editor 侧桥接宿主。[InitializeOnLoad] 在编辑器加载及每次 domain reload 后重挂轮询。
    /// 轮询在 EditorApplication.update 主线程内同步分发,因此命令处理器天然运行在主线程。
    /// </summary>
    [InitializeOnLoad]
    public static class AgentBridgeHost
    {
        private static FileChannel s_Channel;
        private static double s_LastPollTime;
        private static bool s_Running;

        public static bool IsRunning => s_Running;

        static AgentBridgeHost()
        {
            // 编辑器加载 / domain reload 后自动启动。
            Start();
        }

        public static void Start()
        {
            if (s_Running)
            {
                return;
            }

            s_Channel = new FileChannel(BridgeSettings.RootDir);
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
            s_Running = true;
            s_LastPollTime = 0;
            Debug.Log(
                $"[AgentBridge] started. root={s_Channel.RootDir} poll={BridgeSettings.PollIntervalMs}ms");
        }

        public static void Stop()
        {
            if (!s_Running)
            {
                return;
            }

            EditorApplication.update -= Tick;
            s_Running = false;
            Debug.Log("[AgentBridge] stopped.");
        }

        private static void Tick()
        {
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
