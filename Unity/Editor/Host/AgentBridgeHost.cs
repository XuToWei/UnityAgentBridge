using System;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace AgentBridge
{
    /// <summary>
    /// Unity Editor 侧桥接宿主。用户启用状态与 Bridge root 都有效时，
    /// [InitializeOnLoad] 在 domain reload 后恢复轮询。
    /// 轮询从 EditorApplication.update 主线程启动异步分发；await 默认恢复到 Unity 主线程。
    /// </summary>
    [InitializeOnLoad]
    public static class AgentBridgeHost
    {
        private const double RestoreIntervalSeconds = 1.0f;
        private static FileChannel s_Channel;
        private static double s_LastPollTime;
        private static double s_NextRestoreTime;
        private static bool s_IsProcessing;

        public static bool IsEnabled => BridgeHostState.HasExplicitState ? BridgeHostState.IsEnabled : Directory.Exists(BridgeSettings.RootDir);
        public static bool IsRunning => s_Channel != null && Directory.Exists(s_Channel.RootDir);

        /// <summary>当前是否有已认领的 Exchange 尚未完成响应发布。</summary>
        public static bool IsProcessing => s_IsProcessing;

        static AgentBridgeHost()
        {
            // 包更新期间目录和程序集可能尚未稳定，延迟到 Editor update 恢复。
            ScheduleRestore();
        }

        public static void Start()
        {
            BridgeHostState.SetEnabled(true);
            if (IsRunning) return;
            ScheduleRestore();
            RestoreIfEnabled();
        }

        public static void Stop()
        {
            if (s_IsProcessing)
            {
                Debug.LogWarning(
                    "[AgentBridge] cannot stop while an exchange is still processing.");
                return;
            }

            BridgeHostState.SetEnabled(false);
            CancelRestore();
            var hadChannel = s_Channel != null;
            Deactivate();
            if (hadChannel) Debug.Log("[AgentBridge] stopped.");
        }

        private static void Tick()
        {
            _ = TickAsync();
        }

        private static async Task TickAsync()
        {
            if (s_IsProcessing)
            {
                return;
            }

            var now = EditorApplication.timeSinceStartup;
            if ((now - s_LastPollTime) * 1000.0 < BridgeSettings.PollIntervalMs)
            {
                return;
            }
            s_LastPollTime = now;

            if (!IsRunning)
            {
                Deactivate();
                ScheduleRestore();
                return;
            }

            var channel = s_Channel;
            s_IsProcessing = true;

            try
            {
                // Claim 后直接 await handler，完成后再发布终态响应。
                await channel.TryProcessOneAsync(
                    CommandDispatcher.DispatchAsync,
                    CurrentCommandsVersion);
            }
            catch (Exception e)
            {
                // response commit point 前失败时 processing.json 保留，下轮返回 INTERRUPTED。
                Debug.LogError($"[AgentBridge] failed to process exchange: {e.Message}");
            }
            finally
            {
                s_IsProcessing = false;
            }
        }

        private static void ScheduleRestore()
        {
            if (!IsEnabled)
            {
                CancelRestore();
                return;
            }
            s_NextRestoreTime = 0;
            EditorApplication.update -= RestoreIfEnabled;
            EditorApplication.update += RestoreIfEnabled;
        }

        private static void CancelRestore()
        {
            EditorApplication.update -= RestoreIfEnabled;
        }

        private static void RestoreIfEnabled()
        {
            if (!IsEnabled || IsRunning)
            {
                CancelRestore();
                return;
            }
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            var now = EditorApplication.timeSinceStartup;
            if (now < s_NextRestoreTime) return;
            s_NextRestoreTime = now + RestoreIntervalSeconds;
            if (!FileChannel.TryOpenExisting(BridgeSettings.RootDir, out var channel)) return;
            BridgeHostState.SetEnabled(true);
            Activate(channel);
        }

        private static void Deactivate()
        {
            EditorApplication.update -= Tick;
            s_Channel = null;
        }

        private static void Activate(FileChannel channel)
        {
            s_Channel = channel ?? throw new ArgumentNullException(nameof(channel));
            CancelRestore();
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
