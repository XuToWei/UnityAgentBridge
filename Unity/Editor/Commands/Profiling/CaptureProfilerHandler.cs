using System;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditorInternal;

namespace AgentBridge
{
    /// <summary>
    /// capture_profiler:自动或分步录制 Profiler capture。
    /// 活动录制的所有权和 captureId 保存在 SessionState，domain reload 后仍可查询和停止。
    /// </summary>
    public sealed class CaptureProfilerHandler : ICommandHandler
    {
        internal const int DefaultFrameCount = 300;
        internal const int DefaultTimeoutMs = 30000;
        internal const int DefaultPollIntervalMs = 50;

        private const string BusyError = "PROFILER_CAPTURE_BUSY";
        private const string NotActiveError = "PROFILER_CAPTURE_NOT_ACTIVE";
        private const string RecordingNotManagedError = "PROFILER_RECORDING_NOT_MANAGED";
        private const string RecoveryRequiredError = "PROFILER_CAPTURE_RECOVERY_REQUIRED";
        private const string CaptureFailedError = "PROFILER_CAPTURE_FAILED";
        private const string StopFailedError = "PROFILER_CAPTURE_STOP_FAILED";
        private const string SaveFailedError = "PROFILER_CAPTURE_SAVE_FAILED";
        private const string RestoreFailedError = "PROFILER_CAPTURE_RESTORE_FAILED";

        public string Command => "capture_profiler";

        public string Description =>
            "自动录制 Profiler 帧并保存稳定 captureId；区分 observed 与停止后实际 retained 帧，action=capture 一键等待后停止，start/stop/status 支持跨请求控制";

        public string Group => "Profiling";
        public bool CanDisable => true;
        public CommandBatchMode BatchMode => CommandBatchMode.NotAllowed;

        public async Task<object> ExecuteAsync(JObject @params)
        {
            var options = ProfilerCaptureOptions.Parse(@params);
            try
            {
                switch (options.Action)
                {
                    case "capture":
                        return await CaptureAsync(options);
                    case "start":
                        return Start(options);
                    case "stop":
                        return Stop();
                    case "status":
                        return Status();
                    default:
                        throw new CommandException(
                            ErrorCodes.InvalidParams,
                            $"不支持 capture_profiler action='{options.Action}'");
                }
            }
            catch (CommandException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new CommandException(
                    CaptureFailedError,
                    $"Profiler 捕获失败:{ex.GetType().Name}:{ex.Message}");
            }
        }

        private static async Task<ProfilerCaptureResult> CaptureAsync(
            ProfilerCaptureOptions options)
        {
            var state = Begin(options);
            try
            {
                var stopReason = await WaitForFramesAsync(state);
                return Finish(state, "capture", stopReason);
            }
            catch
            {
                AbortAfterUnexpectedFailure(state);
                throw;
            }
        }

        private static ProfilerCaptureResult Start(ProfilerCaptureOptions options)
        {
            var state = Begin(options);
            return ProfilerCaptureSupport.CreateResult(
                state,
                "start",
                "recording",
                ProfilerDriver.enabled,
                ProfilerDriver.profileEditor);
        }

        private static ProfilerCaptureResult Stop()
        {
            var state = ProfilerCaptureStore.ReadActive();
            if (state == null)
            {
                if (ProfilerDriver.enabled)
                {
                    throw new CommandException(
                        RecordingNotManagedError,
                        "Profiler 正在录制，但不是由 capture_profiler 启动；命令不会停止该外部录制");
                }
                throw new CommandException(
                    NotActiveError,
                    "没有由 capture_profiler 管理的活动录制");
            }

            var stopReason = ProfilerDriver.enabled
                ? "stopped"
                : "recordingStopped";
            return Finish(state, "stop", stopReason);
        }

        private static ProfilerCaptureResult Status()
        {
            var state = ProfilerCaptureStore.ReadActive();
            if (state != null)
            {
                try
                {
                    ObserveFrames(state);
                }
                catch (CommandException ex)
                    when (ex.Code == ProfilerErrorCodes.CaptureChanged)
                {
                    // status 保持只读：暴露 session 已变化，等待 stop 完成录制状态清理。
                }
                ProfilerCaptureStore.WriteActive(state);
                var recording = ProfilerDriver.enabled;
                var stopReason = state.CaptureChanged
                    ? "captureChanged"
                    : state.FrameHistoryGap
                    ? "frameHistoryGap"
                    : state.RecordedFrameCount >= state.RequestedFrameCount
                        ? "requestedFrameCount"
                        : recording
                            ? "recording"
                            : "recordingStopped";
                return ProfilerCaptureSupport.CreateResult(
                    state,
                    "status",
                    stopReason,
                    recording,
                    ProfilerDriver.profileEditor);
            }

            var completed = ProfilerCaptureStore.ReadLast();
            if (completed != null)
            {
                return ProfilerCaptureSupport.CreateResult(
                    completed,
                    "status",
                    completed.StopReason,
                    ProfilerDriver.enabled,
                    ProfilerDriver.profileEditor);
            }

            return ProfilerCaptureSupport.CreateEmptyStatus(
                ProfilerDriver.enabled,
                ProfilerDriver.profileEditor);
        }

        private static ProfilerCaptureState Begin(ProfilerCaptureOptions options)
        {
            var existing = ProfilerCaptureStore.ReadActive();
            if (existing != null)
            {
                if (ProfilerDriver.enabled)
                {
                    throw new CommandException(
                        BusyError,
                        $"capture_profiler 已在录制 captureId={existing.CaptureId}；请先 status 或 stop");
                }
                throw new CommandException(
                    RecoveryRequiredError,
                    $"captureId={existing.CaptureId} 的录制已在外部停止；请先调用 action=stop 完成保存和状态恢复");
            }
            if (ProfilerDriver.enabled)
            {
                throw new CommandException(
                    RecordingNotManagedError,
                    "Profiler 已由外部开启录制；capture_profiler 不会清空、接管或停止该录制");
            }

            var captureId = Guid.NewGuid().ToString("N");
            var originalProfileEditor = ProfilerDriver.profileEditor;
            var state = new ProfilerCaptureState
            {
                CaptureId = captureId,
                Active = true,
                RequestedFrameCount = options.FrameCount,
                RecordedFrameCount = 0,
                TimeoutMs = options.TimeoutMs,
                PollIntervalMs = options.PollIntervalMs,
                ClearExisting = options.ClearExisting,
                SaveRequested = options.Save,
                OriginalProfileEditor = originalProfileEditor,
                RequestedProfileEditor = options.ProfileEditor,
                StartFrameAnchorIndex = ProfilerDriver.lastFrameIndex,
                LastObservedFrameIndex = ProfilerDriver.lastFrameIndex,
                StartedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                Path = options.Save
                    ? ProfilerCaptureSupport.BuildCapturePath(captureId)
                    : null,
                RelativePath = options.Save
                    ? ProfilerCaptureSupport.BuildRelativePath(captureId)
                    : null
            };

            // 先建立所有权标记，再修改 Profiler；domain reload 发生在任一步时，
            // stop/status 仍能识别该录制属于本命令。
            ProfilerCaptureStore.WriteActive(state);
            try
            {
                if (options.ProfileEditor.HasValue)
                {
                    ProfilerDriver.profileEditor = options.ProfileEditor.Value;
                }
                if (options.ClearExisting)
                {
                    ProfilerDriver.ClearAllFrames();
                }

                // clear 可能重置或跳变索引；以 clear 后的实际 latest 同时作为
                // 捕获起始锚点和增量观察锚点。
                state.StartFrameAnchorIndex = ProfilerDriver.lastFrameIndex;
                state.LastObservedFrameIndex =
                    state.StartFrameAnchorIndex;
                ProfilerCaptureStore.WriteActive(state);
                ProfilerDriver.enabled = true;
                EnsureCaptureSession(state);
                ProfilerCaptureStore.WriteActive(state);
                return state;
            }
            catch (Exception ex)
            {
                var rollbackError = RollbackBegin(state);
                var detail = rollbackError == null
                    ? ex.Message
                    : $"{ex.Message}；回滚失败:{rollbackError.Message}";
                throw new CommandException(
                    CaptureFailedError,
                    $"无法启动 Profiler 捕获:{detail}");
            }
        }

        private static async Task<string> WaitForFramesAsync(
            ProfilerCaptureState state)
        {
            var startedAt = EditorApplication.timeSinceStartup;
            while (true)
            {
                ObserveFrames(state);
                if (state.FrameHistoryGap)
                {
                    return "frameHistoryGap";
                }
                if (state.RecordedFrameCount >= state.RequestedFrameCount)
                {
                    return "requestedFrameCount";
                }
                if (!ProfilerDriver.enabled)
                {
                    return "recordingStopped";
                }

                var elapsedMs =
                    (EditorApplication.timeSinceStartup - startedAt) * 1000.0;
                if (elapsedMs >= state.TimeoutMs)
                {
                    return "timeout";
                }
                await TaskExtension.Delay(state.PollIntervalMs);
            }
        }

        private static void ObserveFrames(ProfilerCaptureState state)
        {
            EnsureCaptureSession(state);
            var latestFrameIndex = ProfilerDriver.lastFrameIndex;
            var advance = ProfilerCaptureFrameCounter.CountNewFrames(
                state.LastObservedFrameIndex,
                latestFrameIndex,
                ProfilerDriver.GetPreviousFrameIndex);
            if (!advance.Changed)
            {
                return;
            }

            state.RecordedFrameCount = ProfilerCaptureFrameCounter.SaturatingAdd(
                state.RecordedFrameCount,
                advance.NewFrameCount);
            state.LastObservedFrameIndex = latestFrameIndex;
            if (!advance.AnchorFound)
            {
                state.FrameHistoryGap = true;
            }
        }

        private static ProfilerCaptureResult Finish(
            ProfilerCaptureState state,
            string action,
            string requestedStopReason)
        {
            try
            {
                // 在触碰 enabled/profileEditor 前确认我们仍拥有同一个原生 session。
                // 若用户已开始另一段录制，绝不能把它停止或保存到旧 captureId。
                EnsureCaptureSession(state);
            }
            catch (CommandException ex)
                when (ex.Code == ProfilerErrorCodes.CaptureChanged)
            {
                CompleteChangedCaptureWithoutMutation(state);
                throw CaptureChanged(state);
            }

            Exception stopError = null;
            try
            {
                if (ProfilerDriver.enabled)
                {
                    ProfilerDriver.enabled = false;
                }
            }
            catch (Exception ex)
            {
                stopError = ex;
            }

            if (stopError != null)
            {
                state.StopReason = "stopFailed";
                ProfilerCaptureStore.WriteActive(state);
                throw new CommandException(
                    StopFailedError,
                    $"无法停止 captureId={state.CaptureId}:{stopError.Message}");
            }

            // enabled=false 后再做最后一次导航，计入停止前最后一批实际帧。
            try
            {
                ObserveFrames(state);
            }
            catch (CommandException ex)
                when (ex.Code == ProfilerErrorCodes.CaptureChanged)
            {
                state.CaptureChanged = true;
            }
            catch (Exception)
            {
                state.FrameHistoryGap = true;
            }

            // observed 可以跨滚动缓冲累计，不能代表保存时仍有多少帧。
            // 只在 recording=false 后统计 retained，避免扫描期间帧继续淘汰。
            try
            {
                if (!state.CaptureChanged)
                {
                    MeasureRetainedFrames(state);
                }
            }
            catch (CommandException ex)
                when (ex.Code == ProfilerErrorCodes.CaptureChanged)
            {
                state.CaptureChanged = true;
            }
            catch (Exception)
            {
                state.RetainedFrameCount = 0;
                state.RetainedFrameCountMeasured = true;
                state.RetainedFrameCountExact = false;
                state.RetainedStartAnchorFound = false;
            }

            var stopReason = state.CaptureChanged
                ? "captureChanged"
                : !state.RetainedFrameCountExact
                ? "retainedCountIncomplete"
                : state.RetainedFrameCount >= state.RequestedFrameCount
                    ? "requestedFrameCount"
                    : state.RecordedFrameCount >= state.RequestedFrameCount
                        ? "frameHistoryLimit"
                        : requestedStopReason;

            Exception saveError = null;
            Exception restoreError = null;
            try
            {
                if (state.SaveRequested && !state.CaptureChanged)
                {
                    Save(state);
                }
            }
            catch (Exception ex)
            {
                saveError = ex;
            }
            finally
            {
                try
                {
                    ProfilerDriver.profileEditor = state.OriginalProfileEditor;
                }
                catch (Exception ex)
                {
                    restoreError = ex;
                }
            }

            state.Active = false;
            state.StopReason = saveError != null
                ? "saveFailed"
                : restoreError != null
                    ? "restoreFailed"
                    : stopReason;
            state.StoppedAt =
                DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            state.ProfileEditorRestored =
                restoreError == null &&
                ProfilerDriver.profileEditor == state.OriginalProfileEditor;
            if (saveError == null && restoreError == null &&
                !state.CaptureChanged)
            {
                // 让后续 overview/get_profiler_data 将当前 Unity session
                // 解析回自动捕获分配的同一个稳定 id。
                ProfilerCaptureIdentity.AssignManagedCaptureId(
                    state.CaptureId);
            }
            ProfilerCaptureStore.Complete(state);

            if (saveError != null)
            {
                throw new CommandException(
                    SaveFailedError,
                    $"无法保存 captureId={state.CaptureId} 到 '{state.Path}':{saveError.Message}");
            }
            if (restoreError != null)
            {
                throw new CommandException(
                    RestoreFailedError,
                    $"captureId={state.CaptureId} 已停止，但无法恢复 profileEditor:{restoreError.Message}");
            }
            if (state.CaptureChanged)
            {
                throw CaptureChanged(state);
            }

            return ProfilerCaptureSupport.CreateResult(
                state,
                action,
                state.StopReason,
                ProfilerDriver.enabled,
                ProfilerDriver.profileEditor);
        }

        private static void MeasureRetainedFrames(
            ProfilerCaptureState state)
        {
            EnsureCaptureSession(state);
            var firstFrameIndex = ProfilerDriver.firstFrameIndex;
            var lastFrameIndex = ProfilerDriver.lastFrameIndex;
            var retained = ProfilerRetainedFrameCounter.Count(
                state.StartFrameAnchorIndex,
                firstFrameIndex,
                lastFrameIndex,
                ProfilerDriver.GetPreviousFrameIndex);

            // enabled=false 后索引通常稳定；再次读取可以识别原生缓冲在扫描期间
            // 被外部加载/清空等变化，避免把竞争条件报告成精确计数。
            var bufferUnchanged =
                firstFrameIndex == ProfilerDriver.firstFrameIndex &&
                lastFrameIndex == ProfilerDriver.lastFrameIndex;
            state.RetainedFrameCount = retained.FrameCount;
            state.RetainedFrameCountMeasured = true;
            state.RetainedFrameCountExact =
                retained.CountExact && bufferUnchanged;
            state.RetainedStartAnchorFound =
                retained.StartAnchorFound;
        }

        private static void EnsureCaptureSession(
            ProfilerCaptureState state)
        {
            if (ProfilerCaptureSessionGuard.ValidateOrBind(
                    state,
                    ProfilerCaptureIdentity.TryGetSessionGuid()))
            {
                return;
            }

            throw new CommandException(
                ProfilerErrorCodes.CaptureChanged,
                $"captureId={state.CaptureId} 的 Profiler session 已在录制期间变化");
        }

        private static CommandException CaptureChanged(
            ProfilerCaptureState state)
        {
            return new CommandException(
                ProfilerErrorCodes.CaptureChanged,
                $"captureId={state.CaptureId} 录制期间 Profiler session 已变化；为避免影响外部录制或错误归档，命令不会继续操作或保存当前缓冲");
        }

        private static void CompleteChangedCaptureWithoutMutation(
            ProfilerCaptureState state)
        {
            state.CaptureChanged = true;
            state.Active = false;
            state.StopReason = "captureChanged";
            state.StoppedAt =
                DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            state.ProfileEditorRestored =
                ProfilerDriver.profileEditor ==
                state.OriginalProfileEditor;
            ProfilerCaptureStore.Complete(state);
        }

        private static void Save(ProfilerCaptureState state)
        {
            var directory = Path.GetDirectoryName(state.Path);
            if (string.IsNullOrEmpty(directory))
            {
                throw new InvalidOperationException("Profiler capture 保存目录为空");
            }
            Directory.CreateDirectory(directory);
            if (File.Exists(state.Path))
            {
                throw new IOException($"目标文件已存在:{state.Path}");
            }

            // Unity 2022.3 返回 void，Unity 6 返回 bool；作为 statement 调用可同时兼容。
            ProfilerDriver.SaveProfile(state.Path);
            if (!File.Exists(state.Path))
            {
                throw new IOException("ProfilerDriver.SaveProfile 未生成目标文件");
            }
            state.Saved = true;
        }

        private static Exception RollbackBegin(ProfilerCaptureState state)
        {
            Exception firstError = null;
            try
            {
                if (ProfilerDriver.enabled)
                {
                    ProfilerDriver.enabled = false;
                }
            }
            catch (Exception ex)
            {
                firstError = ex;
            }
            try
            {
                ProfilerDriver.profileEditor = state.OriginalProfileEditor;
            }
            catch (Exception ex)
            {
                firstError = firstError ?? ex;
            }
            try
            {
                ProfilerCaptureStore.EraseActive();
            }
            catch (Exception ex)
            {
                firstError = firstError ?? ex;
            }
            return firstError;
        }

        private static void AbortAfterUnexpectedFailure(
            ProfilerCaptureState state)
        {
            var active = ProfilerCaptureStore.ReadActive();
            if (active == null ||
                !string.Equals(
                    active.CaptureId,
                    state.CaptureId,
                    StringComparison.Ordinal))
            {
                return;
            }

            if (state.CaptureChanged)
            {
                try
                {
                    CompleteChangedCaptureWithoutMutation(state);
                }
                catch (Exception)
                {
                    // 原始 CAPTURE_CHANGED 异常优先。
                }
                return;
            }

            try
            {
                if (ProfilerDriver.enabled)
                {
                    ProfilerDriver.enabled = false;
                }
            }
            catch (Exception)
            {
                // 原始异常优先；活动标记保留，允许后续 action=stop 重试。
                return;
            }

            try
            {
                ProfilerDriver.profileEditor = state.OriginalProfileEditor;
            }
            catch (Exception)
            {
                // 仍将终止状态持久化，status 会暴露 profileEditorRestored=false。
            }
            state.Active = false;
            state.StopReason = state.CaptureChanged
                ? "captureChanged"
                : "failed";
            state.StoppedAt =
                DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            state.ProfileEditorRestored =
                ProfilerDriver.profileEditor == state.OriginalProfileEditor;
            try
            {
                ProfilerCaptureStore.Complete(state);
            }
            catch (Exception)
            {
                // 不覆盖最初导致自动捕获失败的异常。
            }
        }

        public JObject ParamsSchema { get; } = JObject.Parse(@"{
  ""type"": ""object"",
  ""additionalProperties"": false,
  ""properties"": {
    ""action"": {
      ""type"": ""string"",
      ""enum"": [""capture"", ""start"", ""stop"", ""status""],
      ""default"": ""capture"",
      ""description"": ""capture 一键录制并等待；start/stop/status 用于跨请求控制同一个 captureId。""
    },
    ""frameCount"": {
      ""type"": ""integer"",
      ""minimum"": 1,
      ""maximum"": 2000,
      ""default"": 300,
      ""description"": ""目标实际 Profiler 帧数；按导航累计 observed，停止后复核 retained，只有保存窗口仍含足量帧时 complete=true。""
    },
    ""timeoutMs"": {
      ""type"": ""integer"",
      ""minimum"": 1000,
      ""maximum"": 120000,
      ""default"": 30000,
      ""description"": ""action=capture 的最长等待时间；超时会停止并保存已有帧，返回 complete=false。""
    },
    ""pollIntervalMs"": {
      ""type"": ""integer"",
      ""minimum"": 10,
      ""maximum"": 1000,
      ""default"": 50,
      ""description"": ""自动捕获检查新 Profiler 帧的间隔。""
    },
    ""clearExisting"": {
      ""type"": ""boolean"",
      ""default"": true,
      ""description"": ""开始前是否清空 Profiler 当前帧缓冲。""
    },
    ""profileEditor"": {
      ""type"": ""boolean"",
      ""description"": ""可选地录制 Editor 或 Player；停止后恢复调用前的 profileEditor。""
    },
    ""save"": {
      ""type"": ""boolean"",
      ""default"": true,
      ""description"": ""停止后是否保存到 .agentbridge/profiler/<captureId>.data。""
    }
  }
}");
    }

    internal sealed class ProfilerCaptureOptions
    {
        internal string Action { get; private set; }
        internal int FrameCount { get; private set; }
        internal int TimeoutMs { get; private set; }
        internal int PollIntervalMs { get; private set; }
        internal bool ClearExisting { get; private set; }
        internal bool? ProfileEditor { get; private set; }
        internal bool Save { get; private set; }

        internal static ProfilerCaptureOptions Parse(JObject @params)
        {
            var options = new ProfilerCaptureOptions
            {
                Action = @params?["action"]?.Value<string>() ?? "capture",
                FrameCount = @params?["frameCount"]?.ToObject<int?>() ??
                             CaptureProfilerHandler.DefaultFrameCount,
                TimeoutMs = @params?["timeoutMs"]?.ToObject<int?>() ??
                            CaptureProfilerHandler.DefaultTimeoutMs,
                PollIntervalMs = @params?["pollIntervalMs"]?.ToObject<int?>() ??
                                 CaptureProfilerHandler.DefaultPollIntervalMs,
                ClearExisting =
                    @params?["clearExisting"]?.ToObject<bool?>() ?? true,
                ProfileEditor =
                    @params?["profileEditor"]?.ToObject<bool?>(),
                Save = @params?["save"]?.ToObject<bool?>() ?? true
            };
            options.Validate();
            return options;
        }

        private void Validate()
        {
            if (Action != "capture" && Action != "start" &&
                Action != "stop" && Action != "status")
            {
                throw new CommandException(
                    ErrorCodes.InvalidParams,
                    "action 必须是 capture/start/stop/status");
            }
            if (FrameCount < 1 || FrameCount > 2000)
            {
                throw new CommandException(
                    ErrorCodes.InvalidParams,
                    "frameCount 必须在 1..2000");
            }
            if (TimeoutMs < 1000 || TimeoutMs > 120000)
            {
                throw new CommandException(
                    ErrorCodes.InvalidParams,
                    "timeoutMs 必须在 1000..120000");
            }
            if (PollIntervalMs < 10 || PollIntervalMs > 1000)
            {
                throw new CommandException(
                    ErrorCodes.InvalidParams,
                    "pollIntervalMs 必须在 10..1000");
            }
        }
    }

    /// <summary>
    /// 纯托管帧推进算法。只比较导航链中的相等锚点，不对 frame index
    /// 做大小或连续性假设。
    /// </summary>
    internal static class ProfilerCaptureFrameCounter
    {
        internal const int MaxFramesPerObservation = 4096;

        internal static ProfilerFrameAdvance CountNewFrames(
            int previousLatestFrameIndex,
            int latestFrameIndex,
            Func<int, int> getPreviousFrameIndex)
        {
            if (getPreviousFrameIndex == null)
            {
                throw new ArgumentNullException(nameof(getPreviousFrameIndex));
            }
            if (latestFrameIndex == previousLatestFrameIndex)
            {
                return new ProfilerFrameAdvance(
                    false,
                    0,
                    true);
            }
            if (latestFrameIndex < 0)
            {
                return new ProfilerFrameAdvance(
                    true,
                    0,
                    previousLatestFrameIndex < 0);
            }

            var current = latestFrameIndex;
            var count = 0;
            while (current >= 0 &&
                   current != previousLatestFrameIndex &&
                   count < MaxFramesPerObservation)
            {
                count++;
                var previous = getPreviousFrameIndex(current);
                if (previous == current)
                {
                    return new ProfilerFrameAdvance(true, count, false);
                }
                current = previous;
            }

            var anchorFound = current == previousLatestFrameIndex;
            return new ProfilerFrameAdvance(true, count, anchorFound);
        }

        internal static int SaturatingAdd(int left, int right)
        {
            if (right > int.MaxValue - left)
            {
                return int.MaxValue;
            }
            return left + right;
        }
    }

    internal readonly struct ProfilerFrameAdvance
    {
        internal ProfilerFrameAdvance(
            bool changed,
            int newFrameCount,
            bool anchorFound)
        {
            Changed = changed;
            NewFrameCount = newFrameCount;
            AnchorFound = anchorFound;
        }

        internal bool Changed { get; }
        internal int NewFrameCount { get; }
        internal bool AnchorFound { get; }
    }

    /// <summary>
    /// 将托管 captureId 绑定到 Unity Profiler 的原生 session。反射 API 不可用
    /// （null）时保持兼容；一旦已绑定，显式 Empty 或不同 GUID 都视为缓冲被替换。
    /// </summary>
    internal static class ProfilerCaptureSessionGuard
    {
        internal static bool ValidateOrBind(
            ProfilerCaptureState state,
            Guid? currentSessionGuid)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }
            if (state.CaptureChanged)
            {
                return false;
            }
            if (!currentSessionGuid.HasValue)
            {
                // 兼容被裁剪或临时不可用的 Editor API；目标 Unity 2021.3–6
                // 均提供该 metadata GUID。
                return true;
            }

            var current = currentSessionGuid.Value;
            if (string.IsNullOrEmpty(state.ProfilerSessionGuid))
            {
                if (current != Guid.Empty)
                {
                    state.ProfilerSessionGuid = current.ToString("N");
                }
                return true;
            }

            var matches =
                current != Guid.Empty &&
                string.Equals(
                    state.ProfilerSessionGuid,
                    current.ToString("N"),
                    StringComparison.Ordinal);
            if (!matches)
            {
                state.CaptureChanged = true;
            }
            return matches;
        }
    }

    /// <summary>
    /// 停止录制后计算仍可从 Profiler 缓冲导航到的捕获帧数。
    /// 起始锚点仍存在时不计锚点本身；锚点已被 FIFO 淘汰时，当前保留帧
    /// 均晚于锚点，因此统计到 firstFrameIndex 为止。
    /// </summary>
    internal static class ProfilerRetainedFrameCounter
    {
        internal const int MaxFramesToCount = 8192;

        internal static ProfilerRetainedFrameCount Count(
            int startFrameAnchorIndex,
            int firstFrameIndex,
            int lastFrameIndex,
            Func<int, int> getPreviousFrameIndex)
        {
            if (getPreviousFrameIndex == null)
            {
                throw new ArgumentNullException(
                    nameof(getPreviousFrameIndex));
            }
            if (lastFrameIndex < 0)
            {
                return new ProfilerRetainedFrameCount(
                    0,
                    true,
                    startFrameAnchorIndex < 0);
            }
            if (lastFrameIndex == startFrameAnchorIndex)
            {
                return new ProfilerRetainedFrameCount(
                    0,
                    true,
                    true);
            }

            var current = lastFrameIndex;
            var count = 0;
            while (current >= 0 &&
                   current != startFrameAnchorIndex &&
                   count < MaxFramesToCount)
            {
                count++;
                if (current == firstFrameIndex)
                {
                    return new ProfilerRetainedFrameCount(
                        count,
                        true,
                        startFrameAnchorIndex < 0);
                }

                var previous = getPreviousFrameIndex(current);
                if (previous == current)
                {
                    return new ProfilerRetainedFrameCount(
                        count,
                        false,
                        false);
                }
                current = previous;
            }

            if (current == startFrameAnchorIndex)
            {
                return new ProfilerRetainedFrameCount(
                    count,
                    true,
                    true);
            }
            if (current < 0)
            {
                return new ProfilerRetainedFrameCount(
                    count,
                    true,
                    startFrameAnchorIndex < 0);
            }
            return new ProfilerRetainedFrameCount(
                count,
                false,
                false);
        }
    }

    internal readonly struct ProfilerRetainedFrameCount
    {
        internal ProfilerRetainedFrameCount(
            int frameCount,
            bool countExact,
            bool startAnchorFound)
        {
            FrameCount = frameCount;
            CountExact = countExact;
            StartAnchorFound = startAnchorFound;
        }

        internal int FrameCount { get; }
        internal bool CountExact { get; }
        internal bool StartAnchorFound { get; }
    }

    internal static class ProfilerCaptureSupport
    {
        private const string DirectoryName = "profiler";

        internal static string BuildCapturePath(string captureId)
        {
            return Path.GetFullPath(
                Path.Combine(
                    BridgeSettings.RootDir,
                    DirectoryName,
                    $"{captureId}.data"));
        }

        internal static string BuildRelativePath(string captureId)
        {
            return $"{DirectoryName}/{captureId}.data";
        }

        internal static ProfilerCaptureResult CreateResult(
            ProfilerCaptureState state,
            string action,
            string stopReason,
            bool recording,
            bool profileEditor)
        {
            return new ProfilerCaptureResult
            {
                Action = action,
                CaptureId = state.CaptureId,
                Requested = state.RequestedFrameCount,
                // recorded 保留原响应字段；终态表示真正可用的 retained，
                // 活动态尚未停止测量时回退为 observed。
                Recorded = state.RetainedFrameCountMeasured
                    ? state.RetainedFrameCount
                    : state.RecordedFrameCount,
                Observed = state.RecordedFrameCount,
                Retained = state.RetainedFrameCountMeasured
                    ? (int?)state.RetainedFrameCount
                    : null,
                RetainedCountExact =
                    state.RetainedFrameCountMeasured &&
                    state.RetainedFrameCountExact,
                RetainedStartAnchorFound =
                    state.RetainedFrameCountMeasured
                        ? (bool?)state.RetainedStartAnchorFound
                        : null,
                Complete =
                    !state.CaptureChanged &&
                    state.RetainedFrameCountMeasured &&
                    state.RetainedFrameCountExact &&
                    state.RetainedFrameCount >=
                    state.RequestedFrameCount,
                StopReason = stopReason,
                Path = state.Path,
                RelativePath = state.RelativePath,
                Saved = state.Saved,
                Recording = recording,
                Managed = state.Active,
                ProfileEditor = profileEditor,
                OriginalProfileEditor = state.OriginalProfileEditor,
                ProfileEditorRestored = state.ProfileEditorRestored,
                FrameHistoryGap = state.FrameHistoryGap,
                CaptureChanged = state.CaptureChanged,
                StartedAt = state.StartedAt,
                StoppedAt = state.StoppedAt
            };
        }

        internal static ProfilerCaptureResult CreateEmptyStatus(
            bool recording,
            bool profileEditor)
        {
            return new ProfilerCaptureResult
            {
                Action = "status",
                Requested = 0,
                Recorded = 0,
                Observed = 0,
                Retained = null,
                RetainedCountExact = false,
                RetainedStartAnchorFound = null,
                Complete = false,
                StopReason = "notStarted",
                Recording = recording,
                Managed = false,
                ProfileEditor = profileEditor
            };
        }
    }

    internal static class ProfilerCaptureStore
    {
        internal const string ActiveStateKey =
            "AgentBridge.ProfilerCapture.Active";
        internal const string LastStateKey =
            "AgentBridge.ProfilerCapture.Last";

        internal static ProfilerCaptureState ReadActive()
        {
            var state = Read(ActiveStateKey);
            return state != null && state.Active ? state : null;
        }

        internal static ProfilerCaptureState ReadLast()
        {
            var state = Read(LastStateKey);
            return state != null && !state.Active ? state : null;
        }

        internal static void WriteActive(ProfilerCaptureState state)
        {
            if (state == null || !state.Active ||
                !IsCaptureId(state.CaptureId))
            {
                throw new InvalidOperationException(
                    "活动 Profiler capture 状态无效");
            }
            SessionState.SetString(
                ActiveStateKey,
                JsonConvert.SerializeObject(state));
        }

        internal static void Complete(ProfilerCaptureState state)
        {
            if (state == null || state.Active ||
                !IsCaptureId(state.CaptureId))
            {
                throw new InvalidOperationException(
                    "终止 Profiler capture 状态无效");
            }

            SessionState.SetString(
                LastStateKey,
                JsonConvert.SerializeObject(state));
            SessionState.EraseString(ActiveStateKey);
        }

        internal static void EraseActive()
        {
            SessionState.EraseString(ActiveStateKey);
        }

        private static ProfilerCaptureState Read(string key)
        {
            var json = SessionState.GetString(key, "");
            if (string.IsNullOrEmpty(json))
            {
                return null;
            }
            try
            {
                var state =
                    JsonConvert.DeserializeObject<ProfilerCaptureState>(json);
                return state != null && IsCaptureId(state.CaptureId)
                    ? state
                    : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool IsCaptureId(string captureId)
        {
            return Guid.TryParseExact(captureId, "N", out _);
        }
    }

    internal sealed class ProfilerCaptureState
    {
        [JsonProperty("captureId")]
        public string CaptureId { get; set; }

        [JsonProperty("active")]
        public bool Active { get; set; }

        [JsonProperty("requestedFrameCount")]
        public int RequestedFrameCount { get; set; }

        [JsonProperty("recordedFrameCount")]
        public int RecordedFrameCount { get; set; }

        [JsonProperty("profilerSessionGuid")]
        public string ProfilerSessionGuid { get; set; }

        [JsonProperty("captureChanged")]
        public bool CaptureChanged { get; set; }

        [JsonProperty("startFrameAnchorIndex")]
        public int StartFrameAnchorIndex { get; set; }

        [JsonProperty("retainedFrameCount")]
        public int RetainedFrameCount { get; set; }

        [JsonProperty("retainedFrameCountMeasured")]
        public bool RetainedFrameCountMeasured { get; set; }

        [JsonProperty("retainedFrameCountExact")]
        public bool RetainedFrameCountExact { get; set; }

        [JsonProperty("retainedStartAnchorFound")]
        public bool RetainedStartAnchorFound { get; set; }

        [JsonProperty("timeoutMs")]
        public int TimeoutMs { get; set; }

        [JsonProperty("pollIntervalMs")]
        public int PollIntervalMs { get; set; }

        [JsonProperty("clearExisting")]
        public bool ClearExisting { get; set; }

        [JsonProperty("saveRequested")]
        public bool SaveRequested { get; set; }

        [JsonProperty("originalProfileEditor")]
        public bool OriginalProfileEditor { get; set; }

        [JsonProperty("requestedProfileEditor")]
        public bool? RequestedProfileEditor { get; set; }

        [JsonProperty("lastObservedFrameIndex")]
        public int LastObservedFrameIndex { get; set; }

        [JsonProperty("frameHistoryGap")]
        public bool FrameHistoryGap { get; set; }

        [JsonProperty("saved")]
        public bool Saved { get; set; }

        [JsonProperty("path")]
        public string Path { get; set; }

        [JsonProperty("relativePath")]
        public string RelativePath { get; set; }

        [JsonProperty("startedAt")]
        public string StartedAt { get; set; }

        [JsonProperty("stoppedAt")]
        public string StoppedAt { get; set; }

        [JsonProperty("stopReason")]
        public string StopReason { get; set; }

        [JsonProperty("profileEditorRestored")]
        public bool ProfileEditorRestored { get; set; }
    }

    internal sealed class ProfilerCaptureResult
    {
        [JsonProperty("action")]
        public string Action { get; set; }

        [JsonProperty("captureId")]
        public string CaptureId { get; set; }

        [JsonProperty("requested")]
        public int Requested { get; set; }

        [JsonProperty("recorded")]
        public int Recorded { get; set; }

        [JsonProperty("observed")]
        public int Observed { get; set; }

        [JsonProperty("retained")]
        public int? Retained { get; set; }

        [JsonProperty("retainedCountExact")]
        public bool RetainedCountExact { get; set; }

        [JsonProperty("retainedStartAnchorFound")]
        public bool? RetainedStartAnchorFound { get; set; }

        [JsonProperty("complete")]
        public bool Complete { get; set; }

        [JsonProperty("stopReason")]
        public string StopReason { get; set; }

        [JsonProperty("path")]
        public string Path { get; set; }

        [JsonProperty("relativePath")]
        public string RelativePath { get; set; }

        [JsonProperty("saved")]
        public bool Saved { get; set; }

        [JsonProperty("recording")]
        public bool Recording { get; set; }

        [JsonProperty("managed")]
        public bool Managed { get; set; }

        [JsonProperty("profileEditor")]
        public bool ProfileEditor { get; set; }

        [JsonProperty("originalProfileEditor")]
        public bool OriginalProfileEditor { get; set; }

        [JsonProperty("profileEditorRestored")]
        public bool ProfileEditorRestored { get; set; }

        [JsonProperty("frameHistoryGap")]
        public bool FrameHistoryGap { get; set; }

        [JsonProperty("captureChanged")]
        public bool CaptureChanged { get; set; }

        [JsonProperty("startedAt")]
        public string StartedAt { get; set; }

        [JsonProperty("stoppedAt")]
        public string StoppedAt { get; set; }
    }
}
