using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AgentBridge
{
    /// <summary>
    /// compare_profiler_windows(只读):对当前或 capture_profiler 保存的
    /// baseline/candidate 窗口做帧时间和路径热点外连接比较。
    /// </summary>
    public sealed class CompareProfilerWindowsHandler : ICommandHandler
    {
        public string Command => "compare_profiler_windows";

        public string Description =>
            "比较当前或已保存 capture 的 baseline/candidate 窗口；按每帧平均或 P95 指标输出 regression/improvement/new/removed";

        public string Group => "Profiling";
        public bool CanDisable => true;
        public CommandBatchMode BatchMode => CommandBatchMode.Allowed;

        public Task<object> ExecuteAsync(JObject @params)
        {
            var baseline = (JObject)@params["baseline"];
            var candidate = (JObject)@params["candidate"];
            var metric = @params["metric"]?.Value<string>() ??
                         "selfTimeAverageMs";
            var candidateLimit =
                @params["candidateLimit"]?.ToObject<int?>() ?? 500;
            var selector = GetProfilerDataHandler.ParseThreadSelector(
                @params["threadSelector"] as JObject);
            var common = new ProfilerCompareOptions
            {
                Metric = metric,
                Direction = @params["direction"]?.Value<string>() ?? "all",
                MinDeltaPercent =
                    @params["minDeltaPercent"]?.ToObject<double?>() ?? 0,
                Limit = @params["limit"]?.ToObject<int?>() ?? 50
            };
            if (candidateLimit < common.Limit)
            {
                throw new CommandException(
                    ErrorCodes.InvalidParams,
                    "candidateLimit 必须大于等于 limit");
            }

            var baselineCaptureId =
                baseline["captureId"]?.Value<string>();
            var candidateCaptureId =
                candidate["captureId"]?.Value<string>();
            var pair = ProfilerSavedCaptureAccess.ReadPair(
                baselineCaptureId,
                () => ProfilerDataReader.QuerySingle(BuildQuery(
                    baseline,
                    @params,
                    selector,
                    metric,
                    candidateLimit)),
                candidateCaptureId,
                () => ProfilerDataReader.QuerySingle(BuildQuery(
                    candidate,
                    @params,
                    selector,
                    metric,
                    candidateLimit)));
            var baselineResult = pair.First;
            var candidateResult = pair.Second;
            ProfilerDataReader.MarkCaptureSource(
                baselineResult,
                pair.FirstImmutable ? "saved" : "current",
                pair.FirstImmutable);
            ProfilerDataReader.MarkCaptureSource(
                candidateResult,
                pair.SecondImmutable ? "saved" : "current",
                pair.SecondImmutable);

            if (!baselineResult.Available || !candidateResult.Available)
            {
                throw new CommandException(
                    ProfilerErrorCodes.FrameNotFound,
                    "baseline 或 candidate 窗口没有可用 Profiler 帧");
            }
            return Task.FromResult<object>(
                ProfilerWindowDiffer.Diff(
                    baselineResult,
                    candidateResult,
                    common,
                    candidateLimit));
        }

        private static ProfilerQueryOptions BuildQuery(
            JObject window,
            JObject @params,
            ProfilerThreadSelector selector,
            string metric,
            int candidateLimit)
        {
            var needsDistribution = metric == "selfTimeP95Ms";
            return new ProfilerQueryOptions
            {
                EndFrameIndex = window["endFrameIndex"]?.ToObject<int?>(),
                FrameCount = window["frameCount"]?.ToObject<int?>() ??
                             ProfilerQueryOptions.DefaultFrameCount,
                ThreadIndex = @params["threadIndex"]?.ToObject<int?>() ?? 0,
                ThreadSelector = selector,
                Query = @params["query"]?.Value<string>(),
                Categories = (@params["categories"] as JArray)?
                                 .Values<string>()
                                 .Distinct(StringComparer.OrdinalIgnoreCase)
                                 .ToArray() ??
                             Array.Empty<string>(),
                SortBy = CompareSortToQuerySort(metric),
                MaxDepth = @params["maxDepth"]?.ToObject<int?>() ??
                           ProfilerQueryOptions.DefaultMaxDepth,
                IncludeEditorOnly =
                    @params["includeEditorOnly"]?.ToObject<bool?>() ?? false,
                Limit = candidateLimit,
                AllowInternalLimit = true,
                HotspotDetails = needsDistribution
                    ? new ProfilerHotspotDetailsOptions
                    {
                        Metric = "selfTimeMs",
                        SlowestLimit = 0,
                        TrendFrameCount = 0,
                        HotspotLimit = candidateLimit
                    }
                    : null
            };
        }

        private static string CompareSortToQuerySort(string metric)
        {
            switch (metric)
            {
                case "totalTimeAverageMs":
                    return "totalTimeSumMs";
                case "gcAllocAverageBytes":
                    return "gcAllocSumBytes";
                case "callCountAverage":
                    return "callCount";
                case "selfTimeP95Ms":
                    return "selfTimeP95Ms";
                default:
                    return "selfTimeSumMs";
            }
        }

        public JObject ParamsSchema { get; } = CreateSchema();

        private static JObject CreateSchema()
        {
            var schema = JObject.Parse(@"{
  ""type"": ""object"",
  ""additionalProperties"": false,
  ""required"": [""baseline"", ""candidate""],
  ""not"": { ""required"": [""threadIndex"", ""threadSelector""] },
  ""properties"": {
    ""baseline"": {
      ""type"": ""object"",
      ""additionalProperties"": false,
      ""properties"": {
        ""captureId"": { ""type"": ""string"", ""pattern"": ""^[0-9a-fA-F]{32}$"" },
        ""endFrameIndex"": { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 2147483647 },
        ""frameCount"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 120, ""default"": 30 }
      }
    },
    ""candidate"": {
      ""type"": ""object"",
      ""additionalProperties"": false,
      ""properties"": {
        ""captureId"": { ""type"": ""string"", ""pattern"": ""^[0-9a-fA-F]{32}$"" },
        ""endFrameIndex"": { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 2147483647 },
        ""frameCount"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 120, ""default"": 30 }
      }
    },
    ""threadIndex"": { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 1023, ""default"": 0 },
    ""threadSelector"": {},
    ""query"": { ""type"": ""string"", ""maxLength"": 256 },
    ""categories"": {
      ""type"": ""array"",
      ""maxItems"": 16,
      ""items"": { ""type"": ""string"", ""minLength"": 1, ""maxLength"": 64 }
    },
    ""metric"": {
      ""type"": ""string"",
      ""enum"": [""selfTimeAverageMs"", ""totalTimeAverageMs"", ""gcAllocAverageBytes"", ""callCountAverage"", ""selfTimeP95Ms""],
      ""default"": ""selfTimeAverageMs"",
      ""description"": ""比较指标；selfTimeP95Ms 按窗口逐帧分布计算，缺失样本按 0 计。""
    },
    ""direction"": {
      ""type"": ""string"",
      ""enum"": [""all"", ""regressions"", ""improvements""],
      ""default"": ""all""
    },
    ""minDeltaPercent"": {
      ""type"": ""number"",
      ""minimum"": 0,
      ""maximum"": 1000000000,
      ""default"": 0
    },
    ""candidateLimit"": {
      ""type"": ""integer"",
      ""minimum"": 1,
      ""maximum"": 1000,
      ""default"": 500,
      ""description"": ""每侧参与外连接的候选热点上限；结果会明确标记候选是否截断。""
    },
    ""limit"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 100, ""default"": 50 },
    ""maxDepth"": { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 128, ""default"": 64 },
    ""includeEditorOnly"": { ""type"": ""boolean"", ""default"": false }
  }
}");
            schema["properties"]["threadSelector"] =
                new GetProfilerDataHandler()
                    .ParamsSchema["properties"]["threadSelector"]
                    .DeepClone();
            return schema;
        }
    }

    internal sealed class ProfilerCompareOptions
    {
        internal string Metric { get; set; }
        internal string Direction { get; set; }
        internal double MinDeltaPercent { get; set; }
        internal int Limit { get; set; }
    }

    internal static class ProfilerWindowDiffer
    {
        internal static ProfilerComparisonResult Diff(
            ProfilerDataResult baseline,
            ProfilerDataResult candidate,
            ProfilerCompareOptions options,
            int candidateLimit)
        {
            var baselineByKey = Index(baseline.Hotspots);
            var candidateByKey = Index(candidate.Hotspots);
            var keys = new HashSet<ProfilerHotspotIdentity>(
                baselineByKey.Keys);
            keys.UnionWith(candidateByKey.Keys);
            var baselineCandidatesTruncated =
                baseline.MatchedHotspots > baseline.Returned;
            var candidateCandidatesTruncated =
                candidate.MatchedHotspots > candidate.Returned;
            var indeterminateOneSidedHotspots = 0;

            var all = new List<ProfilerHotspotDiff>(keys.Count);
            foreach (var key in keys)
            {
                baselineByKey.TryGetValue(key, out var before);
                candidateByKey.TryGetValue(key, out var after);
                // 若缺失侧的候选集已截断，无法区分“确实不存在”和“仍存在但未进入
                // Top candidateLimit”。此时绝不能把热点误报为 new/removed。
                if ((before == null && baselineCandidatesTruncated) ||
                    (after == null && candidateCandidatesTruncated))
                {
                    indeterminateOneSidedHotspots++;
                    continue;
                }
                var beforeValue = before == null
                    ? (double?)null
                    : Metric(before, options.Metric);
                var afterValue = after == null
                    ? (double?)null
                    : Metric(after, options.Metric);
                var absolute = (afterValue ?? 0) - (beforeValue ?? 0);
                var percent = beforeValue.HasValue && beforeValue.Value > 0
                    ? (double?)ProfilerDataValue.RoundSigned(
                        absolute / beforeValue.Value * 100.0)
                    : null;
                var status = Status(before, after, absolute);
                if (!DirectionMatches(status, options.Direction) ||
                    !DeltaMatches(percent, status, options.MinDeltaPercent))
                {
                    continue;
                }

                var source = after ?? before;
                all.Add(new ProfilerHotspotDiff
                {
                    Name = source.Name,
                    Path = source.Path,
                    Category = source.Category,
                    Status = status,
                    Baseline = beforeValue,
                    Candidate = afterValue,
                    AbsoluteDelta = ProfilerDataValue.RoundSigned(absolute),
                    PercentDelta = percent
                });
            }

            all.Sort((left, right) =>
            {
                var magnitude = Math.Abs(right.AbsoluteDelta)
                    .CompareTo(Math.Abs(left.AbsoluteDelta));
                if (magnitude != 0)
                {
                    return magnitude;
                }
                var path = StringComparer.Ordinal.Compare(
                    left.Path, right.Path);
                return path != 0
                    ? path
                    : StringComparer.Ordinal.Compare(
                        left.Category, right.Category);
            });

            return new ProfilerComparisonResult
            {
                BaselineCaptureId = baseline.Capture.CaptureId,
                CandidateCaptureId = candidate.Capture.CaptureId,
                Metric = options.Metric,
                Baseline = WindowInfo(baseline),
                Candidate = WindowInfo(candidate),
                FrameDiff = FrameDiff(
                    baseline.FrameStats, candidate.FrameStats),
                CandidateLimit = candidateLimit,
                BaselineCandidatesTruncated =
                    baselineCandidatesTruncated,
                CandidateCandidatesTruncated =
                    candidateCandidatesTruncated,
                CandidateSetTruncated =
                    baselineCandidatesTruncated ||
                    candidateCandidatesTruncated,
                IndeterminateOneSidedHotspots =
                    indeterminateOneSidedHotspots,
                Matched = all.Count,
                Returned = Math.Min(options.Limit, all.Count),
                Truncated = all.Count > options.Limit,
                Summary = Summarize(all),
                HotspotDiffs = all.Take(options.Limit).ToArray()
            };
        }

        private static Dictionary<ProfilerHotspotIdentity, ProfilerHotspotResult>
            Index(IEnumerable<ProfilerHotspotResult> values)
        {
            var result =
                new Dictionary<ProfilerHotspotIdentity, ProfilerHotspotResult>();
            foreach (var value in values ?? Array.Empty<ProfilerHotspotResult>())
            {
                result[new ProfilerHotspotIdentity(
                    value.FullCategory ?? value.Category,
                    value.FullPath ?? value.Path)] = value;
            }
            return result;
        }

        private static double Metric(
            ProfilerHotspotResult value,
            string metric)
        {
            switch (metric)
            {
                case "totalTimeAverageMs":
                    return value.TotalTimeAverageMs;
                case "gcAllocAverageBytes":
                    return value.GcAllocAverageBytes;
                case "callCountAverage":
                    return value.CallCountAverage;
                case "selfTimeP95Ms":
                    return value.Details?.P95 ?? 0;
                default:
                    return value.SelfTimeAverageMs;
            }
        }

        private static string Status(
            ProfilerHotspotResult before,
            ProfilerHotspotResult after,
            double absolute)
        {
            if (before == null)
            {
                return "new";
            }
            if (after == null)
            {
                return "removed";
            }
            if (absolute > 0)
            {
                return "regressed";
            }
            if (absolute < 0)
            {
                return "improved";
            }
            return "unchanged";
        }

        private static bool DirectionMatches(string status, string direction)
        {
            return direction == "all" ||
                   direction == "regressions" &&
                   (status == "regressed" || status == "new") ||
                   direction == "improvements" &&
                   (status == "improved" || status == "removed");
        }

        private static bool DeltaMatches(
            double? percent,
            string status,
            double minimum)
        {
            if (minimum <= 0 || status == "new" || status == "removed")
            {
                return true;
            }
            // 指标均为非负数。baseline=0 -> candidate>0 的回归百分比没有
            // 有限定义，但不应因此被任意正的 minDeltaPercent 隐藏。
            if (status == "regressed" && !percent.HasValue)
            {
                return true;
            }
            return percent.HasValue && Math.Abs(percent.Value) >= minimum;
        }

        private static ProfilerComparisonWindowInfo WindowInfo(
            ProfilerDataResult value)
        {
            return new ProfilerComparisonWindowInfo
            {
                CaptureId = value.Capture.CaptureId,
                Source = value.Capture.Source,
                FrameCount = value.Selection.FrameCount,
                FirstFrameIndex = value.Selection.FirstFrameIndex,
                EndFrameIndex = value.Selection.EndFrameIndex,
                Thread = value.Thread,
                FrameStats = value.FrameStats,
                MatchedHotspots = value.MatchedHotspots,
                ReturnedCandidates = value.Returned
            };
        }

        private static ProfilerFrameDiff FrameDiff(
            ProfilerFrameStats before,
            ProfilerFrameStats after)
        {
            return new ProfilerFrameDiff
            {
                MeanDeltaMs = Delta(
                    before?.MeanFrameTimeMs, after?.MeanFrameTimeMs),
                MeanDeltaPercent = PercentDelta(
                    before?.MeanFrameTimeMs, after?.MeanFrameTimeMs),
                P50DeltaMs = Delta(
                    before?.P50FrameTimeMs, after?.P50FrameTimeMs),
                P50DeltaPercent = PercentDelta(
                    before?.P50FrameTimeMs, after?.P50FrameTimeMs),
                P95DeltaMs = Delta(
                    before?.P95FrameTimeMs, after?.P95FrameTimeMs),
                P95DeltaPercent = PercentDelta(
                    before?.P95FrameTimeMs, after?.P95FrameTimeMs),
                P99DeltaMs = Delta(
                    before?.P99FrameTimeMs, after?.P99FrameTimeMs),
                P99DeltaPercent = PercentDelta(
                    before?.P99FrameTimeMs, after?.P99FrameTimeMs)
            };
        }

        private static double? Delta(double? before, double? after)
        {
            return before.HasValue && after.HasValue
                ? (double?)ProfilerDataValue.RoundSigned(
                    after.Value - before.Value)
                : null;
        }

        private static double? PercentDelta(double? before, double? after)
        {
            return before.HasValue && before.Value > 0 && after.HasValue
                ? (double?)ProfilerDataValue.RoundSigned(
                    (after.Value - before.Value) / before.Value * 100.0)
                : null;
        }

        private static ProfilerComparisonSummary Summarize(
            IEnumerable<ProfilerHotspotDiff> values)
        {
            var summary = new ProfilerComparisonSummary();
            foreach (var value in values)
            {
                switch (value.Status)
                {
                    case "regressed":
                        summary.Regressed++;
                        break;
                    case "improved":
                        summary.Improved++;
                        break;
                    case "new":
                        summary.New++;
                        break;
                    case "removed":
                        summary.Removed++;
                        break;
                    default:
                        summary.Unchanged++;
                        break;
                }
            }
            return summary;
        }

        private readonly struct ProfilerHotspotIdentity :
            IEquatable<ProfilerHotspotIdentity>
        {
            internal ProfilerHotspotIdentity(string category, string path)
            {
                Category = category ?? "";
                Path = path ?? "";
            }

            private string Category { get; }
            private string Path { get; }

            public bool Equals(ProfilerHotspotIdentity other)
            {
                return StringComparer.Ordinal.Equals(Category, other.Category) &&
                       StringComparer.Ordinal.Equals(Path, other.Path);
            }

            public override bool Equals(object obj)
            {
                return obj is ProfilerHotspotIdentity other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return (StringComparer.Ordinal.GetHashCode(Category) * 397) ^
                           StringComparer.Ordinal.GetHashCode(Path);
                }
            }
        }
    }

    internal sealed class ProfilerComparisonResult
    {
        [JsonProperty("baselineCaptureId")] public string BaselineCaptureId { get; set; }
        [JsonProperty("candidateCaptureId")] public string CandidateCaptureId { get; set; }
        [JsonProperty("metric")] public string Metric { get; set; }
        [JsonProperty("baseline")] public ProfilerComparisonWindowInfo Baseline { get; set; }
        [JsonProperty("candidate")] public ProfilerComparisonWindowInfo Candidate { get; set; }
        [JsonProperty("frameDiff")] public ProfilerFrameDiff FrameDiff { get; set; }
        [JsonProperty("candidateLimit")] public int CandidateLimit { get; set; }
        [JsonProperty("baselineCandidatesTruncated")]
        public bool BaselineCandidatesTruncated { get; set; }
        [JsonProperty("candidateCandidatesTruncated")]
        public bool CandidateCandidatesTruncated { get; set; }
        [JsonProperty("candidateSetTruncated")] public bool CandidateSetTruncated { get; set; }
        [JsonProperty("indeterminateOneSidedHotspots")]
        public int IndeterminateOneSidedHotspots { get; set; }
        [JsonProperty("matched")] public int Matched { get; set; }
        [JsonProperty("returned")] public int Returned { get; set; }
        [JsonProperty("truncated")] public bool Truncated { get; set; }
        [JsonProperty("summary")] public ProfilerComparisonSummary Summary { get; set; }
        [JsonProperty("hotspotDiffs")] public ProfilerHotspotDiff[] HotspotDiffs { get; set; }
    }

    internal sealed class ProfilerComparisonWindowInfo
    {
        [JsonProperty("captureId")] public string CaptureId { get; set; }
        [JsonProperty("source")] public string Source { get; set; }
        [JsonProperty("frameCount")] public int FrameCount { get; set; }
        [JsonProperty("firstFrameIndex")] public int FirstFrameIndex { get; set; }
        [JsonProperty("endFrameIndex")] public int EndFrameIndex { get; set; }
        [JsonProperty("thread")] public ProfilerThreadInfo Thread { get; set; }
        [JsonProperty("frameStats")] public ProfilerFrameStats FrameStats { get; set; }
        [JsonProperty("matchedHotspots")] public int MatchedHotspots { get; set; }
        [JsonProperty("returnedCandidates")] public int ReturnedCandidates { get; set; }
    }

    internal sealed class ProfilerFrameDiff
    {
        [JsonProperty("meanDeltaMs")] public double? MeanDeltaMs { get; set; }
        [JsonProperty("meanDeltaPercent")] public double? MeanDeltaPercent { get; set; }
        [JsonProperty("p50DeltaMs")] public double? P50DeltaMs { get; set; }
        [JsonProperty("p50DeltaPercent")] public double? P50DeltaPercent { get; set; }
        [JsonProperty("p95DeltaMs")] public double? P95DeltaMs { get; set; }
        [JsonProperty("p95DeltaPercent")] public double? P95DeltaPercent { get; set; }
        [JsonProperty("p99DeltaMs")] public double? P99DeltaMs { get; set; }
        [JsonProperty("p99DeltaPercent")] public double? P99DeltaPercent { get; set; }
    }

    internal sealed class ProfilerHotspotDiff
    {
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("path")] public string Path { get; set; }
        [JsonProperty("category")] public string Category { get; set; }
        [JsonProperty("status")] public string Status { get; set; }
        [JsonProperty("baseline")] public double? Baseline { get; set; }
        [JsonProperty("candidate")] public double? Candidate { get; set; }
        [JsonProperty("absoluteDelta")] public double AbsoluteDelta { get; set; }
        [JsonProperty("percentDelta")] public double? PercentDelta { get; set; }
    }

    internal sealed class ProfilerComparisonSummary
    {
        [JsonProperty("regressed")] public int Regressed { get; set; }
        [JsonProperty("improved")] public int Improved { get; set; }
        [JsonProperty("new")] public int New { get; set; }
        [JsonProperty("removed")] public int Removed { get; set; }
        [JsonProperty("unchanged")] public int Unchanged { get; set; }
    }
}
