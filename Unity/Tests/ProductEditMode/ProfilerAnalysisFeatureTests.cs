using System;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace AgentBridge.Tests.ProductEditMode
{
    public sealed class ProfilerAnalysisFeatureTests
    {
        [Test]
        public void DataSchema_AcceptsNewSelectorsFiltersAndBoundedDetails()
        {
            var schema = new GetProfilerDataHandler().ParamsSchema;
            Assert.That(
                JsonParamsValidator.TryValidateSchema(
                    schema, out var schemaError),
                Is.True,
                schemaError);

            var valid = new JObject
            {
                ["captureId"] =
                    Guid.NewGuid().ToString("N").ToUpperInvariant(),
                ["threadSelector"] = new JObject
                {
                    ["mode"] = "all",
                    ["offset"] = 0,
                    ["maxThreads"] = 4
                },
                ["categories"] = new JArray("Scripts", "Physics"),
                ["minSelfTimeSumMs"] = 0.25,
                ["minGcAllocSumBytes"] = 1024,
                ["minCallCount"] = 2,
                ["limit"] = 25,
                ["hotspotDetails"] = new JObject
                {
                    ["metric"] = "selfTimeMs",
                    ["slowestLimit"] = 3,
                    ["trendFrameCount"] = 30,
                    ["hotspotLimit"] = 5
                }
            };
            Assert.That(
                JsonParamsValidator.TryValidate(
                    valid, schema, out var error),
                Is.True,
                error);

            var invalid = new[]
            {
                new JObject
                {
                    ["captureId"] = "not-a-capture"
                },
                new JObject
                {
                    ["threadIndex"] = 0,
                    ["threadSelector"] =
                        new JObject { ["mode"] = "index", ["index"] = 0 }
                },
                new JObject
                {
                    ["threadSelector"] =
                        new JObject { ["mode"] = "id", ["id"] = 123L }
                },
                new JObject
                {
                    ["categories"] = new JArray("")
                },
                new JObject
                {
                    ["minSelfTimeSumMs"] = -0.1
                },
                new JObject
                {
                    ["hotspotDetails"] = new JObject
                    {
                        ["trendFrameCount"] = 121
                    }
                }
            };
            foreach (var value in invalid)
            {
                Assert.That(
                    JsonParamsValidator.TryValidate(value, schema, out _),
                    Is.False,
                    value.ToString());
            }
        }

        [Test]
        public void ThreadSelector_ParsesUInt64StringWithoutJsonPrecisionLoss()
        {
            var selector = GetProfilerDataHandler.ParseThreadSelector(
                new JObject
                {
                    ["mode"] = "id",
                    ["id"] = ulong.MaxValue.ToString()
                });

            Assert.That(selector.Mode, Is.EqualTo("id"));
            Assert.That(selector.Id, Is.EqualTo(ulong.MaxValue));
        }

        [Test]
        public void Analyzer_CategoryOrAndThresholds_AreAppliedBeforeTopK()
        {
            var analyzer = new ProfilerHotspotAnalyzer();
            analyzer.RecordUnmatched(
                "Audio", "Root/Audio", "Audio");
            analyzer.RecordCategoryRejected(
                "Render", "Root/Render", "Rendering");
            analyzer.AddFrame(1, new[]
            {
                Sample("Low", "Root/Low", "Scripts", self: 1, calls: 10, gc: 100),
                Sample("Good", "Root/Good", "Scripts", self: 4, calls: 5, gc: 200),
                Sample("Physics", "Root/Physics", "Physics", self: 3, calls: 5, gc: 300)
            });

            var page = analyzer.Select(
                1,
                20,
                null,
                "selfTimeSumMs",
                10,
                categories: new[] { "scripts" },
                minSelfTimeSumMs: 2,
                minGcAllocSumBytes: 150,
                minCallCount: 5);

            Assert.That(page.UniqueCount, Is.EqualTo(5));
            Assert.That(page.QueryMatchedCount, Is.EqualTo(4));
            Assert.That(page.CategoryMatchedCount, Is.EqualTo(2));
            Assert.That(page.ThresholdMatchedCount, Is.EqualTo(1));
            Assert.That(page.MatchedCount, Is.EqualTo(1));
            Assert.That(
                page.Hotspots.Select(item => item.Name),
                Is.EqualTo(new[] { "Good" }));
        }

        [Test]
        public void Details_IncludeZeroForMissingMarkerAndReportP95P99SlowestAndTrend()
        {
            var analyzer = new ProfilerHotspotAnalyzer(trackDetails: true);
            analyzer.AddFrame(10, new[]
            {
                Sample("Work", "Root/Work", "Scripts", self: 1)
            });
            analyzer.AddFrame(12, new[]
            {
                Sample("Work", "Root/Work", "Scripts", self: 3)
            });
            analyzer.AddFrame(13, new[]
            {
                Sample("Work", "Root/Work", "Scripts", self: 5)
            });

            var result = analyzer.Select(
                    4,
                    40,
                    null,
                    "selfTimeSumMs",
                    1,
                    details: new ProfilerHotspotDetailsOptions
                    {
                        Metric = "selfTimeMs",
                        SlowestLimit = 2,
                        TrendFrameCount = 4,
                        HotspotLimit = 1
                    },
                    framesWithThread: new[] { 10, 11, 12, 13 })
                .Hotspots.Single();

            Assert.That(result.Details.BasisFrameCount, Is.EqualTo(4));
            Assert.That(
                result.Details.Trend.Select(point => point.Value),
                Is.EqualTo(new[] { 1d, 0d, 3d, 5d }));
            Assert.That(result.Details.P95, Is.EqualTo(5));
            Assert.That(result.Details.P99, Is.EqualTo(5));
            Assert.That(result.Details.MaxFrameIndex, Is.EqualTo(13));
            Assert.That(
                result.Details.SlowestOccurrences
                    .Select(point => point.FrameIndex),
                Is.EqualTo(new[] { 13, 12 }));
            Assert.That(
                result.Details.TrendDirection,
                Is.EqualTo("increasing"));
            Assert.That(result.Details.LongestConsecutiveFrames, Is.EqualTo(2));
        }

        [Test]
        public void Details_TrendDirectionUsesReturnedRecentSlice()
        {
            var analyzer = new ProfilerHotspotAnalyzer(trackDetails: true);
            var values = new[] { 100d, 90d, 80d, 1d, 2d, 3d };
            for (var index = 0; index < values.Length; index++)
            {
                analyzer.AddFrame(index + 1, new[]
                {
                    Sample(
                        "Work",
                        "Root/Work",
                        "Scripts",
                        self: values[index])
                });
            }

            var result = analyzer.Select(
                    values.Length,
                    values.Sum(),
                    null,
                    "selfTimeSumMs",
                    1,
                    details: new ProfilerHotspotDetailsOptions
                    {
                        Metric = "selfTimeMs",
                        SlowestLimit = 0,
                        TrendFrameCount = 3,
                        HotspotLimit = 1
                    },
                    framesWithThread: Enumerable.Range(1, values.Length).ToArray())
                .Hotspots.Single();

            Assert.That(
                result.Details.Trend.Select(point => point.Value),
                Is.EqualTo(new[] { 1d, 2d, 3d }));
            Assert.That(result.Details.TrendDirection, Is.EqualTo("increasing"));
            Assert.That(result.Details.TrendSlopePerFrame, Is.EqualTo(1));
        }

        [Test]
        public void Analyzer_SelfTimeP95TopKUsesDistributionNotSingleSpike()
        {
            var analyzer = new ProfilerHotspotAnalyzer(trackDetails: true);
            for (var frameIndex = 1; frameIndex <= 20; frameIndex++)
            {
                analyzer.AddFrame(
                    frameIndex,
                    frameIndex == 1
                        ? new[]
                        {
                            Sample(
                                "Spike",
                                "Root/Spike",
                                "Scripts",
                                self: 100),
                            Sample(
                                "Sustained",
                                "Root/Sustained",
                                "Scripts",
                                self: 5)
                        }
                        : new[]
                        {
                            Sample(
                                "Sustained",
                                "Root/Sustained",
                                "Scripts",
                                self: 5)
                        });
            }

            var result = analyzer.Select(
                    analyzedFrameCount: 20,
                    analyzedFrameTimeSumMs: 400,
                    query: null,
                    sortBy: "selfTimeP95Ms",
                    limit: 1,
                    details: new ProfilerHotspotDetailsOptions
                    {
                        Metric = "selfTimeMs",
                        SlowestLimit = 0,
                        TrendFrameCount = 0,
                        HotspotLimit = 1
                    },
                    framesWithThread: Enumerable.Range(1, 20).ToArray())
                .Hotspots.Single();

            Assert.That(result.Name, Is.EqualTo("Sustained"));
            Assert.That(result.Details.P95, Is.EqualTo(5));
        }

        [Test]
        public void QueryValidation_EnforcesGlobalMultiThreadTrendBudget()
        {
            var oversized = QueryOptionsForDetailBudget(
                mode: "all",
                maxThreads: 16,
                limit: 6,
                hotspotLimit: 5,
                trendFrameCount: 120);
            var error = Assert.Throws<CommandException>(
                () => ProfilerDataReader.ValidateOptions(oversized));
            Assert.That(error.Code, Is.EqualTo(ErrorCodes.InvalidParams));

            var bounded = QueryOptionsForDetailBudget(
                mode: "all",
                maxThreads: 4,
                limit: 25,
                hotspotLimit: 5,
                trendFrameCount: 30);
            Assert.DoesNotThrow(
                () => ProfilerDataReader.ValidateOptions(bounded));

            // index/id 始终只可能返回一个线程，不应被 parser 的默认
            // maxThreads=4 误判为 4 个线程。
            var exactIndex = QueryOptionsForDetailBudget(
                mode: "index",
                maxThreads: 4,
                limit: 100,
                hotspotLimit: 5,
                trendFrameCount: 120);
            Assert.DoesNotThrow(
                () => ProfilerDataReader.ValidateOptions(exactIndex));
        }

        [Test]
        public void CompareSchema_IsValidAndRequiresBothWindows()
        {
            var schema = new CompareProfilerWindowsHandler().ParamsSchema;
            Assert.That(
                JsonParamsValidator.TryValidateSchema(
                    schema, out var schemaError),
                Is.True,
                schemaError);
            Assert.That(
                JsonParamsValidator.TryValidate(
                    new JObject
                    {
                        ["baseline"] = new JObject
                        {
                            ["captureId"] =
                                Guid.NewGuid().ToString("N").ToUpperInvariant(),
                            ["frameCount"] = 30
                        },
                        ["candidate"] = new JObject
                        {
                            ["captureId"] = Guid.NewGuid().ToString("N"),
                            ["frameCount"] = 30
                        },
                        ["threadSelector"] = new JObject
                        {
                            ["mode"] = "name",
                            ["name"] = "Main Thread"
                        },
                        ["metric"] = "selfTimeAverageMs"
                    },
                    schema,
                    out var error),
                Is.True,
                error);
            Assert.That(
                JsonParamsValidator.TryValidate(
                    new JObject { ["baseline"] = new JObject() },
                    schema,
                    out _),
                Is.False);
            Assert.That(
                JsonParamsValidator.TryValidate(
                    new JObject
                    {
                        ["baseline"] =
                            new JObject { ["captureId"] = "bad" },
                        ["candidate"] = new JObject()
                    },
                    schema,
                    out _),
                Is.False);
        }

        [Test]
        public void WindowDiffer_OuterJoinsExactPathAndReportsSignedDeltas()
        {
            var baseline = Result(
                "capture",
                Hotspot("Same", "Root/Same", "Scripts", 2),
                Hotspot("Removed", "Root/Removed", "Scripts", 4));
            var candidate = Result(
                "capture",
                Hotspot("Same", "Root/Same", "Scripts", 3),
                Hotspot("New", "Root/New", "Scripts", 1));

            var result = ProfilerWindowDiffer.Diff(
                baseline,
                candidate,
                new ProfilerCompareOptions
                {
                    Metric = "selfTimeAverageMs",
                    Direction = "all",
                    MinDeltaPercent = 0,
                    Limit = 10
                },
                candidateLimit: 500);

            Assert.That(result.Summary.Regressed, Is.EqualTo(1));
            Assert.That(result.Summary.New, Is.EqualTo(1));
            Assert.That(result.Summary.Removed, Is.EqualTo(1));
            Assert.That(
                result.HotspotDiffs.Single(item => item.Name == "Same")
                    .PercentDelta,
                Is.EqualTo(50));
            Assert.That(
                result.HotspotDiffs.Single(item => item.Name == "Removed")
                    .AbsoluteDelta,
                Is.EqualTo(-4));
            Assert.That(
                result.HotspotDiffs.Single(item => item.Name == "New")
                    .PercentDelta,
                Is.Null);
        }

        [Test]
        public void WindowDiffer_TruncatedOppositeSets_DoNotGuessNewOrRemoved()
        {
            var baseline = Result(
                "capture",
                Hotspot("Shared", "Root/Shared", "Scripts", 2),
                Hotspot("BaselineOnly", "Root/BaselineOnly", "Scripts", 4));
            baseline.MatchedHotspots = baseline.Returned + 1;
            var candidate = Result(
                "capture",
                Hotspot("Shared", "Root/Shared", "Scripts", 3),
                Hotspot("CandidateOnly", "Root/CandidateOnly", "Scripts", 5));
            candidate.MatchedHotspots = candidate.Returned + 1;

            var result = ProfilerWindowDiffer.Diff(
                baseline,
                candidate,
                new ProfilerCompareOptions
                {
                    Metric = "selfTimeAverageMs",
                    Direction = "all",
                    MinDeltaPercent = 0,
                    Limit = 10
                },
                candidateLimit: 2);

            Assert.That(result.CandidateSetTruncated, Is.True);
            Assert.That(result.BaselineCandidatesTruncated, Is.True);
            Assert.That(result.CandidateCandidatesTruncated, Is.True);
            Assert.That(result.IndeterminateOneSidedHotspots, Is.EqualTo(2));
            Assert.That(result.Summary.New, Is.Zero);
            Assert.That(result.Summary.Removed, Is.Zero);
            Assert.That(
                result.HotspotDiffs.Select(item => item.Name),
                Is.EqualTo(new[] { "Shared" }));
        }

        [Test]
        public void WindowDiffer_ZeroBaselineRegression_PassesPositivePercentFilter()
        {
            var baseline = Result(
                "capture",
                Hotspot("Work", "Root/Work", "Scripts", 0));
            var candidate = Result(
                "capture",
                Hotspot("Work", "Root/Work", "Scripts", 1));

            var result = ProfilerWindowDiffer.Diff(
                baseline,
                candidate,
                new ProfilerCompareOptions
                {
                    Metric = "selfTimeAverageMs",
                    Direction = "regressions",
                    MinDeltaPercent = 1000,
                    Limit = 10
                },
                candidateLimit: 10);

            Assert.That(result.HotspotDiffs, Has.Length.EqualTo(1));
            Assert.That(result.HotspotDiffs[0].Status, Is.EqualTo("regressed"));
            Assert.That(result.HotspotDiffs[0].AbsoluteDelta, Is.EqualTo(1));
            Assert.That(result.HotspotDiffs[0].PercentDelta, Is.Null);
        }

        [Test]
        public void MaximumBoundedMultiThreadResult_FitsResponseBudget()
        {
            var escapedName =
                new string('\u0001', ProfilerDataText.MaxNameLength);
            var escapedPath =
                new string('\u0001', ProfilerDataText.MaxPathLength);
            var escapedCategory =
                new string('\u0001', ProfilerDataText.MaxCategoryLength);
            var trend = Enumerable.Range(0, 30)
                .Select(index => new ProfilerHotspotTrendPoint
                {
                    FrameIndex = int.MaxValue - index,
                    Value = double.MaxValue
                })
                .ToArray();
            var threadResults = Enumerable.Range(0, 4)
                .Select(threadIndex => new ProfilerThreadDataResult
                {
                    Thread = new ProfilerThreadInfo
                    {
                        Index = threadIndex,
                        ThreadId = (ulong)threadIndex,
                        ThreadIdString = threadIndex.ToString(),
                        Name = escapedName,
                        Group = escapedCategory,
                        SampleCount = int.MaxValue
                    },
                    Hotspots = Enumerable.Range(0, 25)
                        .Select(hotspotIndex => new ProfilerHotspotResult
                        {
                            Name = escapedName,
                            Path = escapedPath,
                            Category = escapedCategory,
                            CallCount = long.MaxValue,
                            SelfTimeSumMs = double.MaxValue,
                            TotalTimeSumMs = double.MaxValue,
                            GcAllocSumBytes = long.MaxValue,
                            Details = hotspotIndex < 5
                                ? new ProfilerHotspotDetails
                                {
                                    Metric = "selfTimeMs",
                                    BasisFrameCount = 120,
                                    P95 = double.MaxValue,
                                    P99 = double.MaxValue,
                                    Max = double.MaxValue,
                                    TrendDirection = "increasing",
                                    TrendSlopePerFrame = double.MaxValue,
                                    Trend = trend
                                }
                                : null
                        })
                        .ToArray(),
                    Returned = 25,
                    MatchedHotspots = 25
                })
                .ToArray();
            var result = new ProfilerMultiThreadDataResult
            {
                Available = true,
                Capture = new ProfilerCaptureInfo
                {
                    CaptureId = new string('f', 32),
                    Source = "saved",
                    Immutable = true
                },
                Selection = new ProfilerSelectionInfo
                {
                    RequestedFrameCount = 120,
                    FrameCount = 120,
                    FirstFrameIndex = 1,
                    EndFrameIndex = 120
                },
                Threads = threadResults.Select(item => item.Thread).ToArray(),
                ThreadSelection = new ProfilerThreadSelectionInfo
                {
                    Mode = "all",
                    Matched = 4,
                    Returned = 4
                },
                Frames = Enumerable.Range(1, 120)
                    .Select(index => new ProfilerFrameSummary
                    {
                        FrameIndex = index,
                        FrameTimeMs = double.MaxValue,
                        FrameGpuTimeMs = double.MaxValue,
                        Fps = double.MaxValue
                    })
                    .ToArray(),
                ThreadResults = threadResults
            };

            var json = JsonConvert.SerializeObject(result, Formatting.None);
            Assert.That(
                Encoding.UTF8.GetByteCount(json),
                Is.LessThan(FileChannel.MaxFileBytes));
        }

        [Test]
        public void MaximumAllowedThreadFanoutWithDetails_FitsResponseBudget()
        {
            var escapedName =
                new string('\u0001', ProfilerDataText.MaxNameLength);
            var escapedPath =
                new string('\u0001', ProfilerDataText.MaxPathLength);
            var escapedCategory =
                new string('\u0001', ProfilerDataText.MaxCategoryLength);
            var trend = Enumerable.Range(0, 6)
                .Select(index => new ProfilerHotspotTrendPoint
                {
                    FrameIndex = int.MaxValue - index,
                    Value = double.MaxValue
                })
                .ToArray();
            var occurrences = Enumerable.Range(0, 5)
                .Select(index => new ProfilerHotspotOccurrence
                {
                    FrameIndex = int.MaxValue - index,
                    Value = double.MaxValue
                })
                .ToArray();
            var catalog = Enumerable.Range(
                    0, ProfilerDataReader.MaxReturnedThreads)
                .Select(index => new ProfilerThreadInfo
                {
                    Index = index,
                    ThreadId = ulong.MaxValue - (ulong)index,
                    ThreadIdString =
                        (ulong.MaxValue - (ulong)index).ToString(),
                    Name = escapedName,
                    Group = escapedCategory,
                    SampleCount = int.MaxValue
                })
                .ToArray();
            var threadResults = Enumerable.Range(0, 16)
                .Select(threadIndex => new ProfilerThreadDataResult
                {
                    Thread = catalog[threadIndex],
                    FramesWithThread = 120,
                    ScannedSamples = int.MaxValue,
                    UniqueHotspots = int.MaxValue,
                    QueryMatchedHotspots = int.MaxValue,
                    CategoryMatchedHotspots = int.MaxValue,
                    ThresholdMatchedHotspots = int.MaxValue,
                    MatchedHotspots = 6,
                    Returned = 6,
                    Hotspots = Enumerable.Range(0, 6)
                        .Select(_ => new ProfilerHotspotResult
                        {
                            Name = escapedName,
                            Path = escapedPath,
                            Category = escapedCategory,
                            FramesSeen = 120,
                            CallCount = long.MaxValue,
                            CallCountAverage = double.MaxValue,
                            WarningCount = long.MaxValue,
                            SelfTimeSumMs = double.MaxValue,
                            SelfTimeAverageMs = double.MaxValue,
                            TotalTimeSumMs = double.MaxValue,
                            TotalTimeAverageMs = double.MaxValue,
                            GcAllocSumBytes = long.MaxValue,
                            GcAllocAverageBytes = double.MaxValue,
                            Details = new ProfilerHotspotDetails
                            {
                                Metric = "selfTimeMs",
                                BasisFrameCount = 120,
                                P95 = double.MaxValue,
                                P99 = double.MaxValue,
                                Max = double.MaxValue,
                                MaxFrameIndex = int.MaxValue,
                                LongestConsecutiveFrames = 120,
                                SlowestOccurrences = occurrences,
                                TrendDirection = "increasing",
                                TrendSlopePerFrame = double.MaxValue,
                                Trend = trend
                            }
                        })
                        .ToArray()
                })
                .ToArray();
            var result = new ProfilerMultiThreadDataResult
            {
                Available = true,
                Capture = new ProfilerCaptureInfo
                {
                    CaptureId = new string('f', 32),
                    Source = "saved",
                    Immutable = true,
                    ConnectedProfiler = int.MaxValue,
                    LastFrameIndexAtStart = int.MaxValue
                },
                Selection = new ProfilerSelectionInfo
                {
                    RequestedFrameCount = 120,
                    FrameCount = 120,
                    FirstFrameIndex = 1,
                    EndFrameIndex = 120,
                    Complete = true,
                    HasMoreOlderFrames = true,
                    SessionBoundaryChecked = true,
                    ViewMode = "mergedHierarchyWithoutEditorOnly",
                    MaxDepth = 128,
                    FramesWithThread = 120
                },
                Threads = catalog,
                ThreadsTruncated = true,
                ThreadSelection = new ProfilerThreadSelectionInfo
                {
                    Mode = "all",
                    Matched = int.MaxValue,
                    Returned = 16,
                    Truncated = true,
                    NextOffset = 16
                },
                FrameStats = new ProfilerFrameStats
                {
                    MeanFrameTimeMs = double.MaxValue,
                    P50FrameTimeMs = double.MaxValue,
                    P95FrameTimeMs = double.MaxValue,
                    P99FrameTimeMs = double.MaxValue,
                    MaxFrameTimeMs = double.MaxValue,
                    MaxFrameIndex = int.MaxValue
                },
                Frames = Enumerable.Range(1, 120)
                    .Select(index => new ProfilerFrameSummary
                    {
                        FrameIndex = index,
                        FrameTimeMs = double.MaxValue,
                        FrameGpuTimeMs = double.MaxValue,
                        Fps = double.MaxValue
                    })
                    .ToArray(),
                ScannedSamples = int.MaxValue,
                ThreadResults = threadResults
            };

            var json = JsonConvert.SerializeObject(result, Formatting.None);
            Assert.That(
                Encoding.UTF8.GetByteCount(json),
                Is.LessThan(FileChannel.MaxFileBytes));
        }

        private static ProfilerFrameHotspot Sample(
            string name,
            string path,
            string category,
            double self,
            long calls = 0,
            long gc = 0)
        {
            return new ProfilerFrameHotspot
            {
                Name = name,
                Path = path,
                Category = category,
                SelfTimeMs = self,
                TotalTimeMs = self,
                CallCount = calls,
                GcAllocBytes = gc
            };
        }

        private static ProfilerQueryOptions QueryOptionsForDetailBudget(
            string mode,
            int maxThreads,
            int limit,
            int hotspotLimit,
            int trendFrameCount)
        {
            return new ProfilerQueryOptions
            {
                FrameCount = 30,
                ThreadIndex = 0,
                ThreadSelector = new ProfilerThreadSelector
                {
                    Mode = mode,
                    Index = 0,
                    MaxThreads = maxThreads
                },
                SortBy = "selfTimeSumMs",
                MaxDepth = 64,
                Limit = limit,
                HotspotDetails = new ProfilerHotspotDetailsOptions
                {
                    Metric = "selfTimeMs",
                    SlowestLimit = 3,
                    TrendFrameCount = trendFrameCount,
                    HotspotLimit = hotspotLimit
                }
            };
        }

        private static ProfilerHotspotResult Hotspot(
            string name,
            string path,
            string category,
            double selfAverage)
        {
            return new ProfilerHotspotResult
            {
                Name = name,
                Path = path,
                FullPath = path,
                Category = category,
                FullCategory = category,
                SelfTimeAverageMs = selfAverage
            };
        }

        private static ProfilerDataResult Result(
            string captureId,
            params ProfilerHotspotResult[] hotspots)
        {
            return new ProfilerDataResult
            {
                Available = true,
                Capture = new ProfilerCaptureInfo
                {
                    CaptureId = captureId
                },
                Selection = new ProfilerSelectionInfo
                {
                    FrameCount = 30,
                    FirstFrameIndex = 1,
                    EndFrameIndex = 30
                },
                FrameStats = new ProfilerFrameStats(),
                Hotspots = hotspots,
                MatchedHotspots = hotspots.Length,
                Returned = hotspots.Length
            };
        }
    }
}
