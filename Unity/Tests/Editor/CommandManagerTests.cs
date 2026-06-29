using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;

namespace AgentBridge.Tests
{
    /// <summary>命令管理器:CommandToggle(全局禁用名单)/ CommandCatalog(TypeCache 目录)/ LocalRegistry(扫扩展)。</summary>
    public sealed class CommandManagerTests : BridgeTestBase
    {
        public override void TearDown()
        {
            CommandToggle.SetEnabled("__test_echo", true);
            base.TearDown();
        }

        [Test]
        public void CommandToggle_DisableEnable_AffectsRegistry()
        {
            CommandToggle.SetEnabled("__test_echo", false);
            Assert.IsTrue(CommandToggle.Disabled().Contains("__test_echo"));
            Assert.IsTrue(CommandRegistry.IsDisabled("__test_echo"));

            CommandToggle.SetEnabled("__test_echo", true);
            Assert.IsFalse(CommandToggle.Disabled().Contains("__test_echo"));
            Assert.IsFalse(CommandRegistry.IsDisabled("__test_echo"));
        }

        [Test]
        public void NonDisablableCommandsCannotBeDisabled()
        {
            foreach (var essential in new[] { "list_commands", "ping" })
            {
                Assert.IsFalse(CommandRegistry.CanDisable(essential), essential);
                Assert.IsTrue(CommandToggle.IsEssential(essential), essential);

                CommandToggle.SetEnabled(essential, false); // 应被拒绝(no-op)
                Assert.IsFalse(CommandToggle.Disabled().Contains(essential), essential);
                Assert.IsFalse(CommandRegistry.IsDisabled(essential), essential);
            }

            Assert.IsTrue(CommandRegistry.CanDisable("__test_echo")); // 普通命令可禁用
        }

        [Test]
        public void CommandToggle_Reapply_RebuildsFromStore()
        {
            CommandToggle.SetEnabled("__test_echo", false);
            try
            {
                // 模拟 domain reload 重置 file-bridge 进程内禁用集
                CommandRegistry.SetDisabledCommands(new string[0]);
                Assert.IsFalse(CommandRegistry.IsDisabled("__test_echo"));

                CommandToggle.Reapply(); // 从 EditorPrefs 重建
                Assert.IsTrue(CommandRegistry.IsDisabled("__test_echo"));
            }
            finally { CommandToggle.SetEnabled("__test_echo", true); }
        }

        [Test]
        public void CommandCatalog_ListsBuiltinAndTest()
        {
            var all = CommandCatalog.All();
            var ping = all.FirstOrDefault(e => e.Name == "ping");
            var echo = all.FirstOrDefault(e => e.Name == "__test_echo");

            Assert.IsNotNull(ping, "目录应含内置 ping");
            Assert.IsTrue(ping.IsBuiltin);
            Assert.AreEqual(CommandCatalog.BuiltinAssembly, ping.Assembly);
            Assert.AreEqual("Meta", ping.Group); // 功能分组标签
            Assert.IsFalse(ping.CanDisable);     // ping 不可禁用

            Assert.IsNotNull(echo, "目录应含测试命令");
            Assert.IsFalse(echo.IsBuiltin, "测试命令来自测试程序集,非内置");
        }

        [Test]
        public void CommandCatalog_EnabledFlagReflectsToggle()
        {
            CommandToggle.SetEnabled("__test_echo", false);
            try
            {
                var echo = CommandCatalog.All().First(e => e.Name == "__test_echo");
                Assert.IsFalse(echo.Enabled, "禁用后目录 Enabled=false");
            }
            finally { CommandToggle.SetEnabled("__test_echo", true); }

            Assert.IsTrue(CommandCatalog.All().First(e => e.Name == "__test_echo").Enabled);
        }

        [Test]
        public void MarkdownTarget_InvalidPath_ReturnsError()
        {
            var method = typeof(AgentBridgeWindow).GetMethod("TryResolveMarkdownTargetPath",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method);

            var args = new object[] { "\0bad.md", null, null, null };
            var ok = (bool)method.Invoke(null, args);

            Assert.IsFalse(ok);
            Assert.IsNull(args[1]);
            Assert.IsNull(args[2]);
            Assert.IsFalse(string.IsNullOrEmpty((string)args[3]));
        }

        [Test]
        public void MarkdownTarget_ProjectRootRelativePath_ResolvesBesideAssets()
        {
            var method = typeof(AgentBridgeWindow).GetMethod("TryResolveMarkdownTargetPath",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method);

            var args = new object[] { "CLAUDE.md", null, null, null };
            var ok = (bool)method.Invoke(null, args);
            var fullPath = ((string)args[1]).Replace('\\', '/');
            var projectRoot = Path.GetFullPath(Path.Combine(UnityEngine.Application.dataPath, "..")).Replace('\\', '/');
            var expected = Path.GetFullPath(Path.Combine(projectRoot, "CLAUDE.md")).Replace('\\', '/');

            Assert.IsTrue(ok);
            Assert.AreEqual(expected, fullPath);
            Assert.AreEqual("CLAUDE.md", args[2]);
            Assert.IsNull(args[3]);
        }

        [Test]
        public void MarkdownTarget_ParentRelativePath_ResolvesAboveProjectRoot()
        {
            var method = typeof(AgentBridgeWindow).GetMethod("TryResolveMarkdownTargetPath",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method);

            var args = new object[] { "../AGENTS.md", null, null, null };
            var ok = (bool)method.Invoke(null, args);
            var fullPath = ((string)args[1]).Replace('\\', '/');
            var projectRoot = Path.GetFullPath(Path.Combine(UnityEngine.Application.dataPath, "..")).Replace('\\', '/');
            var expected = Path.GetFullPath(Path.Combine(projectRoot, "..", "AGENTS.md")).Replace('\\', '/');

            Assert.IsTrue(ok);
            Assert.AreEqual(expected, fullPath);
            Assert.AreEqual("../AGENTS.md", args[2]);
            Assert.IsNull(args[3]);
        }

        [Test]
        public void MarkdownTarget_OtherMarkdownName_ReturnsError()
        {
            var method = typeof(AgentBridgeWindow).GetMethod("TryResolveMarkdownTargetPath",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method);

            var args = new object[] { "README.md", null, null, null };
            var ok = (bool)method.Invoke(null, args);

            Assert.IsFalse(ok);
            Assert.IsNull(args[1]);
            Assert.IsNull(args[2]);
            Assert.IsFalse(string.IsNullOrEmpty((string)args[3]));
        }

        [Test]
        public void MarkdownTarget_AssetsRelativePath_ReturnsError()
        {
            var method = typeof(AgentBridgeWindow).GetMethod("TryResolveMarkdownTargetPath",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method);

            var args = new object[] { "Assets/CLAUDE.md", null, null, null };
            var ok = (bool)method.Invoke(null, args);

            Assert.IsFalse(ok);
            Assert.IsNull(args[1]);
            Assert.IsNull(args[2]);
            Assert.IsFalse(string.IsNullOrEmpty((string)args[3]));
        }

        [Test]
        public void MarkdownTarget_Upsert_ReplacesManagedBlockOnly()
        {
            var method = typeof(AgentBridgeWindow).GetMethod("TryUpsertManagedMarkdown",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method);

            var current = "# Manual\n\n<!-- BEGIN UNITY_AGENT_BRIDGE -->\nold\n<!-- END UNITY_AGENT_BRIDGE -->\n\nkeep";
            var args = new object[] { current, "new", null, null };
            var ok = (bool)method.Invoke(null, args);
            var updated = (string)args[2];

            Assert.IsTrue(ok);
            Assert.IsTrue(updated.Contains("# Manual"));
            Assert.IsTrue(updated.Contains("keep"));
            Assert.IsTrue(updated.Contains("new"));
            Assert.IsFalse(updated.Contains("old"));
            Assert.IsNull(args[3]);
        }

        [Test]
        public void MarkdownTarget_CurrentBlockDetection_DistinguishesUpdatedFromStale()
        {
            var upsert = typeof(AgentBridgeWindow).GetMethod("TryUpsertManagedMarkdown",
                BindingFlags.NonPublic | BindingFlags.Static);
            var isCurrent = typeof(AgentBridgeWindow).GetMethod("IsManagedMarkdownCurrent",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(upsert);
            Assert.IsNotNull(isCurrent);

            var args = new object[] { "# Manual", "new", null, null };
            var ok = (bool)upsert.Invoke(null, args);
            var updated = (string)args[2];

            Assert.IsTrue(ok);
            Assert.IsTrue((bool)isCurrent.Invoke(null, new object[] { updated, "new" }));
            Assert.IsFalse((bool)isCurrent.Invoke(null, new object[] { updated, "stale" }));
        }

        [Test]
        public void LocalRegistry_ScansTempExtension()
        {
            const string root = "Assets/AgentBridgeExtensions";
            const string ext = root + "/__testext__";
            if (!AssetDatabase.IsValidFolder(root)) AssetDatabase.CreateFolder("Assets", "AgentBridgeExtensions");
            if (!AssetDatabase.IsValidFolder(ext)) AssetDatabase.CreateFolder(root, "__testext__");
            File.WriteAllText(ext + "/extension.json",
                @"{""id"":""__testext__"",""name"":""T"",""version"":""1.0.0"",""commands"":[""foo""],""sourceDir"":"".""}");
            AssetDatabase.ImportAsset(ext + "/extension.json");
            try
            {
                var scanned = LocalRegistry.Scan().FirstOrDefault(e => e.Id == "__testext__");
                Assert.IsNotNull(scanned, "应扫到临时扩展");
                Assert.Contains("foo", scanned.Commands);
            }
            finally
            {
                AssetDatabase.DeleteAsset(ext);
                AssetDatabase.Refresh();
            }
        }
    }
}
