using System.IO;
using NUnit.Framework;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace AgentBridge.Tests
{
    /// <summary>资源操作命令:create_asset / import_asset / move_asset / delete_asset / refresh + 路径守卫。</summary>
    public sealed class AssetCommandTests : BridgeTestBase
    {
        [Test]
        public void CreateAsset_Folder()
        {
            var dir = EnsureTempAssetDir();
            var r = Dispatch("create_asset", new JObject { ["kind"] = "folder", ["path"] = dir + "/sub" });
            Assert.AreEqual("ok", r.Status);
            Assert.IsTrue(AssetDatabase.IsValidFolder(dir + "/sub"));
        }

        [Test]
        public void CreateAsset_Text()
        {
            var dir = EnsureTempAssetDir();
            var r = Dispatch("create_asset", new JObject { ["kind"] = "text", ["path"] = dir + "/f.json", ["content"] = "{}" });
            Assert.AreEqual("ok", r.Status);
            Assert.IsTrue(File.Exists(dir + "/f.json"));
        }

        [Test]
        public void CreateAsset_ScriptableObject()
        {
            var dir = EnsureTempAssetDir();
            var r = Dispatch("create_asset", new JObject
            {
                ["kind"] = "scriptableObject", ["type"] = "TestSettings", ["path"] = dir + "/so.asset"
            });
            Assert.AreEqual("ok", r.Status);
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<TestSettings>(dir + "/so.asset"));
        }

        [Test]
        public void CreateAsset_UnknownType()
        {
            var dir = EnsureTempAssetDir();
            var r = Dispatch("create_asset", new JObject
            {
                ["kind"] = "scriptableObject", ["type"] = "__NoSuchSOType", ["path"] = dir + "/x.asset"
            });
            Assert.AreEqual("error", r.Status);
            Assert.AreEqual(AssetErrorCodes.UnknownAssetType, r.Error.Code);
        }

        [Test]
        public void ImportAsset_FromDisk()
        {
            var dir = EnsureTempAssetDir();
            var src = Path.Combine(Path.GetTempPath(), "agentbridge-import-" + System.Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(src, "external");
            try
            {
                var r = Dispatch("import_asset", new JObject { ["source"] = src, ["destination"] = dir + "/imported.txt" });
                Assert.AreEqual("ok", r.Status);
                Assert.IsTrue(File.Exists(dir + "/imported.txt"));
            }
            finally { File.Delete(src); }
        }

        [Test]
        public void ImportAsset_SourceMissing()
        {
            var dir = EnsureTempAssetDir();
            var r = Dispatch("import_asset", new JObject
            {
                ["source"] = Path.Combine(Path.GetTempPath(), "__nope__.txt"), ["destination"] = dir + "/x.txt"
            });
            Assert.AreEqual("error", r.Status);
            Assert.AreEqual(AssetErrorCodes.AssetSourceNotFound, r.Error.Code);
        }

        [Test]
        public void MoveAsset()
        {
            var dir = EnsureTempAssetDir();
            File.WriteAllText(dir + "/a.txt", "x");
            AssetDatabase.ImportAsset(dir + "/a.txt");

            var r = Dispatch("move_asset", new JObject { ["from"] = dir + "/a.txt", ["to"] = dir + "/b.txt" });
            Assert.AreEqual("ok", r.Status);
            Assert.IsFalse(string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(dir + "/b.txt")));
            Assert.IsTrue(string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(dir + "/a.txt")));
        }

        [Test]
        public void DeleteAsset_ThenNotFound()
        {
            var dir = EnsureTempAssetDir();
            File.WriteAllText(dir + "/del.txt", "x");
            AssetDatabase.ImportAsset(dir + "/del.txt");

            var r = Dispatch("delete_asset", new JObject { ["path"] = dir + "/del.txt" });
            Assert.AreEqual("ok", r.Status);
            Assert.IsTrue(r.Result["deleted"].Value<bool>());

            var again = Dispatch("delete_asset", new JObject { ["path"] = dir + "/del.txt" });
            Assert.AreEqual("error", again.Status);
            Assert.AreEqual(AssetErrorCodes.AssetNotFound, again.Error.Code);
        }

        [Test]
        public void Refresh()
        {
            var r = Dispatch("refresh");
            Assert.AreEqual("ok", r.Status);
            Assert.IsTrue(r.Result["refreshed"].Value<bool>());
        }

        [Test]
        public void PathGuard_RejectsOutsideAssets()
        {
            var r = Dispatch("create_asset", new JObject { ["kind"] = "folder", ["path"] = "Packages/foo" });
            Assert.AreEqual("error", r.Status);
            Assert.AreEqual(AssetErrorCodes.InvalidAssetPath, r.Error.Code);

            var r2 = Dispatch("create_asset", new JObject { ["kind"] = "folder", ["path"] = "Assets/../escape" });
            Assert.AreEqual("error", r2.Status);
            Assert.AreEqual(AssetErrorCodes.InvalidAssetPath, r2.Error.Code);
        }
    }
}
