using System;
using UnityEditor;
using UnityEngine;

namespace AgentBridge
{
    /// <summary>
    /// Editor 宿主(M4)。[InitializeOnLoad] 在编辑器加载及每次 domain reload 后重挂轮询。
    /// 轮询在 EditorApplication.update(主线程)内同步分发 → handler 天然主线程。
    /// 对应 file-bridge roadmap 4.4。
    /// </summary>
    [InitializeOnLoad]
    public static class AgentBridgeHost
    {
        private const string ResponsesClearedKey = "AgentBridge.ResponsesCleared";

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
            ReclaimOrphans();
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
            s_Running = true;
            s_LastPollTime = 0;
            Debug.Log($"[AgentBridge] started. root={s_Channel.RootDir} poll={BridgeSettings.PollIntervalMs}ms");
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

        /// <summary>
        /// 每个编辑器会话只清一次 responses/(SessionState 跨 domain reload 存活),在生成首个响应前触发。
        /// 清掉上次会话遗留的陈旧响应;reload 时守卫已置位 → 不清,避免删掉 reload 前刚写出、AI 尚未读取的响应。
        /// 放在写响应前(而非 Start)可天然保证:本次要写的响应在清理之后落地、不被误清。
        /// </summary>
        private static void ClearResponsesOncePerSession()
        {
            if (SessionState.GetBool(ResponsesClearedKey, false))
            {
                return;
            }
            s_Channel.ClearResponses();
            SessionState.SetBool(ResponsesClearedKey, true);
        }

        /// <summary>启动时把 processing/ 中无配对响应的孤儿请求补 INTERRUPTED(at-most-once,不重试)。</summary>
        private static void ReclaimOrphans()
        {
            foreach (var path in s_Channel.ListOrphans())
            {
                var id = s_Channel.IdFromProcessingPath(path);
                try
                {
                    WriteStamped(Response.MakeError(id, ErrorCodes.Interrupted,
                        "request was interrupted by a domain reload before completion"));
                }
                catch (Exception e)
                {
                    Debug.LogError($"[AgentBridge] failed to write INTERRUPTED for {id}: {e.Message}");
                }

                s_Channel.ReleaseProcessed(path);
            }
        }

        private static void Tick()
        {
            var now = EditorApplication.timeSinceStartup;
            if ((now - s_LastPollTime) * 1000.0 < BridgeSettings.PollIntervalMs)
            {
                return;
            }
            s_LastPollTime = now;

            // 排空本轮当前可见的请求。
            while (s_Channel.TryClaimNext(out var claimedPath, out var request, out var rawId))
            {
                Response response;
                if (request == null)
                {
                    response = Response.MakeError(rawId, ErrorCodes.InternalError, "failed to parse request json");
                }
                else
                {
                    if (string.IsNullOrEmpty(request.Id))
                    {
                        request.Id = rawId; // id 回退到文件名
                    }
                    response = CommandDispatcher.Dispatch(request);
                }

                try
                {
                    WriteStamped(response);
                }
                catch (Exception e)
                {
                    Debug.LogError($"[AgentBridge] failed to write response for {rawId}: {e.Message}");
                }

                s_Channel.ReleaseProcessed(claimedPath);
            }
        }

        // 单点盖戳:任何响应写出前统一盖 commandsVersion,覆盖正常/错误/INTERRUPTED 全路径(4.7)。
        private static void WriteStamped(Response response)
        {
            ClearResponsesOncePerSession(); // 生成响应前,每会话清一次 responses/(清上次会话残留)
            response.CommandsVersion = CommandRegistry.Version;
            s_Channel.WriteResponse(response);
        }
    }
}
