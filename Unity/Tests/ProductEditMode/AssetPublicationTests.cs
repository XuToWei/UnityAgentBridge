using System;
using System.IO;
using NUnit.Framework;
using UnityEditor;

namespace AgentBridge.Tests.ProductEditMode
{
    public sealed class AssetPublicationTests
    {
        private string m_Path;

        [SetUp]
        public void SetUp()
        {
            m_Path = "Assets/__AgentBridgePublication_" + Guid.NewGuid().ToString("N") + ".txt";
        }

        [TearDown]
        public void TearDown()
        {
            AssetDatabase.DeleteAsset(m_Path);
        }

        [Test]
        public void FailedPostImportValidation_RestoresPayloadMetadataAndGuid()
        {
            var original = AssetSupport.PublishTextAsset(m_Path, "before", false);
            var fullPath = AssetSupport.ToAbsolutePath(m_Path);
            var originalPayload = File.ReadAllBytes(fullPath);
            var originalMetadata = File.ReadAllBytes(fullPath + ".meta");

            var error = Assert.Throws<CommandException>(() =>
                AssetSupport.PublishBytesAsset(
                    m_Path,
                    System.Text.Encoding.UTF8.GetBytes("after"),
                    true,
                    _ => throw new InvalidOperationException("forced validation failure")));

            Assert.That(error.Code, Is.EqualTo(AssetErrorCodes.AssetCreateFailed));
            CollectionAssert.AreEqual(originalPayload, File.ReadAllBytes(fullPath));
            CollectionAssert.AreEqual(originalMetadata, File.ReadAllBytes(fullPath + ".meta"));
            Assert.That(AssetDatabase.AssetPathToGUID(m_Path), Is.EqualTo(original.Guid));
        }

        [Test]
        public void AtomicWriteWithoutOverwrite_PreservesAssetErrorAndPayload()
        {
            AssetSupport.PublishTextAsset(m_Path, "before", false);

            var error = Assert.Throws<CommandException>(() =>
                AssetSupport.WriteAllTextAtomic(m_Path, "after", false));

            Assert.That(error.Code, Is.EqualTo(AssetErrorCodes.AssetAlreadyExists));
            Assert.That(File.ReadAllText(AssetSupport.ToAbsolutePath(m_Path)), Is.EqualTo("before"));
        }
    }
}
