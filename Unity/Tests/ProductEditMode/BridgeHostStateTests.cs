using NUnit.Framework;
using UnityEditor;

namespace AgentBridge.Tests.ProductEditMode
{
    public sealed class BridgeHostStateTests
    {
        [Test]
        public void MissingProjectStateDefaultsToEnabled()
        {
            var key = BridgeHostState.PreferenceKey;
            var hadPreference = EditorPrefs.HasKey(key);
            var originalEnabled =
                hadPreference && EditorPrefs.GetBool(key);

            try
            {
                EditorPrefs.DeleteKey(key);

                Assert.That(BridgeHostState.IsEnabled, Is.True);
            }
            finally
            {
                if (hadPreference)
                {
                    EditorPrefs.SetBool(key, originalEnabled);
                }
                else
                {
                    EditorPrefs.DeleteKey(key);
                }
            }
        }

        [Test]
        public void SetEnabledPersistsExplicitProjectState()
        {
            var key = BridgeHostState.PreferenceKey;
            var hadPreference = EditorPrefs.HasKey(key);
            var originalEnabled =
                hadPreference && EditorPrefs.GetBool(key);

            try
            {
                BridgeHostState.SetEnabled(false);
                Assert.That(EditorPrefs.HasKey(key), Is.True);
                Assert.That(BridgeHostState.IsEnabled, Is.False);

                BridgeHostState.SetEnabled(true);
                Assert.That(BridgeHostState.IsEnabled, Is.True);
            }
            finally
            {
                if (hadPreference)
                {
                    EditorPrefs.SetBool(key, originalEnabled);
                }
                else
                {
                    EditorPrefs.DeleteKey(key);
                }
            }
        }
    }
}
