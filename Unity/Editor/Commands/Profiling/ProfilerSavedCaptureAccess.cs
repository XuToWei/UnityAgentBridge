using System;
using System.IO;
using UnityEditorInternal;

namespace AgentBridge
{
    /// <summary>
    /// 在命令作用域内加载由 capture_profiler 保存的不可变 .data，并在结束时恢复
    /// 用户原有的 Profiler 缓冲。Profiler API 必须始终在 Unity 主线程调用。
    /// </summary>
    internal static class ProfilerSavedCaptureAccess
    {
        internal static T Read<T>(
            string captureId,
            Func<T> readCurrentCapture,
            bool requireImmutable,
            out bool immutable)
        {
            immutable = false;
            if (readCurrentCapture == null)
            {
                throw new ArgumentNullException(nameof(readCurrentCapture));
            }
            if (string.IsNullOrEmpty(captureId))
            {
                if (requireImmutable)
                {
                    throw new CommandException(
                        ErrorCodes.InvalidParams,
                        "此操作要求显式提供已保存的 captureId");
                }
                return readCurrentCapture();
            }
            var target = ResolveSavedCapture(captureId);
            var result = WithOriginalBufferRestored(
                target.Path,
                () =>
                {
                    Load(target);
                    return readCurrentCapture();
                });
            immutable = true;
            return result;
        }

        /// <summary>
        /// 在一个恢复点内读取两个窗口。两侧都是已保存 capture 时只备份/恢复当前
        /// Profiler 缓冲一次；captureId 相同还只加载一次。
        /// </summary>
        internal static ProfilerSavedCapturePair<T> ReadPair<T>(
            string firstCaptureId,
            Func<T> readFirst,
            string secondCaptureId,
            Func<T> readSecond)
        {
            if (readFirst == null)
            {
                throw new ArgumentNullException(nameof(readFirst));
            }
            if (readSecond == null)
            {
                throw new ArgumentNullException(nameof(readSecond));
            }

            var firstTarget = string.IsNullOrEmpty(firstCaptureId)
                ? null
                : ResolveSavedCapture(firstCaptureId);
            var secondTarget = string.IsNullOrEmpty(secondCaptureId)
                ? null
                : ResolveSavedCapture(secondCaptureId);
            if (firstTarget == null && secondTarget == null)
            {
                return new ProfilerSavedCapturePair<T>
                {
                    First = readFirst(),
                    Second = readSecond(),
                    FirstImmutable = false,
                    SecondImmutable = false
                };
            }

            var restoreDirectoryHint =
                firstTarget?.Path ?? secondTarget.Path;
            return WithOriginalBufferRestored(
                restoreDirectoryHint,
                () =>
                {
                    // 当前缓冲窗口必须在首次 LoadProfile 之前读取。
                    var pair = new ProfilerSavedCapturePair<T>
                    {
                        FirstImmutable = firstTarget != null,
                        SecondImmutable = secondTarget != null
                    };
                    if (firstTarget == null)
                    {
                        pair.First = readFirst();
                    }
                    if (secondTarget == null)
                    {
                        pair.Second = readSecond();
                    }

                    string loadedCaptureId = null;
                    if (firstTarget != null)
                    {
                        Load(firstTarget);
                        loadedCaptureId = firstTarget.Id;
                        pair.First = readFirst();
                    }
                    if (secondTarget != null)
                    {
                        if (!string.Equals(
                                loadedCaptureId,
                                secondTarget.Id,
                                StringComparison.Ordinal))
                        {
                            Load(secondTarget);
                        }
                        pair.Second = readSecond();
                    }
                    return pair;
                });
        }

        private static SavedCaptureTarget ResolveSavedCapture(string captureId)
        {
            if (!Guid.TryParseExact(captureId, "N", out var parsedCaptureId))
            {
                throw new CommandException(
                    ErrorCodes.InvalidParams,
                    "captureId 必须是 capture_profiler 返回的 32 位十六进制 ID");
            }

            // capture_profiler 始终用 Guid "N" 小写形式命名文件。先规范化，避免
            // 大写 ID 在大小写敏感文件系统上通过 schema 却找不到同一快照。
            var canonicalId = parsedCaptureId.ToString("N");
            var path = ProfilerCaptureSupport.BuildCapturePath(canonicalId);
            if (!File.Exists(path))
            {
                throw new CommandException(
                    ProfilerErrorCodes.CaptureNotFound,
                    $"找不到 captureId={canonicalId}；预期文件 '{path}'");
            }
            return new SavedCaptureTarget(canonicalId, path);
        }

        private static void Load(SavedCaptureTarget target)
        {
            // 即使当前 buffer 的 managed id 相同，也必须从磁盘加载：同一 session
            // 可能在保存后继续追加帧，而 captureId 的契约是不可变快照。
            if (!ProfilerDriver.LoadProfile(target.Path, false))
            {
                throw new CommandException(
                    ProfilerErrorCodes.CaptureLoadFailed,
                    $"Unity 无法加载 captureId={target.Id}");
            }
            ProfilerCaptureIdentity.AssignManagedCaptureId(target.Id);
        }

        private static T WithOriginalBufferRestored<T>(
            string restoreDirectoryHint,
            Func<T> operation)
        {
            if (ProfilerDriver.enabled)
            {
                throw new CommandException(
                    ProfilerErrorCodes.RecordingActive,
                    "Profiler 正在录制；为避免丢失当前缓冲，不能临时加载已保存 capture");
            }
            if (ProfilerCaptureStore.ReadActive() != null)
            {
                throw new CommandException(
                    ProfilerErrorCodes.RecordingActive,
                    "capture_profiler 存在尚未完成的录制；请先 action=stop");
            }

            var firstFrameIndex = ProfilerDriver.firstFrameIndex;
            var lastFrameIndex = ProfilerDriver.lastFrameIndex;
            var currentCaptureId =
                ProfilerCaptureIdentity.GetCurrentCaptureId(
                    ProfilerDriver.connectedProfiler,
                    firstFrameIndex,
                    lastFrameIndex);
            var hadOriginalFrames =
                firstFrameIndex >= 0 && lastFrameIndex >= 0;
            var restorePath = Path.Combine(
                Path.GetDirectoryName(restoreDirectoryHint) ??
                BridgeSettings.RootDir,
                $".restore-{Guid.NewGuid():N}.data");
            var loadAttempted = false;
            try
            {
                if (hadOriginalFrames)
                {
                    ProfilerDriver.SaveProfile(restorePath);
                    if (!File.Exists(restorePath))
                    {
                        throw new CommandException(
                            ProfilerErrorCodes.CaptureLoadFailed,
                            "无法为当前 Profiler 缓冲建立临时恢复点");
                    }
                }

                // operation 至少包含一次 LoadProfile。即使加载抛错，也要恢复，
                // 因为 Unity 可能已经部分替换当前 buffer。
                loadAttempted = true;
                return operation();
            }
            finally
            {
                var restoreSucceeded = !loadAttempted;
                try
                {
                    if (loadAttempted)
                    {
                        if (hadOriginalFrames)
                        {
                            bool restored;
                            try
                            {
                                restored = ProfilerDriver.LoadProfile(
                                    restorePath, false);
                            }
                            catch (Exception ex)
                            {
                                throw new CommandException(
                                    ProfilerErrorCodes.CaptureRestoreFailed,
                                    $"查询完成，但恢复原 Profiler 缓冲时失败；恢复点已保留在 '{restorePath}':{ex.Message}");
                            }
                            if (!restored)
                            {
                                throw new CommandException(
                                    ProfilerErrorCodes.CaptureRestoreFailed,
                                    $"查询完成，但 Unity 无法恢复原 Profiler 缓冲；恢复点已保留在 '{restorePath}'");
                            }
                            restoreSucceeded = true;
                            ProfilerCaptureIdentity.AssignManagedCaptureId(
                                currentCaptureId);
                        }
                        else
                        {
                            ProfilerDriver.ClearAllFrames();
                            restoreSucceeded = true;
                            ProfilerCaptureIdentity.ClearManagedCaptureId();
                        }
                    }
                }
                finally
                {
                    // 恢复失败时绝不能删除唯一恢复点。
                    if (restoreSucceeded)
                    {
                        AtomicFilePublisher.DeleteBestEffort(restorePath);
                    }
                }
            }
        }

        private sealed class SavedCaptureTarget
        {
            internal SavedCaptureTarget(string id, string path)
            {
                Id = id;
                Path = path;
            }

            internal string Id { get; }
            internal string Path { get; }
        }
    }

    internal sealed class ProfilerSavedCapturePair<T>
    {
        internal T First { get; set; }
        internal T Second { get; set; }
        internal bool FirstImmutable { get; set; }
        internal bool SecondImmutable { get; set; }
    }
}
