using System;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace AgentBridge.Tests.ProductEditMode
{
    public sealed class ProfilerOverviewTests
    {
        [Test]
        public void HandlerMetadataAndSchema_DescribeBoundedFrameOnlyOverview()
        {
            var handler = new GetProfilerOverviewHandler();

            Assert.That(handler.Command, Is.EqualTo("get_profiler_overview"));
            Assert.That(handler.Group, Is.EqualTo("Profiling"));
            Assert.That(handler.CanDisable, Is.True);
            Assert.That(handler.BatchMode, Is.EqualTo(CommandBatchMode.Allowed));
            Assert.That(handler.Description, Does.Contain("P50"));
            Assert.That(handler.Description, Does.Contain("P95"));
            Assert.That(handler.Description, Does.Contain("P99"));
            Assert.That(handler.Description, Does.Contain("不扫描 hierarchy"));
            Assert.That(JsonParamsValidator.TryValidateSchema(
                handler.ParamsSchema, out var schemaError), Is.True, schemaError);

            var properties = handler.ParamsSchema["properties"];
            Assert.That(handler.ParamsSchema["additionalProperties"]?.Value<bool>(),
                Is.False);
            Assert.That(properties?["frameCount"]?["default"]?.Value<int>(),
                Is.EqualTo(ProfilerOverviewOptions.DefaultFrameCount));
            Assert.That(properties?["frameCount"]?["minimum"]?.Value<int>(),
                Is.EqualTo(1));
            Assert.That(properties?["frameCount"]?["maximum"]?.Value<int>(),
                Is.EqualTo(ProfilerOverviewOptions.MaxFrameCount));
            Assert.That(properties?["slowestLimit"]?["default"]?.Value<int>(),
                Is.EqualTo(ProfilerOverviewOptions.DefaultSlowestLimit));
            Assert.That(properties?["slowestLimit"]?["maximum"]?.Value<int>(),
                Is.EqualTo(ProfilerOverviewOptions.MaxRankedFrameLimit));
            Assert.That(properties?["spikeLimit"]?["default"]?.Value<int>(),
                Is.EqualTo(ProfilerOverviewOptions.DefaultSpikeLimit));
            Assert.That(properties?["spikeLimit"]?["maximum"]?.Value<int>(),
                Is.EqualTo(ProfilerOverviewOptions.MaxRankedFrameLimit));
            Assert.That(properties?["includeFrames"]?["default"]?.Value<bool>(),
                Is.False);
        }

        [Test]
        public void ParamsSchema_RejectsOutOfBoundsWrongTypesAndUnknownFields()
        {
            var schema = new GetProfilerOverviewHandler().ParamsSchema;
            var invalid = new[]
            {
                new JObject { ["captureId"] = "not-a-capture" },
                new JObject { ["endFrameIndex"] = -1 },
                new JObject { ["endFrameIndex"] = (long)int.MaxValue + 1 },
                new JObject { ["frameCount"] = 0 },
                new JObject { ["frameCount"] = 2001 },
                new JObject { ["frameCount"] = "120" },
                new JObject { ["slowestLimit"] = 0 },
                new JObject { ["slowestLimit"] = 51 },
                new JObject { ["spikeLimit"] = 0 },
                new JObject { ["spikeLimit"] = 51 },
                new JObject { ["includeFrames"] = 1 },
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
                    ["endFrameIndex"] = int.MaxValue,
                    ["frameCount"] = 2000,
                    ["slowestLimit"] = 50,
                    ["spikeLimit"] = 50,
                    ["includeFrames"] = true
                },
                schema,
                out error), Is.True, error);
        }

        [Test]
        public void Analyzer_ComputesNearestRankP50P95P99MeanAndMaximum()
        {
            var analysis = ProfilerOverviewAnalyzer.Analyze(
                new[]
                {
                    Frame(1, 40),
                    Frame(2, null),
                    Frame(3, 10),
                    Frame(4, 30),
                    Frame(5, 20)
                },
                slowestLimit: 10,
                spikeLimit: 10);

            Assert.That(analysis.Stats.ValidFrameCount, Is.EqualTo(4));
            Assert.That(analysis.Stats.MissingFrameTimeCount, Is.EqualTo(1));
            Assert.That(analysis.Stats.MeanFrameTimeMs, Is.EqualTo(25).Within(0.000001));
            Assert.That(analysis.Stats.P50FrameTimeMs, Is.EqualTo(20).Within(0.000001));
            Assert.That(analysis.Stats.P95FrameTimeMs, Is.EqualTo(40).Within(0.000001));
            Assert.That(analysis.Stats.P99FrameTimeMs, Is.EqualTo(40).Within(0.000001));
            Assert.That(analysis.Stats.MaxFrameTimeMs, Is.EqualTo(40).Within(0.000001));
            Assert.That(analysis.Stats.MaxFrameIndex, Is.EqualTo(1));
        }

        [Test]
        public void Analyzer_SlowestAndSpikeTiesPreferNewerFrameIndex()
        {
            var frames = Enumerable.Range(1, 60)
                .Select(index => Frame(index, 10))
                .Concat(new[]
                {
                    Frame(100, 100),
                    Frame(101, 100),
                    Frame(102, 80)
                })
                .ToArray();

            var analysis = ProfilerOverviewAnalyzer.Analyze(
                frames,
                slowestLimit: 3,
                spikeLimit: 2);

            Assert.That(analysis.SlowestFrames.Select(frame => frame.FrameIndex),
                Is.EqualTo(new[] { 101, 100, 102 }));
            Assert.That(analysis.Spikes.ThresholdMs, Is.EqualTo(10).Within(0.000001));
            Assert.That(analysis.Spikes.Matched, Is.EqualTo(3));
            Assert.That(analysis.Spikes.Returned, Is.EqualTo(2));
            Assert.That(analysis.Spikes.Frames.Select(frame => frame.FrameIndex),
                Is.EqualTo(new[] { 101, 100 }));
            Assert.That(analysis.Spikes.Frames,
                Has.All.Matches<ProfilerSpikeFrame>(frame =>
                    frame.OverThresholdPercent == 900));
        }

        [Test]
        public void Analyzer_UsesMaxOfP95AndRobustMadThreshold()
        {
            var stableWithOutlier = Enumerable.Range(1, 100)
                .Select(index => Frame(index, index <= 95 ? 16 : 40))
                .ToArray();
            var p95Dominates = ProfilerOverviewAnalyzer.Analyze(
                stableWithOutlier,
                slowestLimit: 10,
                spikeLimit: 10);

            Assert.That(p95Dominates.Stats.P95FrameTimeMs,
                Is.EqualTo(16).Within(0.000001));
            Assert.That(p95Dominates.Spikes.ThresholdMs,
                Is.EqualTo(16).Within(0.000001));
            Assert.That(p95Dominates.Spikes.Matched, Is.EqualTo(5));

            var broadDistribution = Enumerable.Range(0, 20)
                .Select(index => Frame(index, index + 1))
                .ToArray();
            var madDominates = ProfilerOverviewAnalyzer.Analyze(
                broadDistribution,
                slowestLimit: 10,
                spikeLimit: 10);
            // nearest-rank median=10, MAD=5, robust threshold=32.239; P95=19
            Assert.That(madDominates.Spikes.ThresholdMs,
                Is.EqualTo(32.239).Within(0.000001));
            Assert.That(madDominates.Spikes.Matched, Is.Zero);
            Assert.That(madDominates.Spikes.Algorithm,
                Is.EqualTo(ProfilerOverviewAnalyzer.SpikeAlgorithm));
        }

        [Test]
        public void Analyzer_SmallWindowUsesRobustThresholdSoOutlierCanBeSpike()
        {
            var analysis = ProfilerOverviewAnalyzer.Analyze(
                new[]
                {
                    Frame(1, 10),
                    Frame(2, 10),
                    Frame(3, 10),
                    Frame(4, 100)
                },
                slowestLimit: 4,
                spikeLimit: 4);

            Assert.That(analysis.Stats.P95FrameTimeMs, Is.EqualTo(100));
            Assert.That(analysis.Spikes.ThresholdMs, Is.EqualTo(10));
            Assert.That(
                analysis.Spikes.Frames.Select(item => item.FrameIndex),
                Is.EqualTo(new[] { 4 }));
        }

        [Test]
        public void Analyzer_NoValidFrameTimes_ReturnsEmptyNullableStatistics()
        {
            var analysis = ProfilerOverviewAnalyzer.Analyze(
                new[] { Frame(1, null), Frame(2, null) },
                slowestLimit: 10,
                spikeLimit: 10);

            Assert.That(analysis.Stats.ValidFrameCount, Is.Zero);
            Assert.That(analysis.Stats.MissingFrameTimeCount, Is.EqualTo(2));
            Assert.That(analysis.Stats.MeanFrameTimeMs, Is.Null);
            Assert.That(analysis.Stats.P50FrameTimeMs, Is.Null);
            Assert.That(analysis.Stats.P95FrameTimeMs, Is.Null);
            Assert.That(analysis.Stats.P99FrameTimeMs, Is.Null);
            Assert.That(analysis.Stats.MaxFrameTimeMs, Is.Null);
            Assert.That(analysis.Stats.MaxFrameIndex, Is.Null);
            Assert.That(analysis.SlowestFrames, Is.Empty);
            Assert.That(analysis.Spikes.ThresholdMs, Is.Null);
            Assert.That(analysis.Spikes.Matched, Is.Zero);
            Assert.That(analysis.Spikes.Returned, Is.Zero);
            Assert.That(analysis.Spikes.Frames, Is.Empty);
        }

        [Test]
        public void MaximumBoundedOverview_FitsFileChannelResponseBudget()
        {
            var frames = Enumerable.Range(0, ProfilerOverviewOptions.MaxFrameCount)
                .Select(index => new ProfilerFrameSummary
                {
                    FrameIndex = int.MaxValue - index,
                    FrameTimeMs = double.MaxValue,
                    FrameGpuTimeMs = double.MaxValue,
                    Fps = double.MaxValue
                })
                .ToArray();
            var analysis = ProfilerOverviewAnalyzer.Analyze(
                frames,
                ProfilerOverviewOptions.MaxRankedFrameLimit,
                ProfilerOverviewOptions.MaxRankedFrameLimit);
            var result = new ProfilerOverviewResult
            {
                Available = true,
                Capture = new ProfilerOverviewCaptureInfo
                {
                    CaptureId = new string('f', 64),
                    Recording = true,
                    ProfileEditor = true,
                    DeepProfiling = true,
                    ConnectedProfiler = int.MaxValue,
                    FirstFrameIndexAtStart = 0,
                    LastFrameIndexAtStart = int.MaxValue
                },
                Selection = new ProfilerOverviewSelection
                {
                    RequestedFrameCount = ProfilerOverviewOptions.MaxFrameCount,
                    FrameCount = ProfilerOverviewOptions.MaxFrameCount,
                    FirstFrameIndex = 0,
                    EndFrameIndex = int.MaxValue,
                    Complete = true,
                    HasMoreOlderFrames = true,
                    StopReason = "requestedCount",
                    SessionBoundaryChecked = true,
                    IncludeFrames = true
                },
                Stats = analysis.Stats,
                SlowestFrames = analysis.SlowestFrames,
                Spikes = analysis.Spikes,
                Frames = frames
            };

            var json = JsonConvert.SerializeObject(result, Formatting.None);
            var bytes = Encoding.UTF8.GetByteCount(json);
            Assert.That(bytes, Is.LessThan(FileChannel.MaxFileBytes));
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
