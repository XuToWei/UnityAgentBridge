using System;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace AgentBridge.Tests.ProductEditMode
{
    public sealed class ProfilerDataTests
    {
        [Test]
        public void HandlerMetadataAndSchema_DescribeBoundedReadOnlyAggregation()
        {
            var handler = new GetProfilerDataHandler();

            Assert.That(handler.Command, Is.EqualTo("get_profiler_data"));
            Assert.That(handler.Group, Is.EqualTo("Profiling"));
            Assert.That(handler.CanDisable, Is.True);
            Assert.That(handler.BatchMode, Is.EqualTo(CommandBatchMode.Allowed));
            Assert.That(JsonParamsValidator.TryValidateSchema(
                handler.ParamsSchema, out var schemaError), Is.True, schemaError);

            var schema = handler.ParamsSchema;
            Assert.That(schema["additionalProperties"]?.Value<bool>(), Is.False);
            Assert.That(schema["properties"]?["frameCount"]?["default"]?.Value<int>(),
                Is.EqualTo(ProfilerQueryOptions.DefaultFrameCount));
            Assert.That(schema["properties"]?["frameCount"]?["minimum"]?.Value<int>(),
                Is.EqualTo(1));
            Assert.That(schema["properties"]?["frameCount"]?["maximum"]?.Value<int>(),
                Is.EqualTo(120));
            Assert.That(schema["properties"]?["limit"]?["maximum"]?.Value<int>(),
                Is.EqualTo(100));
            CollectionAssert.AreEqual(
                new[]
                {
                    "selfTimeSumMs", "totalTimeSumMs",
                    "maxSelfTimeMs", "gcAllocSumBytes"
                },
                schema["properties"]?["sortBy"]?["enum"]?.Values<string>().ToArray());
        }

        [Test]
        public void ParamsSchema_RejectsInvalidBoundsEnumsTypesAndUnknownFields()
        {
            var schema = new GetProfilerDataHandler().ParamsSchema;
            var invalid = new[]
            {
                new JObject { ["captureId"] = "not-a-capture" },
                new JObject { ["frameCount"] = 0 },
                new JObject { ["frameCount"] = 121 },
                new JObject { ["frameCount"] = "1" },
                new JObject { ["endFrameIndex"] = (long)int.MaxValue + 1 },
                new JObject { ["sortBy"] = "calls" },
                new JObject { ["threadIndex"] = -1 },
                new JObject { ["maxDepth"] = 129 },
                new JObject { ["limit"] = 0 },
                new JObject { ["limit"] = 101 },
                new JObject { ["query"] = new string('q', 257) },
                new JObject { ["unexpected"] = true }
            };

            foreach (var value in invalid)
            {
                Assert.That(JsonParamsValidator.TryValidate(value, schema, out _),
                    Is.False, value.ToString());
            }

            Assert.That(JsonParamsValidator.TryValidate(new JObject(), schema, out var error),
                Is.True, error);
            Assert.That(JsonParamsValidator.TryValidate(
                new JObject
                {
                    ["captureId"] = Guid.NewGuid().ToString("N").ToUpperInvariant(),
                    ["frameCount"] = 1,
                    ["sortBy"] = "gcAllocSumBytes",
                    ["limit"] = 100
                },
                schema,
                out error), Is.True, error);
        }

        [Test]
        public void Analyzer_AggregatesPerFrameDuplicatesAndCrossFrameSumAverageAndMax()
        {
            var analyzer = new ProfilerHotspotAnalyzer();
            analyzer.AddFrame(10, new[]
            {
                Sample("Update", "PlayerLoop/Update", "Scripts",
                    total: 6, self: 4, calls: 2, gc: 100, warnings: 1),
                Sample("Update", "PlayerLoop/Update", "Scripts",
                    total: 2, self: 1, calls: 3, gc: 20, warnings: 2),
                Sample("Rare", "PlayerLoop/Rare", "Scripts",
                    total: 4, self: 3, calls: 1, gc: 10)
            });
            analyzer.AddFrame(11, new[]
            {
                Sample("Update", "PlayerLoop/Update", "Scripts",
                    total: 4, self: 2, calls: 1, gc: 30)
            });

            var page = analyzer.Select(
                analyzedFrameCount: 2,
                analyzedFrameTimeSumMs: 40,
                query: "update",
                sortBy: "selfTimeSumMs",
                limit: 10);
            var result = page.Hotspots.Single();

            Assert.That(page.UniqueCount, Is.EqualTo(2));
            Assert.That(page.MatchedCount, Is.EqualTo(1));
            Assert.That(result.FramesSeen, Is.EqualTo(2));
            Assert.That(result.CallCount, Is.EqualTo(6));
            Assert.That(result.WarningCount, Is.EqualTo(3));
            Assert.That(result.TotalTimeSumMs, Is.EqualTo(12).Within(0.000001));
            Assert.That(result.TotalTimeAverageMs, Is.EqualTo(6).Within(0.000001));
            Assert.That(result.TotalTimePercent, Is.EqualTo(30).Within(0.000001));
            Assert.That(result.SelfTimeSumMs, Is.EqualTo(7).Within(0.000001));
            Assert.That(result.SelfTimeAverageMs, Is.EqualTo(3.5).Within(0.000001));
            Assert.That(result.SelfTimePercent, Is.EqualTo(17.5).Within(0.000001));
            Assert.That(result.MaxTotalTimeMs, Is.EqualTo(8).Within(0.000001));
            Assert.That(result.MaxTotalTimeFrameIndex, Is.EqualTo(10));
            Assert.That(result.MaxSelfTimeMs, Is.EqualTo(5).Within(0.000001));
            Assert.That(result.MaxSelfTimeFrameIndex, Is.EqualTo(10));
            Assert.That(result.GcAllocSumBytes, Is.EqualTo(150));
            Assert.That(result.GcAllocAverageBytes, Is.EqualTo(75).Within(0.000001));

            var rare = analyzer.Select(2, 40, "rare", "selfTimeSumMs", 10)
                .Hotspots.Single();
            Assert.That(rare.FramesSeen, Is.EqualTo(1));
            Assert.That(rare.SelfTimeSumMs, Is.EqualTo(3).Within(0.000001));
            Assert.That(rare.SelfTimeAverageMs, Is.EqualTo(1.5).Within(0.000001),
                "per-frame averages must use all analyzed frames, not only framesSeen");
        }

        [Test]
        public void Analyzer_DoesNotMergeSameNameAtDifferentPathsOrCategories()
        {
            var analyzer = new ProfilerHotspotAnalyzer();
            analyzer.AddFrame(1, new[]
            {
                Sample("Update", "Root/A/Update", "Scripts", self: 3),
                Sample("Update", "Root/B/Update", "Scripts", self: 2),
                Sample("Update", "Root/A/Update", "Internal", self: 1)
            });

            var page = analyzer.Select(1, 10, "update", "selfTimeSumMs", 10);

            Assert.That(page.UniqueCount, Is.EqualTo(3));
            Assert.That(page.MatchedCount, Is.EqualTo(3));
            Assert.That(page.Hotspots
                    .Select(item => $"{item.Path}\u001f{item.Category}")
                    .Distinct()
                    .Count(),
                Is.EqualTo(3));
        }

        [Test]
        public void Analyzer_QueryMatchesNameOrPathIgnoringCase()
        {
            var analyzer = new ProfilerHotspotAnalyzer();
            analyzer.AddFrame(1, new[]
            {
                Sample("RenderLoop", "Player/Rendering", "Render", self: 3),
                Sample("FixedUpdate", "Player/Systems/PhysicsStep", "Scripts", self: 2),
                Sample("Audio", "Player/Audio", "Audio", self: 1)
            });

            var byName = analyzer.Select(1, 10, "RENDERLOOP", "selfTimeSumMs", 10);
            var byPath = analyzer.Select(1, 10, "physicsstep", "selfTimeSumMs", 10);

            Assert.That(byName.Hotspots.Select(item => item.Name),
                Is.EqualTo(new[] { "RenderLoop" }));
            Assert.That(byPath.Hotspots.Select(item => item.Name),
                Is.EqualTo(new[] { "FixedUpdate" }));
        }

        [Test]
        public void Analyzer_EarlyQueryPreservesCountsWithoutAggregatingUnmatchedSamples()
        {
            var analyzer = new ProfilerHotspotAnalyzer();
            analyzer.RecordUnmatched("Audio", "Player/Audio", "Audio");
            analyzer.RecordUnmatched("Audio", "Player/Audio", "Audio");
            analyzer.RecordUnmatched(
                "PhysicsStep", "Player/Systems/PhysicsStep", "Scripts");
            analyzer.AddSample(
                1,
                "PhysicsStep",
                "Player/Systems/PhysicsStep",
                "Scripts",
                4,
                3,
                2,
                64,
                1);

            var page = analyzer.Select(
                analyzedFrameCount: 1,
                analyzedFrameTimeSumMs: 10,
                query: null,
                sortBy: "selfTimeSumMs",
                limit: 10);

            Assert.That(analyzer.AggregatedCount, Is.EqualTo(1),
                "unmatched samples should not allocate full aggregate state");
            Assert.That(page.UniqueCount, Is.EqualTo(2),
                "unique count must include the early-rejected hotspot exactly once");
            Assert.That(page.MatchedCount, Is.EqualTo(1));
            Assert.That(page.Hotspots.Select(item => item.Name),
                Is.EqualTo(new[] { "PhysicsStep" }));
        }

        [Test]
        public void Analyzer_AllSortModesUseAggregateMetricDescending()
        {
            var analyzer = new ProfilerHotspotAnalyzer();
            analyzer.AddFrame(1, new[]
            {
                Sample("A", "A", "Scripts", total: 10, self: 5, gc: 100),
                Sample("B", "B", "Scripts", total: 15, self: 8, gc: 100),
                Sample("C", "C", "Scripts", total: 5, self: 6, gc: 300)
            });
            analyzer.AddFrame(2, new[]
            {
                Sample("A", "A", "Scripts", total: 10, self: 5),
                Sample("B", "B", "Scripts", total: 15, self: 0, gc: 100),
                Sample("C", "C", "Scripts", total: 5, self: 0)
            });

            AssertOrder(analyzer, "selfTimeSumMs", "A", "B", "C");
            AssertOrder(analyzer, "totalTimeSumMs", "B", "A", "C");
            AssertOrder(analyzer, "maxSelfTimeMs", "B", "C", "A");
            AssertOrder(analyzer, "gcAllocSumBytes", "C", "B", "A");
        }

        [Test]
        public void Analyzer_SortTiesUsePathThenCategoryOrdinally()
        {
            var analyzer = new ProfilerHotspotAnalyzer();
            analyzer.AddFrame(1, new[]
            {
                Sample("B", "b", "Scripts", self: 1),
                Sample("Z", "a", "ZCategory", self: 1),
                Sample("A", "a", "ACategory", self: 1)
            });

            var result = analyzer.Select(1, 10, null, "selfTimeSumMs", 10).Hotspots;

            Assert.That(result.Select(item => $"{item.Path}:{item.Category}"), Is.EqualTo(
                new[] { "a:ACategory", "a:ZCategory", "b:Scripts" }));
        }

        [TestCase("selfTimeSumMs")]
        [TestCase("totalTimeSumMs")]
        [TestCase("maxSelfTimeMs")]
        [TestCase("gcAllocSumBytes")]
        public void Analyzer_TopKMatchesDeterministicFullSortReference(string sortBy)
        {
            const int count = 41;
            const int limit = 9;
            var names = new string[count];
            var paths = new string[count];
            var categories = new string[count];
            var metrics = new double[count];
            var firstFrame = new ProfilerFrameHotspot[count];
            var secondFrame = new ProfilerFrameHotspot[count];

            for (var index = 0; index < count; index++)
            {
                names[index] = $"Sample{index:D2}";
                paths[index] = $"Root/Pair{index / 2:D2}";
                categories[index] = index % 2 == 0 ? "ACategory" : "ZCategory";

                var firstTotal = (index * 5) % 9;
                var secondTotal = (index * 2) % 7;
                var firstSelf = (index * 3) % 6;
                var secondSelf = (index * 7) % 6;
                var firstGc = ((index * 11) % 8) * 100L;
                var secondGc = ((index * 13) % 5) * 100L;
                firstFrame[index] = Sample(
                    names[index], paths[index], categories[index],
                    total: firstTotal, self: firstSelf, gc: firstGc);
                secondFrame[index] = Sample(
                    names[index], paths[index], categories[index],
                    total: secondTotal, self: secondSelf, gc: secondGc);

                switch (sortBy)
                {
                    case "totalTimeSumMs":
                        metrics[index] = firstTotal + secondTotal;
                        break;
                    case "maxSelfTimeMs":
                        metrics[index] = Math.Max(firstSelf, secondSelf);
                        break;
                    case "gcAllocSumBytes":
                        metrics[index] = firstGc + secondGc;
                        break;
                    default:
                        metrics[index] = firstSelf + secondSelf;
                        break;
                }
            }

            var analyzer = new ProfilerHotspotAnalyzer();
            analyzer.AddFrame(100, firstFrame);
            analyzer.AddFrame(101, secondFrame);
            var expected = Enumerable.Range(0, count)
                .OrderByDescending(index => metrics[index])
                .ThenBy(index => paths[index], StringComparer.Ordinal)
                .ThenBy(index => categories[index], StringComparer.Ordinal)
                .Take(limit)
                .Select(index => names[index])
                .ToArray();

            for (var repetition = 0; repetition < 3; repetition++)
            {
                var actual = analyzer.Select(
                        analyzedFrameCount: 2,
                        analyzedFrameTimeSumMs: 40,
                        query: null,
                        sortBy: sortBy,
                        limit: limit)
                    .Hotspots.Select(item => item.Name)
                    .ToArray();

                Assert.That(actual, Is.EqualTo(expected),
                    $"{sortBy}, repetition {repetition}");
            }
        }

        [Test]
        public void Analyzer_LimitPreservesCountsAndExposesTruncationInputs()
        {
            var analyzer = new ProfilerHotspotAnalyzer();
            analyzer.AddFrame(1, new[]
            {
                Sample("A", "A", "Scripts", self: 3),
                Sample("B", "B", "Scripts", self: 2),
                Sample("C", "C", "Scripts", self: 1)
            });

            var page = analyzer.Select(1, 10, null, "selfTimeSumMs", 2);

            Assert.That(page.UniqueCount, Is.EqualTo(3));
            Assert.That(page.MatchedCount, Is.EqualTo(3));
            Assert.That(page.Hotspots, Has.Length.EqualTo(2));
            Assert.That(page.MatchedCount, Is.GreaterThan(page.Hotspots.Length));
        }

        [Test]
        public void Analyzer_FrameCountOneMakesSumAverageAndMaxIdentical()
        {
            var analyzer = new ProfilerHotspotAnalyzer();
            analyzer.AddFrame(42, new[]
            {
                Sample("OneFrame", "Root/OneFrame", "Scripts",
                    total: 7.25, self: 3.5, gc: 9)
            });

            var result = analyzer.Select(1, 10, null, "selfTimeSumMs", 10)
                .Hotspots.Single();

            Assert.That(result.TotalTimeAverageMs, Is.EqualTo(result.TotalTimeSumMs));
            Assert.That(result.MaxTotalTimeMs, Is.EqualTo(result.TotalTimeSumMs));
            Assert.That(result.SelfTimeAverageMs, Is.EqualTo(result.SelfTimeSumMs));
            Assert.That(result.MaxSelfTimeMs, Is.EqualTo(result.SelfTimeSumMs));
            Assert.That(result.GcAllocAverageBytes, Is.EqualTo(result.GcAllocSumBytes));
            Assert.That(result.MaxTotalTimeFrameIndex, Is.EqualTo(42));
            Assert.That(result.MaxSelfTimeFrameIndex, Is.EqualTo(42));
        }

        [Test]
        public void FrameNavigator_UsesPreviousFrameLinksInsteadOfIntegerArithmetic()
        {
            var previous = new System.Collections.Generic.Dictionary<int, int>
            {
                [105] = 103,
                [103] = 98,
                [98] = -1
            };

            var window = ProfilerFrameNavigator.Collect(
                endFrameIndex: 105,
                firstFrameIndex: 98,
                requestedCount: 4,
                getPreviousFrameIndex: frame => previous[frame],
                areSameSession: (left, right) => true);

            Assert.That(window.IndicesNewestFirst, Is.EqualTo(new[] { 105, 103, 98 }));
            Assert.That(window.StopReason, Is.EqualTo("captureStart"));
            Assert.That(window.HasMoreOlderFrames, Is.False);
            Assert.That(window.SessionBoundaryChecked, Is.True);
        }

        [Test]
        public void FrameNavigator_StopsBeforeCrossingSessionBoundary()
        {
            var window = ProfilerFrameNavigator.Collect(
                endFrameIndex: 20,
                firstFrameIndex: 10,
                requestedCount: 3,
                getPreviousFrameIndex: frame => frame == 20 ? 17 : 10,
                areSameSession: (left, right) => false);

            Assert.That(window.IndicesNewestFirst, Is.EqualTo(new[] { 20 }));
            Assert.That(window.StopReason, Is.EqualTo("sessionBoundary"));
            Assert.That(window.HasMoreOlderFrames, Is.False);
            Assert.That(window.SessionBoundaryChecked, Is.True);
        }

        [Test]
        public void FrameNavigator_MarksUnknownSessionChecksWithoutDroppingFrames()
        {
            var window = ProfilerFrameNavigator.Collect(
                endFrameIndex: 20,
                firstFrameIndex: 10,
                requestedCount: 2,
                getPreviousFrameIndex: frame => frame == 20 ? 17 : 10,
                areSameSession: (left, right) => null);

            Assert.That(window.IndicesNewestFirst, Is.EqualTo(new[] { 20, 17 }));
            Assert.That(window.StopReason, Is.EqualTo("requestedCount"));
            Assert.That(window.HasMoreOlderFrames, Is.True);
            Assert.That(window.SessionBoundaryChecked, Is.False);
        }

        [Test]
        public void FrameStatistics_IgnoreUnavailableValuesAndUseNearestRankPercentiles()
        {
            var stats = ProfilerFrameStatistics.Create(new[]
            {
                Frame(1, 40),
                Frame(2, null),
                Frame(3, 10),
                Frame(4, 30),
                Frame(5, 20)
            });

            Assert.That(stats.MeanFrameTimeMs, Is.EqualTo(25).Within(0.000001));
            Assert.That(stats.P50FrameTimeMs, Is.EqualTo(20).Within(0.000001));
            Assert.That(stats.P95FrameTimeMs, Is.EqualTo(40).Within(0.000001));
            Assert.That(stats.MaxFrameTimeMs, Is.EqualTo(40).Within(0.000001));
            Assert.That(stats.MaxFrameIndex, Is.EqualTo(1));
        }

        [Test]
        public void UnavailableResult_PreservesCaptureMetadataAndReturnsEmptyCollections()
        {
            var capture = new ProfilerCaptureInfo
            {
                Recording = false,
                ProfileEditor = true,
                DeepProfiling = false,
                ConnectedProfiler = 7,
                FirstFrameIndexAtStart = -1,
                LastFrameIndexAtStart = -1
            };

            var result = ProfilerDataResult.CreateUnavailable(capture);

            Assert.That(result.Available, Is.False);
            Assert.That(result.Capture, Is.SameAs(capture));
            Assert.That(result.Threads, Is.Empty);
            Assert.That(result.Frames, Is.Empty);
            Assert.That(result.Hotspots, Is.Empty);
        }

        [Test]
        public void MaximumBoundedResult_FitsFileChannelResponseBudget()
        {
            var marker = new string('\u0001', 1000);
            var analyzer = new ProfilerHotspotAnalyzer();
            analyzer.AddFrame(1, Enumerable.Range(0, 100)
                .Select(index => Sample(
                    marker + index,
                    marker + "/path/" + index,
                    marker + "/category/" + index,
                    total: 1000,
                    self: 500,
                    calls: long.MaxValue,
                    gc: long.MaxValue,
                    warnings: long.MaxValue)));
            var hotspots = analyzer.Select(
                analyzedFrameCount: 120,
                analyzedFrameTimeSumMs: 120000,
                query: null,
                sortBy: "selfTimeSumMs",
                limit: 100).Hotspots;
            var threads = Enumerable.Range(0, ProfilerDataReader.MaxReturnedThreads)
                .Select(index => new ProfilerThreadInfo
                {
                    Index = index,
                    ThreadId = ulong.MaxValue - (ulong)index,
                    Name = new string('\u0001', ProfilerDataText.MaxNameLength),
                    Group = new string('\u0001', ProfilerDataText.MaxCategoryLength),
                    SampleCount = int.MaxValue
                })
                .ToArray();
            var frames = Enumerable.Range(0, 120)
                .Select(index => new ProfilerFrameSummary
                {
                    FrameIndex = int.MaxValue - index,
                    FrameTimeMs = double.MaxValue,
                    FrameGpuTimeMs = double.MaxValue,
                    Fps = double.MaxValue
                })
                .ToArray();
            var result = new ProfilerDataResult
            {
                Available = true,
                Capture = new ProfilerCaptureInfo
                {
                    Recording = true,
                    ProfileEditor = true,
                    DeepProfiling = true,
                    ConnectedProfiler = int.MaxValue,
                    FirstFrameIndexAtStart = 1,
                    LastFrameIndexAtStart = int.MaxValue
                },
                Selection = new ProfilerSelectionInfo
                {
                    RequestedFrameCount = 120,
                    FrameCount = 120,
                    FirstFrameIndex = 1,
                    EndFrameIndex = int.MaxValue,
                    Complete = true,
                    HasMoreOlderFrames = true,
                    StopReason = "requestedCount",
                    SessionBoundaryChecked = true,
                    ViewMode = "mergedHierarchyWithoutEditorOnly",
                    MaxDepth = 128,
                    FramesWithThread = 120
                },
                Threads = threads,
                ThreadsTruncated = true,
                Thread = threads[0],
                FrameStats = new ProfilerFrameStats
                {
                    MeanFrameTimeMs = double.MaxValue,
                    P50FrameTimeMs = double.MaxValue,
                    P95FrameTimeMs = double.MaxValue,
                    MaxFrameTimeMs = double.MaxValue,
                    MaxFrameIndex = int.MaxValue
                },
                Frames = frames,
                ScannedSamples = ProfilerDataReader.MaxScannedSamples,
                UniqueHotspots = 100,
                MatchedHotspots = 100,
                Returned = 100,
                Truncated = false,
                Hotspots = hotspots
            };

            Assert.That(hotspots, Has.Length.EqualTo(100));
            Assert.That(hotspots, Has.All.Matches<ProfilerHotspotResult>(item =>
                item.Name.Length <= ProfilerDataText.MaxNameLength &&
                item.Path.Length <= ProfilerDataText.MaxPathLength &&
                item.Category.Length <= ProfilerDataText.MaxCategoryLength));

            var json = JsonConvert.SerializeObject(result, Formatting.None);
            var bytes = Encoding.UTF8.GetByteCount(json);
            Assert.That(bytes, Is.LessThan(FileChannel.MaxFileBytes));
        }

        private static void AssertOrder(
            ProfilerHotspotAnalyzer analyzer,
            string sortBy,
            params string[] expected)
        {
            var actual = analyzer.Select(2, 40, null, sortBy, 10)
                .Hotspots.Select(item => item.Name);
            Assert.That(actual, Is.EqualTo(expected), sortBy);
        }

        private static ProfilerFrameHotspot Sample(
            string name,
            string path,
            string category,
            double total = 0,
            double self = 0,
            long calls = 0,
            long gc = 0,
            long warnings = 0)
        {
            return new ProfilerFrameHotspot
            {
                Name = name,
                Path = path,
                Category = category,
                TotalTimeMs = total,
                SelfTimeMs = self,
                CallCount = calls,
                GcAllocBytes = gc,
                WarningCount = warnings
            };
        }

        private static ProfilerFrameSummary Frame(int frameIndex, double? frameTimeMs)
        {
            return new ProfilerFrameSummary
            {
                FrameIndex = frameIndex,
                FrameTimeMs = frameTimeMs
            };
        }
    }
}
