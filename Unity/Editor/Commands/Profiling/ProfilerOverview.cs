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
    internal sealed class ProfilerOverviewOptions
    {
        internal const int DefaultFrameCount = 120;
        internal const int MaxFrameCount = 2000;
        internal const int DefaultSlowestLimit = 10;
        internal const int DefaultSpikeLimit = 10;
        internal const int MaxRankedFrameLimit = 50;

        internal int? EndFrameIndex { get; set; }
        internal int FrameCount { get; set; } = DefaultFrameCount;
        internal int SlowestLimit { get; set; } = DefaultSlowestLimit;
        internal int SpikeLimit { get; set; } = DefaultSpikeLimit;
        internal bool IncludeFrames { get; set; }
    }

    /// <summary>
    /// Profiler 原生帧数据的轻量读取边界。只打开 thread 0 的 raw frame view，
    /// 不读取 hierarchy，也不跨方法持有 native 资源。
    /// </summary>
    internal static class ProfilerOverviewReader
    {
        private const int AnalysisBudgetMs = 750;
        private const int BudgetCheckInterval = 16;

        internal static ProfilerOverviewResult Query(ProfilerOverviewOptions options)
        {
            try
            {
                ValidateOptions(options);
                return QueryCore(options);
            }
            catch (CommandException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var detail = ex is TargetInvocationException && ex.InnerException != null
                    ? ex.InnerException.Message
                    : ex.Message;
                throw new CommandException(
                    ProfilerErrorCodes.Unavailable,
                    $"Profiler 概览接口不可用:{ex.GetType().Name}:{detail}");
            }
        }

        private static ProfilerOverviewResult QueryCore(ProfilerOverviewOptions options)
        {
            var stopwatch = Stopwatch.StartNew();
            var capture = ReadCaptureInfo();
            var endFrameIndex = options.EndFrameIndex ?? capture.LastFrameIndexAtStart;
            if (endFrameIndex < 0)
            {
                if (options.EndFrameIndex.HasValue)
                {
                    throw FrameNotFound(options.EndFrameIndex.Value);
                }
                return ProfilerOverviewResult.CreateUnavailable(capture);
            }

            if (!IsFrameValid(endFrameIndex))
            {
                if (options.EndFrameIndex.HasValue)
                {
                    throw FrameNotFound(endFrameIndex);
                }
                return ProfilerOverviewResult.CreateUnavailable(capture);
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
                if ((index & (BudgetCheckInterval - 1)) == 0)
                {
                    EnsureTimeBudget(stopwatch, options.FrameCount);
                }
            }

            var analysis = ProfilerOverviewAnalyzer.Analyze(
                frames,
                options.SlowestLimit,
                options.SpikeLimit);
            EnsureTimeBudget(stopwatch, options.FrameCount);
            stopwatch.Stop();

            return new ProfilerOverviewResult
            {
                Available = true,
                Capture = capture,
                Selection = new ProfilerOverviewSelection
                {
                    RequestedFrameCount = options.FrameCount,
                    FrameCount = frames.Length,
                    FirstFrameIndex = chronologicalIndices[0],
                    EndFrameIndex = endFrameIndex,
                    Complete = frames.Length == options.FrameCount,
                    HasMoreOlderFrames = frameWindow.HasMoreOlderFrames,
                    StopReason = frameWindow.StopReason,
                    SessionBoundaryChecked = frameWindow.SessionBoundaryChecked,
                    IncludeFrames = options.IncludeFrames
                },
                Stats = analysis.Stats,
                SlowestFrames = analysis.SlowestFrames,
                Spikes = analysis.Spikes,
                Frames = options.IncludeFrames ? frames : Array.Empty<ProfilerFrameSummary>()
            };
        }

        private static ProfilerOverviewCaptureInfo ReadCaptureInfo()
        {
            var capture = new ProfilerOverviewCaptureInfo
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
                        $"Profiler frame {frameIndex} 在概览查询期间已被淘汰或失效，请重试");
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

        private static void ValidateOptions(ProfilerOverviewOptions options)
        {
            if (options == null)
            {
                throw new CommandException(ErrorCodes.InvalidParams, "params 不能为空");
            }
            if (options.EndFrameIndex.HasValue && options.EndFrameIndex.Value < 0)
            {
                throw new CommandException(
                    ErrorCodes.InvalidParams,
                    "endFrameIndex 必须大于等于 0");
            }
            if (options.FrameCount < 1 ||
                options.FrameCount > ProfilerOverviewOptions.MaxFrameCount)
            {
                throw new CommandException(
                    ErrorCodes.InvalidParams,
                    $"frameCount 必须在 1..{ProfilerOverviewOptions.MaxFrameCount}");
            }
            if (options.SlowestLimit < 1 ||
                options.SlowestLimit > ProfilerOverviewOptions.MaxRankedFrameLimit)
            {
                throw new CommandException(
                    ErrorCodes.InvalidParams,
                    $"slowestLimit 必须在 1..{ProfilerOverviewOptions.MaxRankedFrameLimit}");
            }
            if (options.SpikeLimit < 1 ||
                options.SpikeLimit > ProfilerOverviewOptions.MaxRankedFrameLimit)
            {
                throw new CommandException(
                    ErrorCodes.InvalidParams,
                    $"spikeLimit 必须在 1..{ProfilerOverviewOptions.MaxRankedFrameLimit}");
            }
        }

        private static void EnsureTimeBudget(Stopwatch stopwatch, int requestedFrameCount)
        {
            if (stopwatch.ElapsedMilliseconds > AnalysisBudgetMs)
            {
                throw new CommandException(
                    ProfilerErrorCodes.QueryTimeout,
                    $"Profiler 概览超过主线程预算 {AnalysisBudgetMs}ms(frameCount={requestedFrameCount})；请减小 frameCount");
            }
        }

        private static CommandException FrameNotFound(int frameIndex)
        {
            return new CommandException(
                ProfilerErrorCodes.FrameNotFound,
                $"Profiler capture 中没有 frame {frameIndex}；该帧可能尚未记录或已被缓冲区淘汰");
        }
    }

    /// <summary>帧时间的纯托管统计、排序和 spike 识别。</summary>
    internal static class ProfilerOverviewAnalyzer
    {
        internal const string SpikeAlgorithm =
            "frameTimeMs > (n < 20 ? median + 3 * 1.4826 * MAD : max(p95, median + 3 * 1.4826 * MAD))";

        internal static ProfilerOverviewAnalysis Analyze(
            IReadOnlyList<ProfilerFrameSummary> frames,
            int slowestLimit,
            int spikeLimit)
        {
            if (frames == null)
            {
                throw new ArgumentNullException(nameof(frames));
            }
            if (slowestLimit < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(slowestLimit));
            }
            if (spikeLimit < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(spikeLimit));
            }

            var rankedFrames = frames
                .Where(frame => frame != null && frame.FrameTimeMs.HasValue)
                .OrderByDescending(frame => frame.FrameTimeMs.Value)
                .ThenByDescending(frame => frame.FrameIndex)
                .ToArray();
            var sortedValues = rankedFrames
                .Select(frame => frame.FrameTimeMs.Value)
                .OrderBy(value => value)
                .ToArray();

            if (sortedValues.Length == 0)
            {
                return new ProfilerOverviewAnalysis
                {
                    Stats = new ProfilerOverviewFrameStats
                    {
                        ValidFrameCount = 0,
                        MissingFrameTimeCount = frames.Count
                    },
                    SlowestFrames = Array.Empty<ProfilerFrameSummary>(),
                    Spikes = ProfilerSpikeSummary.CreateUnavailable()
                };
            }

            var p50 = Percentile(sortedValues, 0.50);
            var p95 = Percentile(sortedValues, 0.95);
            var p99 = Percentile(sortedValues, 0.99);
            var deviations = sortedValues
                .Select(value => Math.Abs(value - p50))
                .OrderBy(value => value)
                .ToArray();
            var mad = Percentile(deviations, 0.50);
            var robustThreshold = p50 + 3.0 * 1.4826 * mad;
            // Nearest-rank P95 等到至少 20 个样本才不会必然落在最大值；
            // 小窗口使用 robust threshold，避免单个明显 outlier 永远无法入选。
            var threshold = sortedValues.Length < 20
                ? robustThreshold
                : Math.Max(p95, robustThreshold);
            if (double.IsNaN(threshold) || double.IsInfinity(threshold))
            {
                threshold = double.MaxValue;
            }

            var spikeFrames = rankedFrames
                .Where(frame => frame.FrameTimeMs.Value > threshold)
                .ToArray();
            var returnedSpikeFrames = spikeFrames
                .Take(spikeLimit)
                .Select(frame => new ProfilerSpikeFrame
                {
                    FrameIndex = frame.FrameIndex,
                    FrameTimeMs = frame.FrameTimeMs.Value,
                    OverThresholdPercent = ProfilerDataValue.Percent(
                        frame.FrameTimeMs.Value - threshold,
                        threshold)
                })
                .ToArray();
            var maximum = rankedFrames[0];

            return new ProfilerOverviewAnalysis
            {
                Stats = new ProfilerOverviewFrameStats
                {
                    ValidFrameCount = sortedValues.Length,
                    MissingFrameTimeCount = frames.Count - sortedValues.Length,
                    MeanFrameTimeMs = ProfilerDataValue.Round(sortedValues.Average()),
                    P50FrameTimeMs = ProfilerDataValue.Round(p50),
                    P95FrameTimeMs = ProfilerDataValue.Round(p95),
                    P99FrameTimeMs = ProfilerDataValue.Round(p99),
                    MaxFrameTimeMs = ProfilerDataValue.Round(maximum.FrameTimeMs.Value),
                    MaxFrameIndex = maximum.FrameIndex
                },
                SlowestFrames = rankedFrames
                    .Take(slowestLimit)
                    .ToArray(),
                Spikes = new ProfilerSpikeSummary
                {
                    Algorithm = SpikeAlgorithm,
                    ThresholdMs = ProfilerDataValue.Round(threshold),
                    Matched = spikeFrames.Length,
                    Returned = returnedSpikeFrames.Length,
                    Frames = returnedSpikeFrames
                }
            };
        }

        private static double Percentile(double[] sortedValues, double percentile)
        {
            var rank = Math.Max(1, (int)Math.Ceiling(percentile * sortedValues.Length));
            return sortedValues[rank - 1];
        }
    }

    internal sealed class ProfilerOverviewAnalysis
    {
        internal ProfilerOverviewFrameStats Stats { get; set; }
        internal ProfilerFrameSummary[] SlowestFrames { get; set; }
        internal ProfilerSpikeSummary Spikes { get; set; }
    }

    internal sealed class ProfilerOverviewResult
    {
        [JsonProperty("available")] public bool Available { get; set; }
        [JsonProperty("capture")] public ProfilerOverviewCaptureInfo Capture { get; set; }
        [JsonProperty("selection")] public ProfilerOverviewSelection Selection { get; set; }
        [JsonProperty("stats")] public ProfilerOverviewFrameStats Stats { get; set; }
        [JsonProperty("slowestFrames")] public ProfilerFrameSummary[] SlowestFrames { get; set; }
        [JsonProperty("spikes")] public ProfilerSpikeSummary Spikes { get; set; }
        [JsonProperty("frames")] public ProfilerFrameSummary[] Frames { get; set; }

        internal static ProfilerOverviewResult CreateUnavailable(
            ProfilerOverviewCaptureInfo capture)
        {
            return new ProfilerOverviewResult
            {
                Available = false,
                Capture = capture,
                SlowestFrames = Array.Empty<ProfilerFrameSummary>(),
                Spikes = ProfilerSpikeSummary.CreateUnavailable(),
                Frames = Array.Empty<ProfilerFrameSummary>()
            };
        }
    }

    internal sealed class ProfilerOverviewCaptureInfo
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

    internal sealed class ProfilerOverviewSelection
    {
        [JsonProperty("requestedFrameCount")] public int RequestedFrameCount { get; set; }
        [JsonProperty("frameCount")] public int FrameCount { get; set; }
        [JsonProperty("firstFrameIndex")] public int FirstFrameIndex { get; set; }
        [JsonProperty("endFrameIndex")] public int EndFrameIndex { get; set; }
        [JsonProperty("complete")] public bool Complete { get; set; }
        [JsonProperty("hasMoreOlderFrames")] public bool HasMoreOlderFrames { get; set; }
        [JsonProperty("stopReason")] public string StopReason { get; set; }
        [JsonProperty("sessionBoundaryChecked")] public bool SessionBoundaryChecked { get; set; }
        [JsonProperty("includeFrames")] public bool IncludeFrames { get; set; }
    }

    internal sealed class ProfilerOverviewFrameStats
    {
        [JsonProperty("validFrameCount")] public int ValidFrameCount { get; set; }
        [JsonProperty("missingFrameTimeCount")] public int MissingFrameTimeCount { get; set; }
        [JsonProperty("meanFrameTimeMs")] public double? MeanFrameTimeMs { get; set; }
        [JsonProperty("p50FrameTimeMs")] public double? P50FrameTimeMs { get; set; }
        [JsonProperty("p95FrameTimeMs")] public double? P95FrameTimeMs { get; set; }
        [JsonProperty("p99FrameTimeMs")] public double? P99FrameTimeMs { get; set; }
        [JsonProperty("maxFrameTimeMs")] public double? MaxFrameTimeMs { get; set; }
        [JsonProperty("maxFrameIndex")] public int? MaxFrameIndex { get; set; }
    }

    internal sealed class ProfilerSpikeSummary
    {
        [JsonProperty("algorithm")] public string Algorithm { get; set; }
        [JsonProperty("thresholdMs")] public double? ThresholdMs { get; set; }
        [JsonProperty("matched")] public int Matched { get; set; }
        [JsonProperty("returned")] public int Returned { get; set; }
        [JsonProperty("frames")] public ProfilerSpikeFrame[] Frames { get; set; }

        internal static ProfilerSpikeSummary CreateUnavailable()
        {
            return new ProfilerSpikeSummary
            {
                Algorithm = ProfilerOverviewAnalyzer.SpikeAlgorithm,
                Frames = Array.Empty<ProfilerSpikeFrame>()
            };
        }
    }

    internal sealed class ProfilerSpikeFrame
    {
        [JsonProperty("frameIndex")] public int FrameIndex { get; set; }
        [JsonProperty("frameTimeMs")] public double FrameTimeMs { get; set; }
        [JsonProperty("overThresholdPercent")] public double? OverThresholdPercent { get; set; }
    }
}
