using NUnit.Framework;

namespace AgentBridge.Tests.ProductEditMode
{
    public sealed class MarkdownInstructionsTests
    {
        private const string BlockStart = "<!-- BEGIN UNITY_AGENT_BRIDGE -->";
        private const string BlockEnd = "<!-- END UNITY_AGENT_BRIDGE -->";
        private const string Template = "## Unity Agent Bridge\n\nRead AGENT.md before using the bridge.";

        [TestCase(null, "")]
        [TestCase("", "")]
        [TestCase(" \r\n", "")]
        [TestCase("# Project instructions", "# Project instructions\n\n")]
        [TestCase("# Project instructions\n", "# Project instructions\n\n")]
        [TestCase("# Project instructions\r\n", "# Project instructions\r\n\n")]
        public void WriteThenUpdate_DoesNotAccumulateNewlines(string current, string expectedPrefix)
        {
            Assert.That(AgentBridgeWindow.TryUpsertManagedMarkdown(
                current, Template, out var written, out var error), Is.True, error);
            Assert.That(written, Is.EqualTo($"{expectedPrefix}{BlockStart}\n{Template}\n{BlockEnd}\n"));

            current = written;
            for (var update = 0; update < 3; update++)
            {
                Assert.That(AgentBridgeWindow.TryUpsertManagedMarkdown(
                    current, Template, out var updated, out error), Is.True, error);
                Assert.That(updated, Is.EqualTo(written), $"Update {update + 1} changed the document.");
                current = updated;
            }
        }

        [Test]
        public void Update_PreservesContentAndWhitespaceOutsideMarkers(
            [Values("\n", "\r\n")] string newline,
            [Values("", "\n", "\n\n\n", "\r\n", "\r\n\r\n\r\n",
                "\n\n# Other instructions\n", "\r\n\r\n# Other instructions\r\n", " \t\n\n")]
            string suffix)
        {
            var prefix = $"# Project instructions{newline}{newline}";
            var current = $"{prefix}{BlockStart}{newline}Old instructions{newline}{BlockEnd}{suffix}";
            var expected = $"{prefix}{BlockStart}\n{Template}\n{BlockEnd}{suffix}";

            for (var update = 0; update < 3; update++)
            {
                Assert.That(AgentBridgeWindow.TryUpsertManagedMarkdown(
                    current, Template, out var updated, out var error), Is.True, error);
                Assert.That(updated, Is.EqualTo(expected), $"Update {update + 1} changed content outside the markers.");
                current = updated;
            }
        }
    }
}
