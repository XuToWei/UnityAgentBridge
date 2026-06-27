using System.IO;
using System.Linq;
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
