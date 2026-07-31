using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using UnityEditor.Profiling;
using UnityEditorInternal;

namespace AgentBridge
{
    /// <summary>
    /// Unity Profiler 原生视图的唯一读取边界。视图只在方法栈内 using，
    /// 对外仅暴露普通托管 DTO，避免跨 Exchange 持有 native 资源。
    /// </summary>
    internal static class ProfilerDataReader
    {
        internal const int MaxScannedSamples = 200000;
        internal const int AnalysisBudgetMs = 750;
        internal const int MaxThreadIndex = 1023;
        internal const int MaxReturnedThreads = 64;
        private const int SampleBudgetCheckInterval = 128;
        private const int ThreadBudgetCheckInterval = 32;

        internal static object Query(ProfilerQueryOptions options)
        {
            try
            {
                ValidateOptions(options);
                return options.ThreadSelector == null
                    ? (object)QueryCore(options)
                    : QueryMultipleCore(options);
            }
            catch (CommandException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw Unavailable(ex);
            }
        }

        internal static ProfilerDataResult QuerySingle(ProfilerQueryOptions options)
        {
            try
            {
                if (options != null)
                {
                    options.SingleThreadOperation = true;
                }
                ValidateOptions(options);
                if (options.ThreadSelector == null)
                {
                    return QueryCore(options);
                }

                var capture = ReadCaptureInfo();
                var endFrameIndex =
                    options.EndFrameIndex ?? capture.LastFrameIndexAtStart;
                if (endFrameIndex < 0 || !IsFrameValid(endFrameIndex))
                {
                    if (options.EndFrameIndex.HasValue)
                    {
                        throw FrameNotFound(endFrameIndex);
                    }
                    return ProfilerDataResult.CreateUnavailable(capture);
                }

                var sharedStopwatch = Stopwatch.StartNew();
                var resolved = ResolveThreadSelection(
                    endFrameIndex,
                    options.ThreadSelector,
                    sharedStopwatch,
                    options.FrameCount);
                if (resolved.Matched != 1 ||
                    resolved.Returned.Length != 1)
                {
                    throw new CommandException(
                        ProfilerErrorCodes.ThreadAmbiguous,
                        $"threadSelector 匹配 {resolved.Matched} 个线程；此操作要求唯一线程，请用 index/id 或 name+group 精确选择");
                }

                var selected = options.Clone();
                selected.EndFrameIndex = endFrameIndex;
                selected.ThreadSelector = null;
                selected.ThreadIndex = resolved.Returned[0].Index;
                selected.ExpectedThreadId = resolved.Returned[0].ThreadId;
                selected.PreResolvedThread = resolved.Returned[0];
                selected.SharedStopwatch = sharedStopwatch;
                return QueryCore(selected);
            }
            catch (CommandException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw Unavailable(ex);
            }
        }

        internal static void MarkCaptureSource(
            object result,
            string source,
            bool immutable)
        {
            ProfilerCaptureInfo capture = null;
            if (result is ProfilerDataResult single)
            {
                capture = single.Capture;
            }
            else if (result is ProfilerMultiThreadDataResult multiple)
            {
                capture = multiple.Capture;
            }
            if (capture == null)
            {
                return;
            }
            capture.Source = source;
            capture.Immutable = immutable;
        }

        private static ProfilerMultiThreadDataResult QueryMultipleCore(
            ProfilerQueryOptions options)
        {
            var capture = ReadCaptureInfo();
            var endFrameIndex =
                options.EndFrameIndex ?? capture.LastFrameIndexAtStart;
            if (endFrameIndex < 0 || !IsFrameValid(endFrameIndex))
            {
                if (options.EndFrameIndex.HasValue)
                {
                    throw FrameNotFound(endFrameIndex);
                }
                return ProfilerMultiThreadDataResult.CreateUnavailable(capture);
            }

            var sharedStopwatch = Stopwatch.StartNew();
            var selection = ResolveThreadSelection(
                endFrameIndex,
                options.ThreadSelector,
                sharedStopwatch,
                options.FrameCount);
            var results = new ProfilerDataResult[selection.Returned.Length];
            var scannedSamples = 0L;
            for (var index = 0; index < selection.Returned.Length; index++)
            {
                var query = options.Clone();
                query.EndFrameIndex = endFrameIndex;
                query.ThreadSelector = null;
                query.ThreadIndex = selection.Returned[index].Index;
                query.ExpectedThreadId = selection.Returned[index].ThreadId;
                // ResolveThreadSelection 已经验证了结束帧上的线程。首个查询仍读取
                // 完整 thread catalog 供顶层响应使用；后续线程避免重复枚举目录。
                if (index > 0)
                {
                    query.PreResolvedThread = selection.Returned[index];
                }
                var remainingSamples =
                    MaxScannedSamples - (int)scannedSamples;
                if (remainingSamples <= 0)
                {
                    throw new CommandException(
                        ProfilerErrorCodes.QueryTooLarge,
                        $"多线程 Profiler 查询已耗尽共享预算 {MaxScannedSamples} 个 hierarchy 节点；请减小 frameCount/maxDepth/线程数");
                }
                query.SampleLimit = remainingSamples;
                query.SharedStopwatch = sharedStopwatch;
                results[index] = QueryCore(query);
                scannedSamples += results[index].ScannedSamples;
            }

            var first = results[0];
            return new ProfilerMultiThreadDataResult
            {
                Available = true,
                Capture = first.Capture,
                Selection = first.Selection,
                Threads = first.Threads,
                ThreadsTruncated = first.ThreadsTruncated,
                ThreadSelection = selection.Info,
                FrameStats = first.FrameStats,
                Frames = first.Frames,
                ScannedSamples = (int)Math.Min(int.MaxValue, scannedSamples),
                ThreadResults = results.Select(item =>
                    new ProfilerThreadDataResult
                    {
                        Thread = item.Thread,
                        FramesWithThread = item.Selection.FramesWithThread,
                        FramesWithoutThread = item.Selection.FramesWithoutThread,
                        ScannedSamples = item.ScannedSamples,
                        UniqueHotspots = item.UniqueHotspots,
                        QueryMatchedHotspots = item.QueryMatchedHotspots,
                        CategoryMatchedHotspots = item.CategoryMatchedHotspots,
                        ThresholdMatchedHotspots = item.ThresholdMatchedHotspots,
                        MatchedHotspots = item.MatchedHotspots,
                        Returned = item.Returned,
                        Truncated = item.Truncated,
                        Hotspots = item.Hotspots
                    }).ToArray()
            };
        }

        private static ProfilerDataResult QueryCore(ProfilerQueryOptions options)
        {
            var ownsStopwatch = options.SharedStopwatch == null;
            var stopwatch =
                options.SharedStopwatch ?? Stopwatch.StartNew();
            var capture = ReadCaptureInfo();
            var endFrameIndex = options.EndFrameIndex ?? capture.LastFrameIndexAtStart;
            if (endFrameIndex < 0)
            {
                if (options.EndFrameIndex.HasValue)
                {
                    throw FrameNotFound(options.EndFrameIndex.Value);
                }
                return ProfilerDataResult.CreateUnavailable(capture);
            }

            if (!IsFrameValid(endFrameIndex))
            {
                if (options.EndFrameIndex.HasValue)
                {
                    throw FrameNotFound(endFrameIndex);
                }
                return ProfilerDataResult.CreateUnavailable(capture);
            }

            var sessionChecker = new ProfilerSessionChecker();
            var frameWindow = ProfilerFrameNavigator.Collect(
                endFrameIndex,
                capture.FirstFrameIndexAtStart,
                options.FrameCount,
                ProfilerDriver.GetPreviousFrameIndex,
                sessionChecker.IsAvailable ? sessionChecker.TryAreSameSession : null);
            frameWindow.SessionBoundaryChecked =
                frameWindow.SessionBoundaryChecked && sessionChecker.IsOperational;
            EnsureTimeBudget(stopwatch, options.FrameCount);

            var chronologicalIndices = frameWindow.IndicesNewestFirst
                .AsEnumerable()
                .Reverse()
                .ToArray();
            var frames = new ProfilerFrameSummary[chronologicalIndices.Length];
            for (var index = 0; index < chronologicalIndices.Length; index++)
            {
                frames[index] = ReadFrameSummary(chronologicalIndices[index]);
                if (index % 16 == 0)
                {
                    EnsureTimeBudget(stopwatch, options.FrameCount);
                }
            }
            var threadCatalog = options.PreResolvedThread == null
                ? ReadThreadCatalog(
                    endFrameIndex,
                    options.ThreadIndex,
                    stopwatch,
                    options.FrameCount)
                : new ThreadCatalog
                {
                    Selected = options.PreResolvedThread,
                    Returned = Array.Empty<ProfilerThreadInfo>(),
                    Truncated = false
                };
            var selectedThread = threadCatalog.Selected;
            if (options.ExpectedThreadId.HasValue &&
                selectedThread.ThreadId != options.ExpectedThreadId.Value)
            {
                throw new CommandException(
                    ProfilerErrorCodes.CaptureChanged,
                    $"Profiler 线程目录在查询期间已变化；期望 threadId={options.ExpectedThreadId.Value}，实际={selectedThread.ThreadId}");
            }

            var analyzer = new ProfilerHotspotAnalyzer(
                options.HotspotDetails != null);
            var scannedSamples = 0;
            var framesWithThread = 0;
            var preferredThreadIndex = options.ThreadIndex;
            // Profiler category id 在同一 session 内稳定；窗口不会跨 session。
            var categories = new Dictionary<ushort, string>();
            var children = new List<int>();
            var stack = new Stack<HierarchyNode>();
            // 从最新向旧帧读取；若预算失败，错误响应不会泄露看似完整的 partial Top N。
            foreach (var frameIndex in frameWindow.IndicesNewestFirst)
            {
                EnsureTimeBudget(stopwatch, options.FrameCount);
                if (!frameWindow.SessionBoundaryChecked)
                {
                    // 不能证明窗口属于同一 Profiler session 时，不跨帧复用 category id。
                    categories.Clear();
                }
                // 结束帧的 threadIndex 已由 ReadThreadCatalog 验证，无需再次打开 raw views。
                var actualThreadIndex = frameIndex == endFrameIndex
                    ? options.ThreadIndex
                    : FindThreadIndex(
                        frameIndex,
                        selectedThread.ThreadId,
                        preferredThreadIndex,
                        stopwatch,
                        options.FrameCount);
                if (actualThreadIndex < 0)
                {
                    continue;
                }
                preferredThreadIndex = actualThreadIndex;

                ReadFrameHotspots(
                    frameIndex,
                    actualThreadIndex,
                    options,
                    stopwatch,
                    ref scannedSamples,
                    analyzer,
                    categories,
                    children,
                    stack);
                framesWithThread++;
            }

            var frameTimeSumMs = 0.0;
            foreach (var frame in frames)
            {
                if (frame.FrameTimeMs.HasValue)
                {
                    frameTimeSumMs += frame.FrameTimeMs.Value;
                }
            }
            EnsureTimeBudget(stopwatch, options.FrameCount);
            var hotspotPage = analyzer.Select(
                frames.Length,
                frameTimeSumMs,
                // query 已在 native 数值列读取前下推；Analyzer 里仅保留匹配项。
                null,
                options.SortBy,
                options.Limit,
                _ => EnsureTimeBudget(stopwatch, options.FrameCount),
                null,
                options.MinSelfTimeSumMs,
                options.MinGcAllocSumBytes,
                options.MinCallCount,
                options.HotspotDetails,
                // 趋势与连续帧以完整窗口为基准；线程缺席或 marker 缺席都记为 0，
                // 避免跨缺口误报为“连续”。
                chronologicalIndices);
            var frameStats = ProfilerFrameStatistics.Create(frames);
            EnsureTimeBudget(stopwatch, options.FrameCount);
            if (ownsStopwatch)
            {
                stopwatch.Stop();
            }

            var viewMode = options.IncludeEditorOnly
                ? "mergedHierarchy"
                : "mergedHierarchyWithoutEditorOnly";
            return new ProfilerDataResult
            {
                Available = true,
                Capture = capture,
                Selection = new ProfilerSelectionInfo
                {
                    RequestedFrameCount = options.FrameCount,
                    FrameCount = frames.Length,
                    FirstFrameIndex = chronologicalIndices[0],
                    EndFrameIndex = endFrameIndex,
                    Complete = frames.Length == options.FrameCount,
                    HasMoreOlderFrames = frameWindow.HasMoreOlderFrames,
                    StopReason = frameWindow.StopReason,
                    SessionBoundaryChecked = frameWindow.SessionBoundaryChecked,
                    ViewMode = viewMode,
                    MaxDepth = options.MaxDepth,
                    FramesWithThread = framesWithThread,
                    FramesWithoutThread = frames.Length - framesWithThread
                },
                Threads = threadCatalog.Returned,
                ThreadsTruncated = threadCatalog.Truncated,
                Thread = selectedThread,
                FrameStats = frameStats,
                Frames = frames,
                ScannedSamples = scannedSamples,
                UniqueHotspots = hotspotPage.UniqueCount,
                QueryMatchedHotspots = hotspotPage.QueryMatchedCount,
                CategoryMatchedHotspots = hotspotPage.CategoryMatchedCount,
                ThresholdMatchedHotspots = hotspotPage.ThresholdMatchedCount,
                MatchedHotspots = hotspotPage.MatchedCount,
                Returned = hotspotPage.Hotspots.Length,
                Truncated = hotspotPage.MatchedCount > hotspotPage.Hotspots.Length,
                Hotspots = hotspotPage.Hotspots
            };
        }

        private static ProfilerCaptureInfo ReadCaptureInfo()
        {
            var capture = new ProfilerCaptureInfo
            {
                Recording = ProfilerDriver.enabled,
                ProfileEditor = ProfilerDriver.profileEditor,
                DeepProfiling = ProfilerDriver.deepProfiling,
                ConnectedProfiler = ProfilerDriver.connectedProfiler,
                FirstFrameIndexAtStart = ProfilerDriver.firstFrameIndex,
                LastFrameIndexAtStart = ProfilerDriver.lastFrameIndex
            };
            capture.CaptureId = ProfilerCaptureIdentity.GetCurrentCaptureId(
                capture.ConnectedProfiler,
                capture.FirstFrameIndexAtStart,
                capture.LastFrameIndexAtStart);
            capture.Source = "current";
            capture.Immutable = false;
            return capture;
        }

        private static bool IsFrameValid(int frameIndex)
        {
            if (frameIndex < 0)
            {
                return false;
            }

            using (var frame = ProfilerDriver.GetRawFrameDataView(frameIndex, 0))
            {
                return frame != null && frame.valid;
            }
        }

        private static ProfilerFrameSummary ReadFrameSummary(int frameIndex)
        {
            using (var frame = ProfilerDriver.GetRawFrameDataView(frameIndex, 0))
            {
                if (frame == null || !frame.valid)
                {
                    throw new CommandException(
                        ProfilerErrorCodes.CaptureChanged,
                        $"Profiler frame {frameIndex} 在查询期间已被淘汰或失效，请重试");
                }

                return new ProfilerFrameSummary
                {
                    FrameIndex = frameIndex,
                    FrameTimeMs = ProfilerDataValue.NullableNonNegative(frame.frameTimeMs),
                    FrameGpuTimeMs = ProfilerDataValue.NullablePositive(frame.frameGpuTimeMs),
                    Fps = ProfilerDataValue.NullablePositive(frame.frameFps)
                };
            }
        }

        private static ThreadCatalog ReadThreadCatalog(
            int frameIndex,
            int requestedThreadIndex,
            Stopwatch stopwatch,
            int requestedFrameCount)
        {
            var returned = new List<ProfilerThreadInfo>();
            ProfilerThreadInfo selected = null;
            var threadCount = 0;
            var truncated = false;
            for (var threadIndex = 0; threadIndex <= MaxThreadIndex; threadIndex++)
            {
                if ((threadIndex & (ThreadBudgetCheckInterval - 1)) == 0)
                {
                    EnsureTimeBudget(stopwatch, requestedFrameCount);
                }

                using (var frame = ProfilerDriver.GetRawFrameDataView(frameIndex, threadIndex))
                {
                    if (frame == null || !frame.valid)
                    {
                        break;
                    }

                    threadCount++;
                    ProfilerThreadInfo thread = null;
                    if (returned.Count < MaxReturnedThreads)
                    {
                        thread = CreateThreadInfo(frame);
                        returned.Add(thread);
                    }
                    if (threadIndex == requestedThreadIndex)
                    {
                        thread = thread ?? CreateThreadInfo(frame);
                        selected = thread;
                    }

                    // selected 已找到且已确认至少存在第 65 个线程，继续枚举
                    // 既不改变返回 DTO，也不影响 truncated 语义。
                    if (selected != null && threadCount > MaxReturnedThreads)
                    {
                        truncated = true;
                        break;
                    }
                }
            }

            if (selected == null)
            {
                throw new CommandException(
                    ProfilerErrorCodes.ThreadNotFound,
                    $"Profiler frame {frameIndex} 没有 threadIndex={requestedThreadIndex}；可用线程数={threadCount}");
            }

            return new ThreadCatalog
            {
                Selected = selected,
                Returned = returned.ToArray(),
                Truncated = truncated
            };
        }

        private static ResolvedThreadSelection ResolveThreadSelection(
            int frameIndex,
            ProfilerThreadSelector selector,
            Stopwatch stopwatch,
            int requestedFrameCount)
        {
            var returned = new List<ProfilerThreadInfo>();
            var matched = 0;
            for (var threadIndex = 0; threadIndex <= MaxThreadIndex; threadIndex++)
            {
                if ((threadIndex & (ThreadBudgetCheckInterval - 1)) == 0)
                {
                    EnsureTimeBudget(stopwatch, requestedFrameCount);
                }

                using (var frame =
                       ProfilerDriver.GetRawFrameDataView(frameIndex, threadIndex))
                {
                    if (frame == null || !frame.valid)
                    {
                        break;
                    }
                    if (!MatchesThread(frame, selector))
                    {
                        continue;
                    }

                    if (matched >= selector.Offset &&
                        returned.Count < selector.MaxThreads)
                    {
                        returned.Add(CreateThreadInfo(frame));
                    }
                    matched++;
                }
            }

            if (matched == 0)
            {
                throw new CommandException(
                    ProfilerErrorCodes.ThreadNotFound,
                    $"Profiler frame {frameIndex} 没有匹配 threadSelector(mode={selector.Mode}) 的线程");
            }
            if (returned.Count == 0)
            {
                throw new CommandException(
                    ProfilerErrorCodes.ThreadNotFound,
                    $"threadSelector offset={selector.Offset} 超出匹配线程数 {matched}");
            }

            var nextOffset = selector.Offset + returned.Count;
            return new ResolvedThreadSelection
            {
                Matched = matched,
                Returned = returned.ToArray(),
                Info = new ProfilerThreadSelectionInfo
                {
                    Mode = selector.Mode,
                    Matched = matched,
                    Offset = selector.Offset,
                    Returned = returned.Count,
                    Truncated = nextOffset < matched,
                    NextOffset = nextOffset < matched ? (int?)nextOffset : null
                }
            };
        }

        private static bool MatchesThread(
            FrameDataView frame,
            ProfilerThreadSelector selector)
        {
            switch (selector.Mode)
            {
                case "index":
                    return frame.threadIndex == selector.Index;
                case "id":
                    return frame.threadId == selector.Id;
                case "name":
                    return string.Equals(
                               frame.threadName ?? "",
                               selector.Name ?? "",
                               StringComparison.OrdinalIgnoreCase) &&
                           (selector.Group == null ||
                            string.Equals(
                                frame.threadGroupName ?? "",
                                selector.Group,
                                StringComparison.OrdinalIgnoreCase));
                case "group":
                    return string.Equals(
                        frame.threadGroupName ?? "",
                        selector.Group ?? "",
                        StringComparison.OrdinalIgnoreCase);
                case "all":
                    return true;
                default:
                    return false;
            }
        }

        private static ProfilerThreadInfo CreateThreadInfo(FrameDataView frame)
        {
            var name = ProfilerDataText.Truncate(
                frame.threadName, ProfilerDataText.MaxNameLength, out _);
            var group = ProfilerDataText.Truncate(
                frame.threadGroupName, ProfilerDataText.MaxCategoryLength, out _);
            return new ProfilerThreadInfo
            {
                Index = frame.threadIndex,
                ThreadId = frame.threadId,
                ThreadIdString = frame.threadId.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                Name = name,
                Group = group,
                SampleCount = frame.sampleCount
            };
        }

        private static int FindThreadIndex(
            int frameIndex,
            ulong targetThreadId,
            int preferredThreadIndex,
            Stopwatch stopwatch,
            int requestedFrameCount)
        {
            EnsureTimeBudget(stopwatch, requestedFrameCount);
            // thread 0 也是 frame 本身是否仍有效的探针。先读取它，避免
            // preferredThreadIndex==0 且 frame 被淘汰时被误判成“线程缺失”。
            using (var mainThread = ProfilerDriver.GetRawFrameDataView(frameIndex, 0))
            {
                if (mainThread == null || !mainThread.valid)
                {
                    throw new CommandException(
                        ProfilerErrorCodes.CaptureChanged,
                        $"Profiler frame {frameIndex} 在查询期间已被淘汰或失效，请重试");
                }
                if (mainThread.threadId == targetThreadId)
                {
                    return 0;
                }
            }

            if (preferredThreadIndex > 0)
            {
                using (var preferred =
                       ProfilerDriver.GetRawFrameDataView(frameIndex, preferredThreadIndex))
                {
                    if (preferred != null && preferred.valid &&
                        preferred.threadId == targetThreadId)
                    {
                        return preferredThreadIndex;
                    }
                }
            }

            for (var threadIndex = 1; threadIndex <= MaxThreadIndex; threadIndex++)
            {
                if ((threadIndex & (ThreadBudgetCheckInterval - 1)) == 0)
                {
                    EnsureTimeBudget(stopwatch, requestedFrameCount);
                }
                if (threadIndex == preferredThreadIndex)
                {
                    continue;
                }
                using (var frame = ProfilerDriver.GetRawFrameDataView(frameIndex, threadIndex))
                {
                    if (frame == null || !frame.valid)
                    {
                        break;
                    }
                    if (frame.threadId == targetThreadId)
                    {
                        return threadIndex;
                    }
                }
            }
            return -1;
        }

        private static void ReadFrameHotspots(
            int frameIndex,
            int threadIndex,
            ProfilerQueryOptions options,
            Stopwatch stopwatch,
            ref int scannedSamples,
            ProfilerHotspotAnalyzer analyzer,
            IDictionary<ushort, string> categories,
            List<int> children,
            Stack<HierarchyNode> stack)
        {
            var viewMode = HierarchyFrameDataView.ViewModes.MergeSamplesWithTheSameName;
            if (!options.IncludeEditorOnly)
            {
                viewMode |= HierarchyFrameDataView.ViewModes.HideEditorOnlySamples;
            }

            using (var frame = ProfilerDriver.GetHierarchyFrameDataView(
                       frameIndex,
                       threadIndex,
                       viewMode,
                       HierarchyFrameDataView.columnDontSort,
                       false))
            {
                EnsureTimeBudget(stopwatch, options.FrameCount);
                if (frame == null || !frame.valid)
                {
                    throw new CommandException(
                        ProfilerErrorCodes.CaptureChanged,
                        $"Profiler frame {frameIndex} 的线程视图在查询期间已失效，请重试");
                }

                var rootId = frame.GetRootItemID();
                if (rootId == HierarchyFrameDataView.invalidSampleId)
                {
                    return;
                }

                children.Clear();
                stack.Clear();
                frame.GetItemChildren(rootId, children);
                for (var index = children.Count - 1; index >= 0; index--)
                {
                    stack.Push(new HierarchyNode(children[index], 0));
                }

                while (stack.Count > 0)
                {
                    EnsureSampleCapacity(
                        scannedSamples, options.SampleLimit);
                    if ((scannedSamples & (SampleBudgetCheckInterval - 1)) == 0)
                    {
                        EnsureTimeBudget(stopwatch, options.FrameCount);
                    }
                    var node = stack.Pop();
                    scannedSamples++;

                    var name = frame.GetItemName(node.ItemId) ?? "";
                    var path = frame.GetItemPath(node.ItemId);
                    if (string.IsNullOrEmpty(path))
                    {
                        path = name;
                    }
                    var categoryId = frame.GetItemCategoryIndex(node.ItemId);
                    if (!categories.TryGetValue(categoryId, out var category))
                    {
                        category = ReadCategoryName(frame, categoryId);
                        categories.Add(categoryId, category);
                    }

                    if (!ProfilerHotspotAnalyzer.MatchesQuery(
                            name, path, options.Query))
                    {
                        analyzer.RecordUnmatched(name, path, category);
                    }
                    else if (!ProfilerHotspotAnalyzer.MatchesCategory(
                                 category, options.Categories))
                    {
                        analyzer.RecordCategoryRejected(name, path, category);
                    }
                    else
                    {
                        analyzer.AddSample(
                            frameIndex,
                            name,
                            path,
                            category,
                            frame.GetItemColumnDataAsDouble(
                                node.ItemId, HierarchyFrameDataView.columnTotalTime),
                            frame.GetItemColumnDataAsDouble(
                                node.ItemId, HierarchyFrameDataView.columnSelfTime),
                            ProfilerDataValue.ToNonNegativeInt64(
                                frame.GetItemColumnDataAsDouble(
                                    node.ItemId, HierarchyFrameDataView.columnCalls)),
                            ProfilerDataValue.ToNonNegativeInt64(
                                frame.GetItemColumnDataAsDouble(
                                    node.ItemId, HierarchyFrameDataView.columnGcMemory)),
                            ProfilerDataValue.ToNonNegativeInt64(
                                frame.GetItemColumnDataAsDouble(
                                    node.ItemId,
                                    HierarchyFrameDataView.columnWarningCount)));
                    }

                    if (node.Depth >= options.MaxDepth)
                    {
                        continue;
                    }

                    children.Clear();
                    frame.GetItemChildren(node.ItemId, children);
                    for (var index = children.Count - 1; index >= 0; index--)
                    {
                        stack.Push(new HierarchyNode(children[index], node.Depth + 1));
                    }
                }
                EnsureTimeBudget(stopwatch, options.FrameCount);
            }
        }

        private static string ReadCategoryName(FrameDataView frame, ushort categoryId)
        {
            try
            {
                return frame.GetCategoryInfo(categoryId).name ?? "";
            }
            catch (ArgumentException)
            {
                return "";
            }
        }

        private static void EnsureSampleCapacity(
            int scannedSamples,
            int sampleLimit)
        {
            if (scannedSamples >= sampleLimit)
            {
                throw new CommandException(
                    ProfilerErrorCodes.QueryTooLarge,
                    $"Profiler 查询超过本次共享预算 {sampleLimit} 个 hierarchy 节点；请减小 frameCount/maxDepth/线程数");
            }
        }

        private static void EnsureTimeBudget(
            Stopwatch stopwatch,
            int requestedFrameCount)
        {
            if (stopwatch.ElapsedMilliseconds > AnalysisBudgetMs)
            {
                throw new CommandException(
                    ProfilerErrorCodes.QueryTimeout,
                    $"Profiler 查询超过主线程预算 {AnalysisBudgetMs}ms(frameCount={requestedFrameCount})；请减小 frameCount/maxDepth 或使用 query 缩小指标聚合范围");
            }
        }

        internal static void ValidateOptions(ProfilerQueryOptions options)
        {
            if (options == null)
            {
                throw new CommandException(ErrorCodes.InvalidParams, "params 不可为空");
            }
            if (options.EndFrameIndex.HasValue && options.EndFrameIndex.Value < 0)
            {
                throw new CommandException(ErrorCodes.InvalidParams, "endFrameIndex 必须 >= 0");
            }
            if (options.FrameCount < 1 || options.FrameCount > 120)
            {
                throw new CommandException(ErrorCodes.InvalidParams, "frameCount 必须在 1..120");
            }
            if (options.SampleLimit < 1 ||
                options.SampleLimit > MaxScannedSamples)
            {
                throw new CommandException(
                    ErrorCodes.InvalidParams,
                    $"sample budget 必须在 1..{MaxScannedSamples}");
            }
            if (options.ThreadIndex < 0 || options.ThreadIndex > MaxThreadIndex)
            {
                throw new CommandException(ErrorCodes.InvalidParams, "threadIndex 必须在 0..1023");
            }
            ValidateThreadSelector(options);
            if (options.Query != null && options.Query.Length > 256)
            {
                throw new CommandException(ErrorCodes.InvalidParams, "query 最长 256 个字符");
            }
            if (options.Categories == null)
            {
                options.Categories = Array.Empty<string>();
            }
            if (options.Categories.Length > 16 ||
                options.Categories.Any(category =>
                    string.IsNullOrEmpty(category) || category.Length > 64))
            {
                throw new CommandException(
                    ErrorCodes.InvalidParams,
                    "categories 最多 16 项，每项长度必须在 1..64");
            }
            if (double.IsNaN(options.MinSelfTimeSumMs) ||
                double.IsInfinity(options.MinSelfTimeSumMs) ||
                options.MinSelfTimeSumMs < 0 ||
                options.MinGcAllocSumBytes < 0 ||
                options.MinCallCount < 0)
            {
                throw new CommandException(
                    ErrorCodes.InvalidParams,
                    "热点阈值必须是非负有限值");
            }
            if (options.SortBy != "selfTimeSumMs" &&
                options.SortBy != "totalTimeSumMs" &&
                options.SortBy != "maxSelfTimeMs" &&
                options.SortBy != "gcAllocSumBytes" &&
                !(options.AllowInternalLimit &&
                  (options.SortBy == "callCount" ||
                   options.SortBy == "selfTimeP95Ms")))
            {
                throw new CommandException(ErrorCodes.InvalidParams, "sortBy 不受支持");
            }
            if (options.MaxDepth < 0 || options.MaxDepth > 128)
            {
                throw new CommandException(ErrorCodes.InvalidParams, "maxDepth 必须在 0..128");
            }
            var maximumLimit = options.AllowInternalLimit
                ? ProfilerQueryOptions.MaxInternalLimit
                : ProfilerQueryOptions.MaxPublicLimit;
            if (options.Limit < 1 || options.Limit > maximumLimit)
            {
                throw new CommandException(
                    ErrorCodes.InvalidParams,
                    $"limit 必须在 1..{maximumLimit}");
            }
            var maximumSelectedThreads =
                options.ThreadSelector == null
                    ? 1
                    : options.ThreadSelector.Mode == "index" ||
                      options.ThreadSelector.Mode == "id"
                        ? 1
                        : options.ThreadSelector.MaxThreads;
            if (options.ThreadSelector != null &&
                !options.SingleThreadOperation &&
                (long)maximumSelectedThreads * options.Limit > 100)
            {
                throw new CommandException(
                    ErrorCodes.InvalidParams,
                    "多线程查询要求 threadSelector.maxThreads × limit <= 100");
            }
            ValidateHotspotDetails(
                options.HotspotDetails, options.AllowInternalLimit);
            if (options.ThreadSelector != null &&
                !options.SingleThreadOperation &&
                options.HotspotDetails != null)
            {
                var detailedHotspotsPerThread = Math.Min(
                    options.Limit,
                    options.HotspotDetails.HotspotLimit);
                var maximumTrendPoints =
                    (long)maximumSelectedThreads *
                    detailedHotspotsPerThread *
                    options.HotspotDetails.TrendFrameCount;
                if (maximumTrendPoints >
                    ProfilerHotspotDetailsOptions.MaxTrendPoints)
                {
                    throw new CommandException(
                        ErrorCodes.InvalidParams,
                        "多线程 hotspotDetails 要求 maxThreads × min(limit, hotspotLimit) × trendFrameCount <= 600");
                }
            }
        }

        private static void ValidateThreadSelector(ProfilerQueryOptions options)
        {
            var selector = options.ThreadSelector;
            if (selector == null)
            {
                return;
            }
            if (selector.Mode != "index" &&
                selector.Mode != "id" &&
                selector.Mode != "name" &&
                selector.Mode != "group" &&
                selector.Mode != "all")
            {
                throw new CommandException(
                    ErrorCodes.InvalidParams, "threadSelector.mode 不受支持");
            }
            if (selector.Offset < 0 ||
                selector.MaxThreads < 1 ||
                selector.MaxThreads > ProfilerThreadSelector.MaximumThreads)
            {
                throw new CommandException(
                    ErrorCodes.InvalidParams,
                    "threadSelector offset 必须 >= 0，maxThreads 必须在 1..16");
            }
            if (selector.Mode == "index" &&
                (selector.Index < 0 || selector.Index > MaxThreadIndex))
            {
                throw new CommandException(
                    ErrorCodes.InvalidParams,
                    "threadSelector.index 必须在 0..1023");
            }
            if (selector.Mode == "name" &&
                string.IsNullOrEmpty(selector.Name))
            {
                throw new CommandException(
                    ErrorCodes.InvalidParams,
                    "threadSelector.name 不可为空");
            }
            if (selector.Mode == "group" &&
                selector.Group == null)
            {
                throw new CommandException(
                    ErrorCodes.InvalidParams,
                    "threadSelector.group 不可缺省");
            }
        }

        private static void ValidateHotspotDetails(
            ProfilerHotspotDetailsOptions details,
            bool allowInternalLimit)
        {
            if (details == null)
            {
                return;
            }
            if (details.Metric != "selfTimeMs" &&
                details.Metric != "totalTimeMs" &&
                details.Metric != "gcAllocBytes" &&
                details.Metric != "calls")
            {
                throw new CommandException(
                    ErrorCodes.InvalidParams, "hotspotDetails.metric 不受支持");
            }
            if (details.SlowestLimit < 0 || details.SlowestLimit > 5 ||
                details.TrendFrameCount < 0 || details.TrendFrameCount > 120 ||
                details.HotspotLimit < 1 ||
                details.HotspotLimit >
                (allowInternalLimit
                    ? ProfilerQueryOptions.MaxInternalLimit
                    : 10) ||
                details.TrendFrameCount * details.HotspotLimit >
                ProfilerHotspotDetailsOptions.MaxTrendPoints)
            {
                throw new CommandException(
                    ErrorCodes.InvalidParams,
                    "hotspotDetails 超出范围或趋势点总数超过 600");
            }
        }

        private static CommandException FrameNotFound(int frameIndex)
        {
            return new CommandException(
                ProfilerErrorCodes.FrameNotFound,
                $"Profiler capture 中没有 frame {frameIndex}；该帧可能尚未记录或已被缓冲区淘汰");
        }

        private static CommandException Unavailable(Exception ex)
        {
            var detail = ex is TargetInvocationException && ex.InnerException != null
                ? ex.InnerException.Message
                : ex.Message;
            return new CommandException(
                ProfilerErrorCodes.Unavailable,
                $"Profiler 数据接口不可用:{ex.GetType().Name}:{detail}");
        }

        private sealed class ThreadCatalog
        {
            internal ProfilerThreadInfo Selected { get; set; }
            internal ProfilerThreadInfo[] Returned { get; set; }
            internal bool Truncated { get; set; }
        }

        private sealed class ResolvedThreadSelection
        {
            internal int Matched { get; set; }
            internal ProfilerThreadInfo[] Returned { get; set; }
            internal ProfilerThreadSelectionInfo Info { get; set; }
        }

        private readonly struct HierarchyNode
        {
            internal HierarchyNode(int itemId, int depth)
            {
                ItemId = itemId;
                Depth = depth;
            }

            internal int ItemId { get; }
            internal int Depth { get; }
        }
    }

    /// <summary>可注入委托的纯帧导航器，确保不把 Profiler frame index 当连续整数。</summary>
    internal static class ProfilerFrameNavigator
    {
        internal static ProfilerFrameWindow Collect(
            int endFrameIndex,
            int firstFrameIndex,
            int requestedCount,
            Func<int, int> getPreviousFrameIndex,
            Func<int, int, bool?> areSameSession)
        {
            if (getPreviousFrameIndex == null)
            {
                throw new ArgumentNullException(nameof(getPreviousFrameIndex));
            }

            var result = new ProfilerFrameWindow
            {
                SessionBoundaryChecked = areSameSession != null
            };
            result.IndicesNewestFirst.Add(endFrameIndex);
            var current = endFrameIndex;
            while (result.IndicesNewestFirst.Count < requestedCount)
            {
                if (current == firstFrameIndex)
                {
                    result.StopReason = "captureStart";
                    return result;
                }

                var previous = getPreviousFrameIndex(current);
                if (previous < 0 || previous == current)
                {
                    result.StopReason = "captureStart";
                    return result;
                }

                if (areSameSession != null)
                {
                    var sameSession = areSameSession(current, previous);
                    if (!sameSession.HasValue)
                    {
                        result.SessionBoundaryChecked = false;
                    }
                    else if (!sameSession.Value)
                    {
                        result.StopReason = "sessionBoundary";
                        return result;
                    }
                }

                result.IndicesNewestFirst.Add(previous);
                current = previous;
            }

            result.StopReason = "requestedCount";
            if (current != firstFrameIndex)
            {
                var previous = getPreviousFrameIndex(current);
                if (previous >= 0 && previous != current)
                {
                    if (areSameSession == null)
                    {
                        result.HasMoreOlderFrames = true;
                    }
                    else
                    {
                        var sameSession = areSameSession(current, previous);
                        if (!sameSession.HasValue)
                        {
                            result.SessionBoundaryChecked = false;
                            result.HasMoreOlderFrames = true;
                        }
                        else
                        {
                            result.HasMoreOlderFrames = sameSession.Value;
                        }
                    }
                }
            }
            return result;
        }
    }

    internal sealed class ProfilerFrameWindow
    {
        internal List<int> IndicesNewestFirst { get; } = new List<int>();
        internal bool HasMoreOlderFrames { get; set; }
        internal bool SessionBoundaryChecked { get; set; }
        internal string StopReason { get; set; } = "requestedCount";
    }

    internal sealed class ProfilerSessionChecker
    {
        private readonly MethodInfo m_Method;
        private bool m_Failed;

        internal ProfilerSessionChecker()
        {
            var signature = new[] { typeof(int), typeof(int) };
            m_Method =
                typeof(ProfilerDriver).GetMethod(
                    "GetFramesBelongToSameProfilerSession",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    signature,
                    null) ??
                typeof(ProfilerDriver).GetMethod(
                    "GetFramesBelongToSameSession",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    signature,
                    null);
        }

        internal bool IsAvailable => m_Method != null;
        internal bool IsOperational => m_Method != null && !m_Failed;

        internal bool? TryAreSameSession(int leftFrameIndex, int rightFrameIndex)
        {
            if (m_Method == null || m_Failed)
            {
                return null;
            }
            try
            {
                return (bool)m_Method.Invoke(null, new object[] { leftFrameIndex, rightFrameIndex });
            }
            catch (Exception)
            {
                m_Failed = true;
                return null;
            }
        }
    }

    internal static class ProfilerFrameStatistics
    {
        internal static ProfilerFrameStats Create(IReadOnlyList<ProfilerFrameSummary> frames)
        {
            var values = frames
                .Where(frame => frame.FrameTimeMs.HasValue)
                .Select(frame => new FrameValue(frame.FrameIndex, frame.FrameTimeMs.Value))
                .OrderBy(item => item.Value)
                .ToArray();
            if (values.Length == 0)
            {
                return new ProfilerFrameStats();
            }

            var maximum = values[values.Length - 1];
            return new ProfilerFrameStats
            {
                MeanFrameTimeMs = ProfilerDataValue.Round(values.Average(item => item.Value)),
                P50FrameTimeMs = ProfilerDataValue.Round(Percentile(values, 0.50)),
                P95FrameTimeMs = ProfilerDataValue.Round(Percentile(values, 0.95)),
                P99FrameTimeMs = ProfilerDataValue.Round(Percentile(values, 0.99)),
                MaxFrameTimeMs = ProfilerDataValue.Round(maximum.Value),
                MaxFrameIndex = maximum.FrameIndex
            };
        }

        private static double Percentile(FrameValue[] sorted, double percentile)
        {
            var rank = Math.Max(1, (int)Math.Ceiling(percentile * sorted.Length));
            return sorted[rank - 1].Value;
        }

        private readonly struct FrameValue
        {
            internal FrameValue(int frameIndex, double value)
            {
                FrameIndex = frameIndex;
                Value = value;
            }

            internal int FrameIndex { get; }
            internal double Value { get; }
        }
    }

    internal sealed class ProfilerDataResult
    {
        [JsonProperty("available")] public bool Available { get; set; }
        [JsonProperty("capture")] public ProfilerCaptureInfo Capture { get; set; }
        [JsonProperty("selection")] public ProfilerSelectionInfo Selection { get; set; }
        [JsonProperty("threads")] public ProfilerThreadInfo[] Threads { get; set; }
        [JsonProperty("threadsTruncated")] public bool ThreadsTruncated { get; set; }
        [JsonProperty("thread")] public ProfilerThreadInfo Thread { get; set; }
        [JsonProperty("frameStats")] public ProfilerFrameStats FrameStats { get; set; }
        [JsonProperty("frames")] public ProfilerFrameSummary[] Frames { get; set; }
        [JsonProperty("scannedSamples")] public int ScannedSamples { get; set; }
        [JsonProperty("uniqueHotspots")] public int UniqueHotspots { get; set; }
        [JsonProperty("queryMatchedHotspots")] public int QueryMatchedHotspots { get; set; }
        [JsonProperty("categoryMatchedHotspots")] public int CategoryMatchedHotspots { get; set; }
        [JsonProperty("thresholdMatchedHotspots")] public int ThresholdMatchedHotspots { get; set; }
        [JsonProperty("matchedHotspots")] public int MatchedHotspots { get; set; }
        [JsonProperty("returned")] public int Returned { get; set; }
        [JsonProperty("truncated")] public bool Truncated { get; set; }
        [JsonProperty("hotspots")] public ProfilerHotspotResult[] Hotspots { get; set; }

        internal static ProfilerDataResult CreateUnavailable(ProfilerCaptureInfo capture)
        {
            return new ProfilerDataResult
            {
                Available = false,
                Capture = capture,
                Threads = Array.Empty<ProfilerThreadInfo>(),
                Frames = Array.Empty<ProfilerFrameSummary>(),
                Hotspots = Array.Empty<ProfilerHotspotResult>()
            };
        }
    }

    internal sealed class ProfilerCaptureInfo
    {
        [JsonProperty("captureId")] public string CaptureId { get; set; }
        [JsonProperty("source")] public string Source { get; set; }
        [JsonProperty("immutable")] public bool Immutable { get; set; }
        [JsonProperty("recording")] public bool Recording { get; set; }
        [JsonProperty("profileEditor")] public bool ProfileEditor { get; set; }
        [JsonProperty("deepProfiling")] public bool DeepProfiling { get; set; }
        [JsonProperty("connectedProfiler")] public int ConnectedProfiler { get; set; }
        [JsonProperty("firstFrameIndexAtStart")] public int FirstFrameIndexAtStart { get; set; }
        [JsonProperty("lastFrameIndexAtStart")] public int LastFrameIndexAtStart { get; set; }
    }

    internal sealed class ProfilerSelectionInfo
    {
        [JsonProperty("requestedFrameCount")] public int RequestedFrameCount { get; set; }
        [JsonProperty("frameCount")] public int FrameCount { get; set; }
        [JsonProperty("firstFrameIndex")] public int FirstFrameIndex { get; set; }
        [JsonProperty("endFrameIndex")] public int EndFrameIndex { get; set; }
        [JsonProperty("complete")] public bool Complete { get; set; }
        [JsonProperty("hasMoreOlderFrames")] public bool HasMoreOlderFrames { get; set; }
        [JsonProperty("stopReason")] public string StopReason { get; set; }
        [JsonProperty("sessionBoundaryChecked")] public bool SessionBoundaryChecked { get; set; }
        [JsonProperty("viewMode")] public string ViewMode { get; set; }
        [JsonProperty("maxDepth")] public int MaxDepth { get; set; }
        [JsonProperty("framesWithThread")] public int FramesWithThread { get; set; }
        [JsonProperty("framesWithoutThread")] public int FramesWithoutThread { get; set; }
    }

    internal sealed class ProfilerThreadInfo
    {
        [JsonProperty("index")] public int Index { get; set; }
        [JsonProperty("id")] public ulong ThreadId { get; set; }
        [JsonProperty("idString")] public string ThreadIdString { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("group")] public string Group { get; set; }
        [JsonProperty("sampleCount")] public int SampleCount { get; set; }
    }

    internal sealed class ProfilerFrameSummary
    {
        [JsonProperty("frameIndex")] public int FrameIndex { get; set; }
        [JsonProperty("frameTimeMs")] public double? FrameTimeMs { get; set; }
        [JsonProperty("frameGpuTimeMs")] public double? FrameGpuTimeMs { get; set; }
        [JsonProperty("fps")] public double? Fps { get; set; }
    }

    internal sealed class ProfilerFrameStats
    {
        [JsonProperty("meanFrameTimeMs")] public double? MeanFrameTimeMs { get; set; }
        [JsonProperty("p50FrameTimeMs")] public double? P50FrameTimeMs { get; set; }
        [JsonProperty("p95FrameTimeMs")] public double? P95FrameTimeMs { get; set; }
        [JsonProperty("p99FrameTimeMs")] public double? P99FrameTimeMs { get; set; }
        [JsonProperty("maxFrameTimeMs")] public double? MaxFrameTimeMs { get; set; }
        [JsonProperty("maxFrameIndex")] public int? MaxFrameIndex { get; set; }
    }

    internal sealed class ProfilerMultiThreadDataResult
    {
        [JsonProperty("available")] public bool Available { get; set; }
        [JsonProperty("capture")] public ProfilerCaptureInfo Capture { get; set; }
        [JsonProperty("selection")] public ProfilerSelectionInfo Selection { get; set; }
        [JsonProperty("threads")] public ProfilerThreadInfo[] Threads { get; set; }
        [JsonProperty("threadsTruncated")] public bool ThreadsTruncated { get; set; }
        [JsonProperty("threadSelection")]
        public ProfilerThreadSelectionInfo ThreadSelection { get; set; }
        [JsonProperty("frameStats")] public ProfilerFrameStats FrameStats { get; set; }
        [JsonProperty("frames")] public ProfilerFrameSummary[] Frames { get; set; }
        [JsonProperty("scannedSamples")] public int ScannedSamples { get; set; }
        [JsonProperty("threadResults")]
        public ProfilerThreadDataResult[] ThreadResults { get; set; }

        internal static ProfilerMultiThreadDataResult CreateUnavailable(
            ProfilerCaptureInfo capture)
        {
            return new ProfilerMultiThreadDataResult
            {
                Available = false,
                Capture = capture,
                Threads = Array.Empty<ProfilerThreadInfo>(),
                Frames = Array.Empty<ProfilerFrameSummary>(),
                ThreadResults = Array.Empty<ProfilerThreadDataResult>()
            };
        }
    }

    internal sealed class ProfilerThreadSelectionInfo
    {
        [JsonProperty("mode")] public string Mode { get; set; }
        [JsonProperty("matched")] public int Matched { get; set; }
        [JsonProperty("offset")] public int Offset { get; set; }
        [JsonProperty("returned")] public int Returned { get; set; }
        [JsonProperty("truncated")] public bool Truncated { get; set; }
        [JsonProperty("nextOffset")] public int? NextOffset { get; set; }
    }

    internal sealed class ProfilerThreadDataResult
    {
        [JsonProperty("thread")] public ProfilerThreadInfo Thread { get; set; }
        [JsonProperty("framesWithThread")] public int FramesWithThread { get; set; }
        [JsonProperty("framesWithoutThread")] public int FramesWithoutThread { get; set; }
        [JsonProperty("scannedSamples")] public int ScannedSamples { get; set; }
        [JsonProperty("uniqueHotspots")] public int UniqueHotspots { get; set; }
        [JsonProperty("queryMatchedHotspots")] public int QueryMatchedHotspots { get; set; }
        [JsonProperty("categoryMatchedHotspots")] public int CategoryMatchedHotspots { get; set; }
        [JsonProperty("thresholdMatchedHotspots")] public int ThresholdMatchedHotspots { get; set; }
        [JsonProperty("matchedHotspots")] public int MatchedHotspots { get; set; }
        [JsonProperty("returned")] public int Returned { get; set; }
        [JsonProperty("truncated")] public bool Truncated { get; set; }
        [JsonProperty("hotspots")] public ProfilerHotspotResult[] Hotspots { get; set; }
    }
}
