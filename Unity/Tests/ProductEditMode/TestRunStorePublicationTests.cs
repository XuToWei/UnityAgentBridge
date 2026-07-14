using System;
using System.IO;
using NUnit.Framework;

namespace AgentBridge.Tests.ProductEditMode
{
    public sealed class TestRunStorePublicationTests
    {
        private string m_RunId;
        private string m_ResultDirectory;
        private string m_ResultPath;

        [SetUp]
        public void SetUp()
        {
            m_RunId = "atomic-" + Guid.NewGuid().ToString("N");
            m_ResultDirectory = Path.Combine(BridgeSettings.RootDir, "test-results");
            m_ResultPath = Path.Combine(m_ResultDirectory, m_RunId + ".json");
        }

        [TearDown]
        public void TearDown()
        {
            AtomicFilePublisher.DeleteBestEffort(m_ResultPath);
            if (Directory.Exists(m_ResultPath))
            {
                Directory.Delete(m_ResultPath, true);
            }
        }

        [Test]
        public void Save_OverwritesRecordAtomicallyWithoutPublicationArtifacts()
        {
            var record = new TestRunRecord
            {
                RunId = m_RunId,
                Status = TestRunStatuses.Running,
                Message = "before"
            };
            TestRunStore.Save(record);
            record.Status = TestRunStatuses.Completed;
            record.Message = "after";

            TestRunStore.Save(record);

            Assert.That(TestRunStore.TryLoad(m_RunId, out var loaded), Is.True);
            Assert.That(loaded.Status, Is.EqualTo(TestRunStatuses.Completed));
            Assert.That(loaded.Message, Is.EqualTo("after"));
            AssertNoPublicationArtifacts();
        }

        [Test]
        public void Save_IoFailurePreservesDomainErrorAndCleansTemp()
        {
            Directory.CreateDirectory(m_ResultPath);
            var record = new TestRunRecord
            {
                RunId = m_RunId,
                Status = TestRunStatuses.Running
            };

            var error = Assert.Throws<CommandException>(() => TestRunStore.Save(record));

            Assert.That(error.Code, Is.EqualTo(TestErrorCodes.TestResultIoError));
            Assert.That(Directory.Exists(m_ResultPath), Is.True);
            AssertNoPublicationArtifacts();
        }

        private void AssertNoPublicationArtifacts()
        {
            Assert.That(Directory.GetFiles(m_ResultDirectory, ".*.tmp"), Is.Empty);
        }
    }
}
