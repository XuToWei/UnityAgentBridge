using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace AgentBridge
{
    internal sealed class ProfilerQueryOptions
    {
        internal const int DefaultFrameCount = 30;
        internal const int DefaultMaxDepth = 64;
        internal const int DefaultLimit = 50;
        internal const int MaxPublicLimit = 100;
        internal const int MaxInternalLimit = 1000;
        internal const string DefaultSortBy = "selfTimeSumMs";

        internal int? EndFrameIndex { get; set; }
        internal int FrameCount { get; set; }
        internal int ThreadIndex { get; set; }
        internal ulong? ExpectedThreadId { get; set; }
        internal ProfilerThreadSelector ThreadSelector { get; set; }
        internal string Query { get; set; }
        internal string[] Categories { get; set; } = Array.Empty<string>();
        internal double MinSelfTimeSumMs { get; set; }
        internal long MinGcAllocSumBytes { get; set; }
        internal long MinCallCount { get; set; }
        internal string SortBy { get; set; }
        internal int MaxDepth { get; set; }
        internal bool IncludeEditorOnly { get; set; }
        internal int Limit { get; set; }
        internal ProfilerHotspotDetailsOptions HotspotDetails { get; set; }
        internal bool AllowInternalLimit { get; set; }
        internal bool SingleThreadOperation { get; set; }
        internal int SampleLimit { get; set; } = ProfilerDataReader.MaxScannedSamples;
        internal System.Diagnostics.Stopwatch SharedStopwatch { get; set; }
        internal ProfilerThreadInfo PreResolvedThread { get; set; }

        internal ProfilerQueryOptions Clone()
        {
            return (ProfilerQueryOptions)MemberwiseClone();
        }
    }

    internal sealed class ProfilerThreadSelector
    {
        internal const int DefaultMaxThreads = 4;
        internal const int MaximumThreads = 16;

        internal string Mode { get; set; }
        internal int Index { get; set; }
        internal ulong Id { get; set; }
        internal string Name { get; set; }
        internal string Group { get; set; }
        internal int Offset { get; set; }
        internal int MaxThreads { get; set; } = DefaultMaxThreads;
    }

    internal sealed class ProfilerHotspotDetailsOptions
    {
        internal const int DefaultSlowestLimit = 3;
        internal const int DefaultTrendFrameCount = 30;
        internal const int DefaultHotspotLimit = 5;
        internal const int MaxTrendPoints = 600;

        internal string Metric { get; set; } = "selfTimeMs";
        internal int SlowestLimit { get; set; } = DefaultSlowestLimit;
        internal int TrendFrameCount { get; set; } = DefaultTrendFrameCount;
        internal int HotspotLimit { get; set; } = DefaultHotspotLimit;
    }

    /// <summary>Hierarchy 中一个帧内节点的普通托管快照。</summary>
    internal sealed class ProfilerFrameHotspot
    {
        internal string Name { get; set; } = "";
        internal string Path { get; set; } = "";
        internal string Category { get; set; } = "";
        internal double TotalTimeMs { get; set; }
        internal double SelfTimeMs { get; set; }
        internal long CallCount { get; set; }
        internal long GcAllocBytes { get; set; }
        internal long WarningCount { get; set; }
    }

    internal sealed class ProfilerHotspotPage
    {
        internal int UniqueCount { get; set; }
        internal int QueryMatchedCount { get; set; }
        internal int CategoryMatchedCount { get; set; }
        internal int ThresholdMatchedCount { get; set; }
        internal int MatchedCount { get; set; }
        internal ProfilerHotspotResult[] Hotspots { get; set; } =
            Array.Empty<ProfilerHotspotResult>();
    }

    /// <summary>
    /// 纯托管跨帧分析器。每帧先合并相同 category + path，再更新 sum/max，
    /// 防止一个帧内重复节点把 framesSeen 或单帧最大值算错。
    /// </summary>
    internal sealed class ProfilerHotspotAnalyzer
    {
        private const int SelectionBudgetCheckInterval = 256;

        private readonly bool m_TrackDetails;
        private readonly Dictionary<HotspotKey, Aggregate> m_Aggregates =
            new Dictionary<HotspotKey, Aggregate>();
        // 提前 query 时只为未匹配路径保留轻量 identity，以维持 uniqueHotspots 语义。
        private readonly HashSet<HotspotKey> m_UnmatchedKeys =
            new HashSet<HotspotKey>();
        // Category 在 Reader 中下推时同样只保留 identity，避免读取五个数值列。
        private readonly HashSet<HotspotKey> m_CategoryRejectedKeys =
            new HashSet<HotspotKey>();

        internal ProfilerHotspotAnalyzer(bool trackDetails = false)
        {
            m_TrackDetails = trackDetails;
        }

        internal int UniqueCount =>
            m_Aggregates.Count + m_UnmatchedKeys.Count + m_CategoryRejectedKeys.Count;
        internal int AggregatedCount => m_Aggregates.Count;

        internal void AddFrame(int frameIndex, IEnumerable<ProfilerFrameHotspot> samples)
        {
            if (samples == null)
            {
                return;
            }

            foreach (var sample in samples)
            {
                if (sample == null)
                {
                    continue;
                }

                AddSample(
                    frameIndex,
                    sample.Name,
                    sample.Path,
                    sample.Category,
                    sample.TotalTimeMs,
                    sample.SelfTimeMs,
                    sample.CallCount,
                    sample.GcAllocBytes,
                    sample.WarningCount);
            }
        }

        /// <summary>
        /// Reader 热路径直接调用，避免“节点 DTO -> normalized DTO -> 两级字典”的重复物化。
        /// Aggregate 内部暂存当前帧，同一帧重复 path/category 会先合并再计入跨帧统计。
        /// </summary>
        internal void AddSample(
            int frameIndex,
            string name,
            string path,
            string category,
            double totalTimeMs,
            double selfTimeMs,
            long callCount,
            long gcAllocBytes,
            long warningCount)
        {
            name = name ?? "";
            path = string.IsNullOrEmpty(path) ? name : path;
            category = category ?? "";
            var key = new HotspotKey(category, path);
            m_UnmatchedKeys.Remove(key);
            m_CategoryRejectedKeys.Remove(key);

            if (!m_Aggregates.TryGetValue(key, out var aggregate))
            {
                aggregate = new Aggregate(name, path, category, m_TrackDetails);
                m_Aggregates.Add(key, aggregate);
            }

            aggregate.AddSample(
                frameIndex,
                ProfilerDataValue.NonNegative(totalTimeMs),
                ProfilerDataValue.NonNegative(selfTimeMs),
                Math.Max(0, callCount),
                Math.Max(0, gcAllocBytes),
                Math.Max(0, warningCount));
        }

        /// <summary>
        /// 提前 query 排除的节点不读取数值列，但仍计入全量 uniqueHotspots。
        /// 若同一 identity 后续进入聚合，AddSample 会从此集合移除它。
        /// </summary>
        internal void RecordUnmatched(string name, string path, string category)
        {
            name = name ?? "";
            path = string.IsNullOrEmpty(path) ? name : path;
            var key = new HotspotKey(category ?? "", path);
            if (!m_Aggregates.ContainsKey(key) &&
                !m_CategoryRejectedKeys.Contains(key))
            {
                m_UnmatchedKeys.Add(key);
            }
        }

        /// <summary>
        /// query 已匹配、但 Category 未匹配的 identity。单独计数以便返回各过滤阶段，
        /// 同时避免为必然被排除的节点读取 native 数值列。
        /// </summary>
        internal void RecordCategoryRejected(string name, string path, string category)
        {
            name = name ?? "";
            path = string.IsNullOrEmpty(path) ? name : path;
            var key = new HotspotKey(category ?? "", path);
            if (!m_Aggregates.ContainsKey(key) && !m_UnmatchedKeys.Contains(key))
            {
                m_CategoryRejectedKeys.Add(key);
            }
        }

        internal ProfilerHotspotPage Select(
            int analyzedFrameCount,
            double analyzedFrameTimeSumMs,
            string query,
            string sortBy,
            int limit,
            Action<int> progress = null,
            IReadOnlyCollection<string> categories = null,
            double minSelfTimeSumMs = 0,
            long minGcAllocSumBytes = 0,
            long minCallCount = 0,
            ProfilerHotspotDetailsOptions details = null,
            IReadOnlyList<int> framesWithThread = null)
        {
            var comparison = StringComparison.OrdinalIgnoreCase;
            var capacity = Math.Max(0, limit);
            var selected = new List<Aggregate>(
                Math.Min(capacity, m_Aggregates.Count));
            var queryMatchedCount = m_CategoryRejectedKeys.Count;
            var categoryMatchedCount = 0;
            var thresholdMatchedCount = 0;
            var visited = 0;

            foreach (var item in m_Aggregates.Values)
            {
                item.FlushPending();
                if (sortBy == "selfTimeP95Ms")
                {
                    item.PrepareSelfTimeP95(analyzedFrameCount);
                }
                visited++;
                if (progress != null &&
                    (visited & (SelectionBudgetCheckInterval - 1)) == 0)
                {
                    progress(visited);
                }

                if (!string.IsNullOrEmpty(query) &&
                    item.Name.IndexOf(query, comparison) < 0 &&
                    item.Path.IndexOf(query, comparison) < 0)
                {
                    continue;
                }

                queryMatchedCount++;
                if (!MatchesCategory(item.Category, categories))
                {
                    continue;
                }

                categoryMatchedCount++;
                if (item.SelfTimeSumMs < minSelfTimeSumMs ||
                    item.GcAllocSumBytes < minGcAllocSumBytes ||
                    item.CallCount < minCallCount)
                {
                    continue;
                }

                thresholdMatchedCount++;
                AddToTopK(selected, item, capacity, sortBy);
            }

            if (progress != null &&
                (visited & (SelectionBudgetCheckInterval - 1)) != 0)
            {
                progress(visited);
            }

            selected.Sort((left, right) => Compare(left, right, sortBy));
            var output = new ProfilerHotspotResult[selected.Count];
            for (var index = 0; index < selected.Count; index++)
            {
                output[index] = selected[index].CreateResult(
                    analyzedFrameCount,
                    analyzedFrameTimeSumMs,
                    details != null && index < details.HotspotLimit ? details : null,
                    framesWithThread);
            }

            return new ProfilerHotspotPage
            {
                UniqueCount = UniqueCount,
                QueryMatchedCount = queryMatchedCount,
                CategoryMatchedCount = categoryMatchedCount,
                ThresholdMatchedCount = thresholdMatchedCount,
                // 保留旧字段语义为“所有过滤后参与 limit 的数量”。
                MatchedCount = thresholdMatchedCount,
                Hotspots = output
            };
        }

        internal static bool MatchesQuery(string name, string path, string query)
        {
            if (string.IsNullOrEmpty(query))
            {
                return true;
            }

            var comparison = StringComparison.OrdinalIgnoreCase;
            return (name ?? "").IndexOf(query, comparison) >= 0 ||
                   (path ?? "").IndexOf(query, comparison) >= 0;
        }

        internal static bool MatchesCategory(
            string category,
            IReadOnlyCollection<string> categories)
        {
            if (categories == null || categories.Count == 0)
            {
                return true;
            }

            foreach (var candidate in categories)
            {
                if (string.Equals(
                        category ?? "",
                        candidate ?? "",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static void AddToTopK(
            List<Aggregate> heap,
            Aggregate candidate,
            int capacity,
            string sortBy)
        {
            if (capacity == 0)
            {
                return;
            }

            if (heap.Count < capacity)
            {
                heap.Add(candidate);
                SiftWorstUp(heap, heap.Count - 1, sortBy);
                return;
            }

            // Root 是当前 Top K 中最差的一项；candidate 不更优时直接丢弃。
            if (Compare(candidate, heap[0], sortBy) >= 0)
            {
                return;
            }

            heap[0] = candidate;
            SiftWorstDown(heap, 0, sortBy);
        }

        private static void SiftWorstUp(
            IList<Aggregate> heap,
            int index,
            string sortBy)
        {
            while (index > 0)
            {
                var parent = (index - 1) / 2;
                if (Compare(heap[index], heap[parent], sortBy) <= 0)
                {
                    return;
                }

                Swap(heap, index, parent);
                index = parent;
            }
        }

        private static void SiftWorstDown(
            IList<Aggregate> heap,
            int index,
            string sortBy)
        {
            while (true)
            {
                var left = index * 2 + 1;
                if (left >= heap.Count)
                {
                    return;
                }

                var right = left + 1;
                var worst = right < heap.Count &&
                            Compare(heap[right], heap[left], sortBy) > 0
                    ? right
                    : left;
                if (Compare(heap[worst], heap[index], sortBy) <= 0)
                {
                    return;
                }

                Swap(heap, index, worst);
                index = worst;
            }
        }

        private static void Swap(IList<Aggregate> items, int left, int right)
        {
            var value = items[left];
            items[left] = items[right];
            items[right] = value;
        }

        private static int Compare(Aggregate left, Aggregate right, string sortBy)
        {
            var metric = sortBy == "gcAllocSumBytes"
                ? right.GcAllocSumBytes.CompareTo(left.GcAllocSumBytes)
                : sortBy == "callCount"
                    ? right.CallCount.CompareTo(left.CallCount)
                    : GetMetric(right, sortBy).CompareTo(GetMetric(left, sortBy));
            if (metric != 0)
            {
                return metric;
            }

            var path = StringComparer.Ordinal.Compare(left.Path, right.Path);
            return path != 0
                ? path
                : StringComparer.Ordinal.Compare(left.Category, right.Category);
        }

        private static double GetMetric(Aggregate item, string sortBy)
        {
            switch (sortBy)
            {
                case "totalTimeSumMs":
                    return item.TotalTimeSumMs;
                case "maxSelfTimeMs":
                    return item.MaxSelfTimeMs;
                case "gcAllocSumBytes":
                    return item.GcAllocSumBytes;
                case "selfTimeP95Ms":
                    return item.SelfTimeP95Ms;
                default:
                    return item.SelfTimeSumMs;
            }
        }

        private sealed class Aggregate
        {
            private readonly bool m_TrackDetails;
            private bool m_HasPending;
            private int m_PendingFrameIndex;
            private long m_PendingCallCount;
            private long m_PendingWarningCount;
            private double m_PendingTotalTimeMs;
            private double m_PendingSelfTimeMs;
            private long m_PendingGcAllocBytes;
            private bool m_HasFirstFrameMetric;
            private ProfilerHotspotFrameMetric m_FirstFrameMetric;
            private List<ProfilerHotspotFrameMetric> m_FrameMetrics;

            internal Aggregate(
                string name,
                string path,
                string category,
                bool trackDetails)
            {
                Name = name;
                Path = path;
                Category = category;
                m_TrackDetails = trackDetails;
            }

            internal string Name { get; }
            internal string Path { get; }
            internal string Category { get; }
            internal int FramesSeen { get; private set; }
            internal long CallCount { get; private set; }
            internal long WarningCount { get; private set; }
            internal double TotalTimeSumMs { get; private set; }
            internal double SelfTimeSumMs { get; private set; }
            internal double MaxTotalTimeMs { get; private set; }
            internal int? MaxTotalTimeFrameIndex { get; private set; }
            internal double MaxSelfTimeMs { get; private set; }
            internal int? MaxSelfTimeFrameIndex { get; private set; }
            internal double SelfTimeP95Ms { get; private set; }
            internal long GcAllocSumBytes { get; private set; }

            internal void AddSample(
                int frameIndex,
                double totalTimeMs,
                double selfTimeMs,
                long callCount,
                long gcAllocBytes,
                long warningCount)
            {
                if (m_HasPending && m_PendingFrameIndex != frameIndex)
                {
                    FlushPending();
                }
                if (!m_HasPending)
                {
                    m_HasPending = true;
                    m_PendingFrameIndex = frameIndex;
                }

                m_PendingCallCount = ProfilerDataValue.SaturatingAdd(
                    m_PendingCallCount, callCount);
                m_PendingWarningCount = ProfilerDataValue.SaturatingAdd(
                    m_PendingWarningCount, warningCount);
                m_PendingGcAllocBytes = ProfilerDataValue.SaturatingAdd(
                    m_PendingGcAllocBytes, gcAllocBytes);
                m_PendingTotalTimeMs = ProfilerDataValue.AddFinite(
                    m_PendingTotalTimeMs, totalTimeMs);
                m_PendingSelfTimeMs = ProfilerDataValue.AddFinite(
                    m_PendingSelfTimeMs, selfTimeMs);
            }

            internal void FlushPending()
            {
                if (!m_HasPending)
                {
                    return;
                }

                FramesSeen++;
                CallCount = ProfilerDataValue.SaturatingAdd(
                    CallCount, m_PendingCallCount);
                WarningCount = ProfilerDataValue.SaturatingAdd(
                    WarningCount, m_PendingWarningCount);
                GcAllocSumBytes = ProfilerDataValue.SaturatingAdd(
                    GcAllocSumBytes, m_PendingGcAllocBytes);
                TotalTimeSumMs = ProfilerDataValue.AddFinite(
                    TotalTimeSumMs, m_PendingTotalTimeMs);
                SelfTimeSumMs = ProfilerDataValue.AddFinite(
                    SelfTimeSumMs, m_PendingSelfTimeMs);

                if (!MaxTotalTimeFrameIndex.HasValue ||
                    m_PendingTotalTimeMs > MaxTotalTimeMs)
                {
                    MaxTotalTimeMs = m_PendingTotalTimeMs;
                    MaxTotalTimeFrameIndex = m_PendingFrameIndex;
                }
                if (!MaxSelfTimeFrameIndex.HasValue ||
                    m_PendingSelfTimeMs > MaxSelfTimeMs)
                {
                    MaxSelfTimeMs = m_PendingSelfTimeMs;
                    MaxSelfTimeFrameIndex = m_PendingFrameIndex;
                }

                if (m_TrackDetails)
                {
                    var metric = new ProfilerHotspotFrameMetric
                    {
                        FrameIndex = m_PendingFrameIndex,
                        TotalTimeMs = m_PendingTotalTimeMs,
                        SelfTimeMs = m_PendingSelfTimeMs,
                        GcAllocBytes = m_PendingGcAllocBytes,
                        CallCount = m_PendingCallCount
                    };
                    if (!m_HasFirstFrameMetric)
                    {
                        m_FirstFrameMetric = metric;
                        m_HasFirstFrameMetric = true;
                    }
                    else
                    {
                        if (m_FrameMetrics == null)
                        {
                            m_FrameMetrics =
                                new List<ProfilerHotspotFrameMetric>
                                {
                                    m_FirstFrameMetric
                                };
                        }
                        m_FrameMetrics.Add(metric);
                    }
                }

                m_HasPending = false;
                m_PendingCallCount = 0;
                m_PendingWarningCount = 0;
                m_PendingTotalTimeMs = 0;
                m_PendingSelfTimeMs = 0;
                m_PendingGcAllocBytes = 0;
            }

            internal void PrepareSelfTimeP95(int analyzedFrameCount)
            {
                var basisCount = Math.Max(analyzedFrameCount, FramesSeen);
                if (basisCount <= 0 || FramesSeen <= 0)
                {
                    SelfTimeP95Ms = 0;
                    return;
                }

                var rank = Math.Max(
                    1,
                    (int)Math.Ceiling(0.95 * basisCount));
                var missingCount = basisCount - FramesSeen;
                if (rank <= missingCount)
                {
                    SelfTimeP95Ms = 0;
                    return;
                }

                var seenRank = rank - missingCount;
                if (m_FrameMetrics == null)
                {
                    SelfTimeP95Ms = m_HasFirstFrameMetric
                        ? m_FirstFrameMetric.SelfTimeMs
                        : 0;
                    return;
                }

                m_FrameMetrics.Sort(CompareFrameSelfTime);
                SelfTimeP95Ms =
                    m_FrameMetrics[Math.Min(
                        seenRank, m_FrameMetrics.Count) - 1]
                        .SelfTimeMs;
            }

            private static int CompareFrameSelfTime(
                ProfilerHotspotFrameMetric left,
                ProfilerHotspotFrameMetric right)
            {
                return left.SelfTimeMs.CompareTo(right.SelfTimeMs);
            }

            internal ProfilerHotspotResult CreateResult(
                int analyzedFrameCount,
                double analyzedFrameTimeSumMs,
                ProfilerHotspotDetailsOptions details = null,
                IReadOnlyList<int> framesWithThread = null)
            {
                var outputName = ProfilerDataText.Truncate(
                    Name, ProfilerDataText.MaxNameLength, out var nameTruncated);
                var outputPath = ProfilerDataText.Truncate(
                    Path, ProfilerDataText.MaxPathLength, out var pathTruncated);
                var outputCategory = ProfilerDataText.Truncate(
                    Category, ProfilerDataText.MaxCategoryLength, out var categoryTruncated);
                var denominator = Math.Max(1, analyzedFrameCount);

                var result = new ProfilerHotspotResult
                {
                    Name = outputName,
                    NameTruncated = nameTruncated,
                    Path = outputPath,
                    PathTruncated = pathTruncated,
                    Category = outputCategory,
                    CategoryTruncated = categoryTruncated,
                    FramesSeen = FramesSeen,
                    CallCount = CallCount,
                    WarningCount = WarningCount,
                    TotalTimeSumMs = ProfilerDataValue.Round(TotalTimeSumMs),
                    TotalTimeAverageMs = ProfilerDataValue.Round(
                        TotalTimeSumMs / denominator),
                    TotalTimePercent = ProfilerDataValue.Percent(
                        TotalTimeSumMs, analyzedFrameTimeSumMs),
                    SelfTimeSumMs = ProfilerDataValue.Round(SelfTimeSumMs),
                    SelfTimeAverageMs = ProfilerDataValue.Round(
                        SelfTimeSumMs / denominator),
                    SelfTimePercent = ProfilerDataValue.Percent(
                        SelfTimeSumMs, analyzedFrameTimeSumMs),
                    MaxTotalTimeMs = ProfilerDataValue.Round(MaxTotalTimeMs),
                    MaxTotalTimeFrameIndex = MaxTotalTimeFrameIndex,
                    MaxSelfTimeMs = ProfilerDataValue.Round(MaxSelfTimeMs),
                    MaxSelfTimeFrameIndex = MaxSelfTimeFrameIndex,
                    GcAllocSumBytes = GcAllocSumBytes,
                    GcAllocAverageBytes = ProfilerDataValue.Round(
                        (double)GcAllocSumBytes / denominator),
                    CallCountAverage = ProfilerDataValue.Round(
                        (double)CallCount / denominator),
                    FullPath = Path,
                    FullCategory = Category
                };
                if (details != null)
                {
                    IReadOnlyList<ProfilerHotspotFrameMetric> metrics =
                        m_FrameMetrics;
                    if (metrics == null && m_HasFirstFrameMetric)
                    {
                        metrics = new[] { m_FirstFrameMetric };
                    }
                    result.Details = ProfilerHotspotDetailsBuilder.Create(
                        details,
                        framesWithThread,
                        metrics);
                }
                return result;
            }
        }

        private readonly struct HotspotKey : IEquatable<HotspotKey>
        {
            internal HotspotKey(string category, string path)
            {
                Category = category;
                Path = path;
            }

            private string Category { get; }
            private string Path { get; }

            public bool Equals(HotspotKey other)
            {
                return StringComparer.Ordinal.Equals(Category, other.Category) &&
                       StringComparer.Ordinal.Equals(Path, other.Path);
            }

            public override bool Equals(object obj)
            {
                return obj is HotspotKey other && Equals(other);
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

    internal struct ProfilerHotspotFrameMetric
    {
        internal int FrameIndex { get; set; }
        internal double TotalTimeMs { get; set; }
        internal double SelfTimeMs { get; set; }
        internal long GcAllocBytes { get; set; }
        internal long CallCount { get; set; }
    }

    internal static class ProfilerHotspotDetailsBuilder
    {
        internal static ProfilerHotspotDetails Create(
            ProfilerHotspotDetailsOptions options,
            IReadOnlyList<int> framesWithThread,
            IReadOnlyList<ProfilerHotspotFrameMetric> metrics)
        {
            var metricByFrame = new Dictionary<int, double>();
            if (metrics != null)
            {
                foreach (var point in metrics)
                {
                    metricByFrame[point.FrameIndex] = MetricValue(point, options.Metric);
                }
            }

            var basis = framesWithThread == null
                ? metricByFrame.Keys.OrderBy(value => value).ToArray()
                : framesWithThread.ToArray();
            var values = new double[basis.Length];
            var maximum = 0.0;
            int? maximumFrameIndex = null;
            var longestConsecutive = 0;
            var currentConsecutive = 0;
            for (var index = 0; index < basis.Length; index++)
            {
                metricByFrame.TryGetValue(basis[index], out var value);
                value = ProfilerDataValue.NonNegative(value);
                values[index] = value;
                if (!maximumFrameIndex.HasValue || value >= maximum)
                {
                    maximum = value;
                    maximumFrameIndex = basis[index];
                }

                if (value > 0)
                {
                    currentConsecutive++;
                    longestConsecutive = Math.Max(
                        longestConsecutive, currentConsecutive);
                }
                else
                {
                    currentConsecutive = 0;
                }
            }

            var sorted = values.OrderBy(value => value).ToArray();
            var slowest = basis
                .Select((frameIndex, ordinal) => new
                {
                    FrameIndex = frameIndex,
                    Ordinal = ordinal,
                    Value = values[ordinal]
                })
                .Where(item => item.Value > 0)
                .OrderByDescending(item => item.Value)
                .ThenByDescending(item => item.Ordinal)
                .Take(options.SlowestLimit)
                .Select(item => new ProfilerHotspotOccurrence
                {
                    FrameIndex = item.FrameIndex,
                    Value = ProfilerDataValue.Round(item.Value)
                })
                .ToArray();

            var trendCount = Math.Min(options.TrendFrameCount, basis.Length);
            var trendStart = basis.Length - trendCount;
            var trend = new ProfilerHotspotTrendPoint[trendCount];
            for (var index = 0; index < trendCount; index++)
            {
                var source = trendStart + index;
                trend[index] = new ProfilerHotspotTrendPoint
                {
                    FrameIndex = basis[source],
                    Value = ProfilerDataValue.Round(values[source])
                };
            }

            // 趋势方向必须与返回给调用方的同一段窗口一致；否则“最近 30 帧”
            // 可能明显上升，而完整窗口的旧数据却把方向压成下降。
            var slope = LinearSlope(values, trendStart, trendCount);
            var mean = Mean(values, trendStart, trendCount);
            var epsilon = Math.Max(0.000001, mean * 0.01);
            var direction = slope > epsilon
                ? "increasing"
                : slope < -epsilon
                    ? "decreasing"
                    : "stable";

            return new ProfilerHotspotDetails
            {
                Metric = options.Metric,
                BasisFrameCount = basis.Length,
                MissingSampleValue = 0,
                P95 = Percentile(sorted, 0.95),
                P99 = Percentile(sorted, 0.99),
                Max = ProfilerDataValue.Round(maximum),
                MaxFrameIndex = maximumFrameIndex,
                LongestConsecutiveFrames = longestConsecutive,
                SlowestOccurrences = slowest,
                TrendDirection = direction,
                TrendSlopePerFrame = ProfilerDataValue.RoundSigned(slope),
                Trend = trend
            };
        }

        private static double MetricValue(
            ProfilerHotspotFrameMetric point,
            string metric)
        {
            switch (metric)
            {
                case "totalTimeMs":
                    return point.TotalTimeMs;
                case "gcAllocBytes":
                    return point.GcAllocBytes;
                case "calls":
                    return point.CallCount;
                default:
                    return point.SelfTimeMs;
            }
        }

        private static double? Percentile(double[] sorted, double percentile)
        {
            if (sorted.Length == 0)
            {
                return null;
            }
            var rank = Math.Max(1, (int)Math.Ceiling(percentile * sorted.Length));
            return ProfilerDataValue.Round(sorted[rank - 1]);
        }

        private static double Mean(
            IReadOnlyList<double> values,
            int start,
            int count)
        {
            if (count <= 0)
            {
                return 0;
            }

            var sum = 0.0;
            for (var index = 0; index < count; index++)
            {
                sum += values[start + index];
            }
            return sum / count;
        }

        private static double LinearSlope(
            IReadOnlyList<double> values,
            int start,
            int valueCount)
        {
            if (valueCount < 2)
            {
                return 0;
            }

            var count = (double)valueCount;
            var sumX = 0.0;
            var sumY = 0.0;
            var sumXy = 0.0;
            var sumX2 = 0.0;
            for (var index = 0; index < valueCount; index++)
            {
                sumX += index;
                var value = values[start + index];
                sumY += value;
                sumXy += index * value;
                sumX2 += index * index;
            }
            var denominator = count * sumX2 - sumX * sumX;
            return Math.Abs(denominator) < double.Epsilon
                ? 0
                : (count * sumXy - sumX * sumY) / denominator;
        }
    }

    internal static class ProfilerDataText
    {
        // 与最多 100 个热点、64 个线程及 120 条帧摘要共同保留充足的 1 MiB 响应余量。
        // Query/聚合始终使用完整内部字符串，只在最终 DTO 输出时截断。
        internal const int MaxNameLength = 128;
        internal const int MaxPathLength = 768;
        internal const int MaxCategoryLength = 64;
        private const string Suffix = "...[truncated]";

        internal static string Truncate(string value, int maxLength, out bool truncated)
        {
            value = value ?? "";
            truncated = value.Length > maxLength;
            if (!truncated)
            {
                return value;
            }

            var prefixLength = Math.Max(0, maxLength - Suffix.Length);
            return value.Substring(0, prefixLength) + Suffix;
        }
    }

    internal static class ProfilerDataValue
    {
        internal static double NonNegative(double value)
        {
            return double.IsNaN(value) || double.IsInfinity(value) || value < 0 ? 0 : value;
        }

        internal static double AddFinite(double left, double right)
        {
            var sum = NonNegative(left) + NonNegative(right);
            return double.IsInfinity(sum) ? double.MaxValue : sum;
        }

        internal static long ToNonNegativeInt64(double value)
        {
            if (double.IsNaN(value) || value <= 0)
            {
                return 0;
            }
            if (double.IsInfinity(value) || value >= long.MaxValue)
            {
                return long.MaxValue;
            }
            return (long)Math.Round(value, MidpointRounding.AwayFromZero);
        }

        internal static long SaturatingAdd(long left, long right)
        {
            left = Math.Max(0, left);
            right = Math.Max(0, right);
            return left > long.MaxValue - right ? long.MaxValue : left + right;
        }

        internal static double Round(double value)
        {
            return Math.Round(NonNegative(value), 6, MidpointRounding.AwayFromZero);
        }

        internal static double RoundSigned(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return 0;
            }
            return Math.Round(value, 6, MidpointRounding.AwayFromZero);
        }

        internal static double? NullableNonNegative(double value)
        {
            return double.IsNaN(value) || double.IsInfinity(value) || value < 0
                ? (double?)null
                : Round(value);
        }

        internal static double? NullablePositive(double value)
        {
            return double.IsNaN(value) || double.IsInfinity(value) || value <= 0
                ? (double?)null
                : Round(value);
        }

        internal static double? Percent(double numerator, double denominator)
        {
            if (denominator <= 0 || double.IsNaN(denominator) ||
                double.IsInfinity(denominator))
            {
                return null;
            }
            return Round(NonNegative(numerator) / denominator * 100.0);
        }
    }

    internal sealed class ProfilerHotspotResult
    {
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("nameTruncated")] public bool NameTruncated { get; set; }
        [JsonProperty("path")] public string Path { get; set; }
        [JsonProperty("pathTruncated")] public bool PathTruncated { get; set; }
        [JsonProperty("category")] public string Category { get; set; }
        [JsonProperty("categoryTruncated")] public bool CategoryTruncated { get; set; }
        [JsonProperty("framesSeen")] public int FramesSeen { get; set; }
        [JsonProperty("callCount")] public long CallCount { get; set; }
        [JsonProperty("callCountAverage")] public double CallCountAverage { get; set; }
        [JsonProperty("warningCount")] public long WarningCount { get; set; }
        [JsonProperty("totalTimeSumMs")] public double TotalTimeSumMs { get; set; }
        [JsonProperty("totalTimeAverageMs")] public double TotalTimeAverageMs { get; set; }
        [JsonProperty("totalTimePercent")] public double? TotalTimePercent { get; set; }
        [JsonProperty("selfTimeSumMs")] public double SelfTimeSumMs { get; set; }
        [JsonProperty("selfTimeAverageMs")] public double SelfTimeAverageMs { get; set; }
        [JsonProperty("selfTimePercent")] public double? SelfTimePercent { get; set; }
        [JsonProperty("maxTotalTimeMs")] public double MaxTotalTimeMs { get; set; }
        [JsonProperty("maxTotalTimeFrameIndex")] public int? MaxTotalTimeFrameIndex { get; set; }
        [JsonProperty("maxSelfTimeMs")] public double MaxSelfTimeMs { get; set; }
        [JsonProperty("maxSelfTimeFrameIndex")] public int? MaxSelfTimeFrameIndex { get; set; }
        [JsonProperty("gcAllocSumBytes")] public long GcAllocSumBytes { get; set; }
        [JsonProperty("gcAllocAverageBytes")] public double GcAllocAverageBytes { get; set; }
        [JsonProperty("details", NullValueHandling = NullValueHandling.Ignore)]
        public ProfilerHotspotDetails Details { get; set; }

        [JsonIgnore] internal string FullPath { get; set; }
        [JsonIgnore] internal string FullCategory { get; set; }
    }

    internal sealed class ProfilerHotspotDetails
    {
        [JsonProperty("metric")] public string Metric { get; set; }
        [JsonProperty("basisFrameCount")] public int BasisFrameCount { get; set; }
        [JsonProperty("missingSampleValue")] public double MissingSampleValue { get; set; }
        [JsonProperty("p95")] public double? P95 { get; set; }
        [JsonProperty("p99")] public double? P99 { get; set; }
        [JsonProperty("max")] public double Max { get; set; }
        [JsonProperty("maxFrameIndex")] public int? MaxFrameIndex { get; set; }
        [JsonProperty("longestConsecutiveFrames")] public int LongestConsecutiveFrames { get; set; }
        [JsonProperty("slowestOccurrences")]
        public ProfilerHotspotOccurrence[] SlowestOccurrences { get; set; } =
            Array.Empty<ProfilerHotspotOccurrence>();
        [JsonProperty("trendDirection")] public string TrendDirection { get; set; }
        [JsonProperty("trendSlopePerFrame")] public double TrendSlopePerFrame { get; set; }
        [JsonProperty("trend")]
        public ProfilerHotspotTrendPoint[] Trend { get; set; } =
            Array.Empty<ProfilerHotspotTrendPoint>();
    }

    internal sealed class ProfilerHotspotOccurrence
    {
        [JsonProperty("frameIndex")] public int FrameIndex { get; set; }
        [JsonProperty("value")] public double Value { get; set; }
    }

    internal sealed class ProfilerHotspotTrendPoint
    {
        [JsonProperty("frameIndex")] public int FrameIndex { get; set; }
        [JsonProperty("value")] public double Value { get; set; }
    }
}
