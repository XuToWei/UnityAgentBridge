using System.Collections;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace AgentBridge.Tests.EditMode
{
    /// <summary>
    /// Stable fixtures used by Test-AgentBridge.ps1 to verify run_tests/get_test_result.
    /// Keep the intentional failure Explicit so a normal Test Runner "Run All" stays green.
    /// </summary>
    public sealed class TestCommandFixtures
    {
        [Test]
        public void PassCase()
        {
            Assert.Pass();
        }

        [Test]
        [Explicit("Selected by the AgentBridge Full integration suite to verify failed results.")]
        public void ExpectedFailureCase()
        {
            Assert.Fail("AgentBridge expected failure");
        }

        [UnityTest]
        public IEnumerator SlowCase()
        {
            for (var i = 0; i < 120; i++)
            {
                yield return null;
            }

            Assert.Pass();
        }
    }
}
