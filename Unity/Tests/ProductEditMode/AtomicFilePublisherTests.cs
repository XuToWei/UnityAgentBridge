using System;
using System.IO;
using System.Text;
using NUnit.Framework;

namespace AgentBridge.Tests.ProductEditMode
{
    public sealed class AtomicFilePublisherTests
    {
        private string m_Directory;
        private string m_Destination;

        [SetUp]
        public void SetUp()
        {
            m_Directory = Path.Combine(Path.GetTempPath(),
                $"AgentBridgeAtomicPublisher-{Guid.NewGuid():N}");
            Directory.CreateDirectory(m_Directory);
            m_Destination = Path.Combine(m_Directory, "published.txt");
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(m_Directory))
            {
                Directory.Delete(m_Directory, true);
            }
        }

        [Test]
        public void Publish_NewDestination_MovesCompleteStagedFileAndCleansTemp()
        {
            AtomicFilePublisher.Publish(m_Destination, false,
                temp => File.WriteAllText(temp, "complete", new UTF8Encoding(false)));

            Assert.That(File.ReadAllText(m_Destination), Is.EqualTo("complete"));
            AssertNoPublicationArtifacts();
        }

        [Test]
        public void PublishRecoverableNew_UsesDeterministicTempAndPublishesCompleteFile()
        {
            string stagedPath = null;

            AtomicFilePublisher.PublishRecoverableNew(m_Destination, temp =>
            {
                stagedPath = temp;
                File.WriteAllText(temp, "complete", new UTF8Encoding(false));
            });

            Assert.That(stagedPath, Is.EqualTo($"{m_Destination}.tmp"));
            Assert.That(File.ReadAllText(m_Destination), Is.EqualTo("complete"));
            Assert.That(File.Exists($"{m_Destination}.tmp"), Is.False);
        }

        [Test]
        public void PublishRecoverableNew_DiscardsStaleTempAndStagesFreshContent()
        {
            File.WriteAllText($"{m_Destination}.tmp", "stale");
            var stageCount = 0;

            AtomicFilePublisher.PublishRecoverableNew(m_Destination, temp =>
            {
                stageCount++;
                Assert.That(temp, Is.EqualTo($"{m_Destination}.tmp"));
                Assert.That(File.Exists(temp), Is.False);
                File.WriteAllText(temp, "fresh");
            });

            Assert.That(stageCount, Is.EqualTo(1));
            Assert.That(File.ReadAllText(m_Destination), Is.EqualTo("fresh"));
            Assert.That(File.Exists($"{m_Destination}.tmp"), Is.False);
        }

        [Test]
        public void PublishRecoverableNew_StagingFailureCleansDeterministicTemp()
        {
            Assert.Throws<IOException>(() =>
                AtomicFilePublisher.PublishRecoverableNew(m_Destination, temp =>
                {
                    File.WriteAllText(temp, "partial");
                    throw new IOException("forced staging failure");
                }));

            Assert.That(File.Exists(m_Destination), Is.False);
            Assert.That(File.Exists($"{m_Destination}.tmp"), Is.False);
        }

        [Test]
        public void PublishRecoverableNew_ExistingDestinationPreservesFileAndReportsCollision()
        {
            File.WriteAllText(m_Destination, "before");

            var error = Assert.Throws<AtomicFileDestinationExistsException>(() =>
                AtomicFilePublisher.PublishRecoverableNew(m_Destination,
                    temp => File.WriteAllText(temp, "after")));

            Assert.That(error.Destination, Is.EqualTo(m_Destination));
            Assert.That(File.ReadAllText(m_Destination), Is.EqualTo("before"));
            Assert.That(File.Exists($"{m_Destination}.tmp"), Is.False);
        }

        [Test]
        public void Publish_Overwrite_ReplacesPreviousFileAndCleansTemp()
        {
            File.WriteAllText(m_Destination, "before");

            AtomicFilePublisher.Publish(m_Destination, true,
                temp => File.WriteAllText(temp, "after"));

            Assert.That(File.ReadAllText(m_Destination), Is.EqualTo("after"));
            AssertNoPublicationArtifacts();
        }

        [Test]
        public void Publish_OverwriteDisabled_PreservesPreviousFileAndReportsCollision()
        {
            File.WriteAllText(m_Destination, "before");

            var error = Assert.Throws<AtomicFileDestinationExistsException>(() =>
                AtomicFilePublisher.Publish(m_Destination, false,
                    temp => File.WriteAllText(temp, "after")));

            Assert.That(error.Destination, Is.EqualTo(m_Destination));
            Assert.That(File.ReadAllText(m_Destination), Is.EqualTo("before"));
            AssertNoPublicationArtifacts();
        }

        [Test]
        public void Publish_StagingFailure_PreservesPreviousFileAndCleansPartialTemp()
        {
            File.WriteAllText(m_Destination, "before");

            Assert.Throws<IOException>(() =>
                AtomicFilePublisher.Publish(m_Destination, true, temp =>
                {
                    File.WriteAllText(temp, "partial");
                    throw new IOException("forced staging failure");
                }));

            Assert.That(File.ReadAllText(m_Destination), Is.EqualTo("before"));
            AssertNoPublicationArtifacts();
        }

        [Test]
        public void Publish_TempNameDoesNotExtendDestinationFileName()
        {
            string stagedPath = null;

            AtomicFilePublisher.Publish(m_Destination, false, temp =>
            {
                stagedPath = temp;
                File.WriteAllText(temp, "complete");
            });

            Assert.That(Path.GetDirectoryName(stagedPath),
                Is.EqualTo(Path.GetDirectoryName(m_Destination)));
            Assert.That(Path.GetFileName(stagedPath).Length, Is.LessThan(64));
            Assert.That(Path.GetFileName(stagedPath), Does.Not.Contain("published.txt"));
        }

        [Test]
        public void Publish_OverwriteDisabled_DirectoryCollisionReportsCollision()
        {
            Directory.CreateDirectory(m_Destination);

            var error = Assert.Throws<AtomicFileDestinationExistsException>(() =>
                AtomicFilePublisher.Publish(m_Destination, false,
                    temp => File.WriteAllText(temp, "after")));

            Assert.That(error.Destination, Is.EqualTo(m_Destination));
            Assert.That(Directory.Exists(m_Destination), Is.True);
            AssertNoPublicationArtifacts();
        }

        [Test]
        public void ScreenshotCollision_PreservesDomainErrorAndExistingFile()
        {
            File.WriteAllText(m_Destination, "before");
            var target = new ScreenshotSupport.Target("published.txt", m_Destination, false);

            var error = Assert.Throws<CommandException>(() =>
                ScreenshotSupport.Write(target, new byte[] { 1, 2, 3 }));

            Assert.That(error.Code, Is.EqualTo("SCREENSHOT_ALREADY_EXISTS"));
            Assert.That(File.ReadAllText(m_Destination), Is.EqualTo("before"));
            AssertNoPublicationArtifacts();
        }

        private void AssertNoPublicationArtifacts()
        {
            Assert.That(Directory.GetFiles(m_Directory, "*.tmp"), Is.Empty);
        }
    }
}
