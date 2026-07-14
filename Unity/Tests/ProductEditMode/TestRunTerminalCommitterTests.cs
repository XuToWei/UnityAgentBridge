using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;

namespace AgentBridge.Tests.ProductEditMode
{
    public sealed class TestRunTerminalCommitterTests
    {
        [Test]
        public void FailedSaveKeepsPendingAndDoesNotPublishTerminalState()
        {
            var published = false;
            var committer = new TestRunTerminalCommitter(
                _ => throw new IOException("locked"),
                _ => published = true);
            var terminal = CreateTerminal(TestRunStatuses.Completed);

            committer.Stage(terminal, "write final");

            Assert.That(committer.TryCommit(out var failure), Is.False);
            Assert.That(failure, Is.TypeOf<IOException>());
            Assert.That(published, Is.False);
            Assert.That(committer.HasPending, Is.True);
            Assert.That(committer.PendingRunId, Is.EqualTo(terminal.RunId));
            Assert.That(committer.FailureCount, Is.EqualTo(1));
        }

        [Test]
        public void RetryPublishesOnceOnlyAfterSaveSucceeds()
        {
            var events = new List<string>();
            var attempts = 0;
            TestRunRecord published = null;
            var committer = new TestRunTerminalCommitter(
                record =>
                {
                    attempts++;
                    events.Add("save:" + attempts);
                    if (attempts == 1)
                    {
                        throw new IOException("transient");
                    }
                    Assert.That(record.Status, Is.EqualTo(TestRunStatuses.Interrupted));
                },
                record =>
                {
                    events.Add("publish");
                    published = record;
                });
            var terminal = CreateTerminal(TestRunStatuses.Interrupted);
            committer.Stage(terminal, "write interrupted");

            Assert.That(committer.TryCommit(out var firstFailure), Is.False);
            Assert.That(firstFailure, Is.Not.Null);
            Assert.That(committer.TryCommit(out var retryFailure), Is.True);

            Assert.That(retryFailure, Is.Null);
            Assert.That(published, Is.SameAs(terminal));
            Assert.That(events, Is.EqualTo(new[] { "save:1", "save:2", "publish" }));
            Assert.That(committer.HasPending, Is.False);
            Assert.That(committer.FailureCount, Is.Zero);
        }

        [Test]
        public void PublishFailureKeepsPendingAndRetriesIdempotentSave()
        {
            var saves = 0;
            var publishAttempts = 0;
            var published = false;
            var committer = new TestRunTerminalCommitter(
                _ => saves++,
                _ =>
                {
                    publishAttempts++;
                    if (publishAttempts == 1)
                    {
                        throw new InvalidOperationException("session state unavailable");
                    }
                    published = true;
                });
            committer.Stage(CreateTerminal(TestRunStatuses.Completed), "write final");

            Assert.That(committer.TryCommit(out var publishFailure), Is.False);
            Assert.That(publishFailure, Is.TypeOf<InvalidOperationException>());
            Assert.That(committer.HasPending, Is.True);
            Assert.That(published, Is.False);

            Assert.That(committer.TryCommit(out var retryFailure), Is.True);
            Assert.That(retryFailure, Is.Null);
            Assert.That(saves, Is.EqualTo(2));
            Assert.That(publishAttempts, Is.EqualTo(2));
            Assert.That(published, Is.True);
            Assert.That(committer.HasPending, Is.False);
        }

        [Test]
        public void RunningRecordCannotEnterTerminalCommitQueue()
        {
            var committer = new TestRunTerminalCommitter(_ => { }, _ => { });
            var running = CreateTerminal(TestRunStatuses.Running);

            Assert.Throws<ArgumentException>(() => committer.Stage(running, "invalid"));
            Assert.That(committer.HasPending, Is.False);
        }

        [Test]
        public void TerminalSnapshotIsDeeplyIsolatedFromActiveRecord()
        {
            var active = new TestRunRecord
            {
                RunId = "test-run-clone",
                Status = TestRunStatuses.Running,
                Filter = new TestRunFilterRecord { TestNames = new[] { "Before" } },
                SavedScenes = new[] { "Assets/Before.unity" },
                Results = new List<TestCaseResultRecord>
                {
                    new TestCaseResultRecord
                    {
                        Id = "case-1",
                        Status = "Passed",
                        Categories = new[] { "Fast" }
                    }
                }
            };

            var terminal = active.Clone();
            terminal.Status = TestRunStatuses.Completed;
            terminal.Filter.TestNames[0] = "After";
            terminal.SavedScenes[0] = "Assets/After.unity";
            terminal.Results[0].Categories[0] = "Slow";
            terminal.Results.Add(new TestCaseResultRecord { Id = "case-2" });

            Assert.That(active.Status, Is.EqualTo(TestRunStatuses.Running));
            Assert.That(active.Filter.TestNames, Is.EqualTo(new[] { "Before" }));
            Assert.That(active.SavedScenes, Is.EqualTo(new[] { "Assets/Before.unity" }));
            Assert.That(active.Results[0].Categories, Is.EqualTo(new[] { "Fast" }));
            Assert.That(active.Results, Has.Count.EqualTo(1));
        }

        private static TestRunRecord CreateTerminal(string status)
        {
            return new TestRunRecord
            {
                RunId = "test-run-terminal",
                Status = status,
                StartedAt = "2026-07-12T00:00:00.0000000Z",
                FinishedAt = "2026-07-12T00:00:01.0000000Z"
            };
        }
    }
}
