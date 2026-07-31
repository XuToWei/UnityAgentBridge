using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace AgentBridge.Tests.ProductEditMode
{
    public sealed class ProfilerCaptureTests
    {
        [Test]
        public void HandlerMetadataAndSchema_DescribeManagedAutomatedCapture()
        {
            var handler = new CaptureProfilerHandler();

            Assert.That(handler.Command, Is.EqualTo("capture_profiler"));
            Assert.That(handler.Group, Is.EqualTo("Profiling"));
            Assert.That(handler.CanDisable, Is.True);
            Assert.That(
                handler.BatchMode,
                Is.EqualTo(CommandBatchMode.NotAllowed));
            Assert.That(
                JsonParamsValidator.TryValidateSchema(
                    handler.ParamsSchema,
                    out var schemaError),
                Is.True,
                schemaError);

            var properties = (JObject)handler.ParamsSchema["properties"];
            CollectionAssert.AreEqual(
                new[] { "capture", "start", "stop", "status" },
                properties["action"]["enum"].Values<string>().ToArray());
            Assert.That(
                properties["action"]["default"].Value<string>(),
                Is.EqualTo("capture"));
            Assert.That(
                properties["frameCount"]["default"].Value<int>(),
                Is.EqualTo(CaptureProfilerHandler.DefaultFrameCount));
            Assert.That(
                properties["frameCount"]["minimum"].Value<int>(),
                Is.EqualTo(1));
            Assert.That(
                properties["frameCount"]["maximum"].Value<int>(),
                Is.EqualTo(2000));
            Assert.That(
                properties["timeoutMs"]["default"].Value<int>(),
                Is.EqualTo(CaptureProfilerHandler.DefaultTimeoutMs));
            Assert.That(
                properties["timeoutMs"]["minimum"].Value<int>(),
                Is.EqualTo(1000));
            Assert.That(
                properties["timeoutMs"]["maximum"].Value<int>(),
                Is.EqualTo(120000));
            Assert.That(
                properties["pollIntervalMs"]["default"].Value<int>(),
                Is.EqualTo(CaptureProfilerHandler.DefaultPollIntervalMs));
            Assert.That(
                properties["pollIntervalMs"]["minimum"].Value<int>(),
                Is.EqualTo(10));
            Assert.That(
                properties["pollIntervalMs"]["maximum"].Value<int>(),
                Is.EqualTo(1000));
            Assert.That(
                properties["clearExisting"]["default"].Value<bool>(),
                Is.True);
            Assert.That(
                properties["save"]["default"].Value<bool>(),
                Is.True);
            Assert.That(
                handler.ParamsSchema["additionalProperties"].Value<bool>(),
                Is.False);
        }

        [Test]
        public void ParamsSchema_RejectsInvalidBoundsEnumsTypesAndUnknownFields()
        {
            var schema = new CaptureProfilerHandler().ParamsSchema;
            var invalid = new[]
            {
                new JObject { ["action"] = "run" },
                new JObject { ["frameCount"] = 0 },
                new JObject { ["frameCount"] = 2001 },
                new JObject { ["frameCount"] = "300" },
                new JObject { ["timeoutMs"] = 999 },
                new JObject { ["timeoutMs"] = 120001 },
                new JObject { ["pollIntervalMs"] = 9 },
                new JObject { ["pollIntervalMs"] = 1001 },
                new JObject { ["clearExisting"] = 1 },
                new JObject { ["profileEditor"] = "true" },
                new JObject { ["save"] = 1 },
                new JObject { ["captureId"] = Guid.NewGuid().ToString("N") }
            };

            foreach (var value in invalid)
            {
                Assert.That(
                    JsonParamsValidator.TryValidate(value, schema, out _),
                    Is.False,
                    value.ToString());
            }

            Assert.That(
                JsonParamsValidator.TryValidate(
                    new JObject(),
                    schema,
                    out var error),
                Is.True,
                error);
            Assert.That(
                JsonParamsValidator.TryValidate(
                    new JObject
                    {
                        ["action"] = "capture",
                        ["frameCount"] = 2000,
                        ["timeoutMs"] = 120000,
                        ["pollIntervalMs"] = 10,
                        ["clearExisting"] = false,
                        ["profileEditor"] = true,
                        ["save"] = false
                    },
                    schema,
                    out error),
                Is.True,
                error);
        }

        [Test]
        public void FrameCounter_CountsNonContiguousFramesUntilExactAnchor()
        {
            var previous = new Dictionary<int, int>
            {
                [41] = 27,
                [27] = 12,
                [12] = 5
            };

            var result = ProfilerCaptureFrameCounter.CountNewFrames(
                5,
                41,
                frame => previous[frame]);

            Assert.That(result.Changed, Is.True);
            Assert.That(result.NewFrameCount, Is.EqualTo(3));
            Assert.That(result.AnchorFound, Is.True);
        }

        [Test]
        public void FrameCounter_FirstObservationUsesNegativeAnchor()
        {
            var previous = new Dictionary<int, int>
            {
                [90] = 62,
                [62] = -1
            };

            var result = ProfilerCaptureFrameCounter.CountNewFrames(
                -1,
                90,
                frame => previous[frame]);

            Assert.That(result.NewFrameCount, Is.EqualTo(2));
            Assert.That(result.AnchorFound, Is.True);
        }

        [Test]
        public void FrameCounter_UnchangedLatestDoesNotNavigate()
        {
            var navigationCalls = 0;

            var result = ProfilerCaptureFrameCounter.CountNewFrames(
                17,
                17,
                _ =>
                {
                    navigationCalls++;
                    return -1;
                });

            Assert.That(result.Changed, Is.False);
            Assert.That(result.NewFrameCount, Is.Zero);
            Assert.That(result.AnchorFound, Is.True);
            Assert.That(navigationCalls, Is.Zero);
        }

        [Test]
        public void FrameCounter_ReportsHistoryGapWhenAnchorWasEvicted()
        {
            var previous = new Dictionary<int, int>
            {
                [80] = 70,
                [70] = -1
            };

            var result = ProfilerCaptureFrameCounter.CountNewFrames(
                25,
                80,
                frame => previous[frame]);

            Assert.That(result.NewFrameCount, Is.EqualTo(2));
            Assert.That(result.AnchorFound, Is.False);
        }

        [Test]
        public void FrameCounter_BoundsBrokenSelfLoop()
        {
            var result = ProfilerCaptureFrameCounter.CountNewFrames(
                5,
                80,
                _ => 80);

            Assert.That(result.NewFrameCount, Is.EqualTo(1));
            Assert.That(result.AnchorFound, Is.False);
        }

        [Test]
        public void SessionGuard_BindsOnceAndRejectsReplacedProfilerBuffer()
        {
            var state = new ProfilerCaptureState();
            var original = Guid.NewGuid();

            Assert.That(
                ProfilerCaptureSessionGuard.ValidateOrBind(state, null),
                Is.True,
                "temporarily unavailable metadata must preserve compatibility");
            Assert.That(state.ProfilerSessionGuid, Is.Null);
            Assert.That(
                ProfilerCaptureSessionGuard.ValidateOrBind(
                    state, original),
                Is.True);
            Assert.That(
                state.ProfilerSessionGuid,
                Is.EqualTo(original.ToString("N")));
            Assert.That(
                ProfilerCaptureSessionGuard.ValidateOrBind(
                    state, original),
                Is.True);

            Assert.That(
                ProfilerCaptureSessionGuard.ValidateOrBind(
                    state, Guid.NewGuid()),
                Is.False);
            Assert.That(state.CaptureChanged, Is.True);
            Assert.That(
                ProfilerCaptureSessionGuard.ValidateOrBind(
                    state, original),
                Is.False,
                "captureChanged is terminal and must not silently rebind");
        }

        [Test]
        public void SessionGuard_EmptyGuidAfterBindingMeansCaptureChanged()
        {
            var state = new ProfilerCaptureState();
            Assert.That(
                ProfilerCaptureSessionGuard.ValidateOrBind(
                    state, Guid.NewGuid()),
                Is.True);

            Assert.That(
                ProfilerCaptureSessionGuard.ValidateOrBind(
                    state, Guid.Empty),
                Is.False);
            Assert.That(state.CaptureChanged, Is.True);
        }

        [Test]
        public void RetainedCounter_CountsOnlyFramesAfterRetainedStartAnchor()
        {
            var previous = new Dictionary<int, int>
            {
                [41] = 27,
                [27] = 12,
                [12] = 5
            };

            var result = ProfilerRetainedFrameCounter.Count(
                startFrameAnchorIndex: 5,
                firstFrameIndex: 5,
                lastFrameIndex: 41,
                getPreviousFrameIndex: frame => previous[frame]);

            Assert.That(result.FrameCount, Is.EqualTo(3));
            Assert.That(result.CountExact, Is.True);
            Assert.That(result.StartAnchorFound, Is.True);
        }

        [Test]
        public void RetainedCounter_WhenAnchorWasEvictedCountsWholeRetainedWindow()
        {
            var previous = new Dictionary<int, int>
            {
                [90] = 62,
                [62] = 40
            };

            var result = ProfilerRetainedFrameCounter.Count(
                startFrameAnchorIndex: 5,
                firstFrameIndex: 40,
                lastFrameIndex: 90,
                getPreviousFrameIndex: frame => previous[frame]);

            Assert.That(result.FrameCount, Is.EqualTo(3));
            Assert.That(result.CountExact, Is.True);
            Assert.That(result.StartAnchorFound, Is.False);
        }

        [Test]
        public void RetainedCounter_AfterClearCountsToNegativeAnchor()
        {
            var previous = new Dictionary<int, int>
            {
                [90] = 62,
                [62] = -1
            };

            var result = ProfilerRetainedFrameCounter.Count(
                startFrameAnchorIndex: -1,
                firstFrameIndex: 62,
                lastFrameIndex: 90,
                getPreviousFrameIndex: frame => previous[frame]);

            Assert.That(result.FrameCount, Is.EqualTo(2));
            Assert.That(result.CountExact, Is.True);
            Assert.That(result.StartAnchorFound, Is.True);
        }

        [Test]
        public void RetainedCounter_BrokenNavigationIsNotReportedExact()
        {
            var result = ProfilerRetainedFrameCounter.Count(
                startFrameAnchorIndex: 5,
                firstFrameIndex: 1,
                lastFrameIndex: 80,
                getPreviousFrameIndex: _ => 80);

            Assert.That(result.FrameCount, Is.EqualTo(1));
            Assert.That(result.CountExact, Is.False);
            Assert.That(result.StartAnchorFound, Is.False);
        }

        [Test]
        public void ResultMapping_PreservesStableCaptureIdAndCompletionSemantics()
        {
            var captureId = Guid.NewGuid().ToString("N");
            var state = new ProfilerCaptureState
            {
                CaptureId = captureId,
                Active = false,
                RequestedFrameCount = 3,
                RecordedFrameCount = 4,
                RetainedFrameCount = 4,
                RetainedFrameCountMeasured = true,
                RetainedFrameCountExact = true,
                RetainedStartAnchorFound = true,
                OriginalProfileEditor = true,
                ProfileEditorRestored = true,
                Saved = true,
                Path = $"profiler/{captureId}.data",
                RelativePath =
                    ProfilerCaptureSupport.BuildRelativePath(captureId),
                StartedAt = "2026-01-01T00:00:00.000Z",
                StoppedAt = "2026-01-01T00:00:01.000Z"
            };

            var result = ProfilerCaptureSupport.CreateResult(
                state,
                "capture",
                "requestedFrameCount",
                false,
                true);

            Assert.That(result.CaptureId, Is.EqualTo(captureId));
            Assert.That(result.Requested, Is.EqualTo(3));
            Assert.That(result.Recorded, Is.EqualTo(4));
            Assert.That(result.Observed, Is.EqualTo(4));
            Assert.That(result.Retained, Is.EqualTo(4));
            Assert.That(result.RetainedCountExact, Is.True);
            Assert.That(result.Complete, Is.True);
            Assert.That(result.Saved, Is.True);
            Assert.That(result.Recording, Is.False);
            Assert.That(result.ProfileEditorRestored, Is.True);
            Assert.That(
                result.RelativePath,
                Is.EqualTo($"profiler/{captureId}.data"));

            state.RequestedFrameCount = 2000;
            state.RecordedFrameCount = 2000;
            state.RetainedFrameCount = 300;
            state.RetainedStartAnchorFound = false;
            state.FrameHistoryGap = true;
            result = ProfilerCaptureSupport.CreateResult(
                state,
                "capture",
                "frameHistoryLimit",
                false,
                true);

            Assert.That(result.Observed, Is.EqualTo(2000));
            Assert.That(result.Retained, Is.EqualTo(300));
            Assert.That(result.Recorded, Is.EqualTo(300));
            Assert.That(result.Complete, Is.False);

            state.RequestedFrameCount = 1;
            state.RetainedFrameCount = 300;
            state.CaptureChanged = true;
            state.Saved = false;
            result = ProfilerCaptureSupport.CreateResult(
                state,
                "capture",
                "captureChanged",
                false,
                true);
            Assert.That(result.CaptureChanged, Is.True);
            Assert.That(result.Complete, Is.False);
            Assert.That(result.Saved, Is.False);
        }
    }
}
