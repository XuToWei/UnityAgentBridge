using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace AgentBridge.Tests.ProductEditMode
{
    public sealed class AgentBridgeHostTests
    {
        private static readonly FieldInfo s_ChannelField = RequireField("s_Channel");
        private static readonly FieldInfo s_IsProcessingField =
            RequireField("s_IsProcessing");

        [Test]
        public void StopWhileProcessingKeepsHostRunning()
        {
            var originalChannel = s_ChannelField.GetValue(null);
            var originalIsProcessing = s_IsProcessingField.GetValue(null);
            var preferenceKey = BridgeHostState.PreferenceKey;
            var hadPreference = EditorPrefs.HasKey(preferenceKey);
            var originalEnabled =
                hadPreference && EditorPrefs.GetBool(preferenceKey);
            var root = Path.Combine(
                Path.GetTempPath(),
                "AgentBridge.HostTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            try
            {
                var channel = new FileChannel(root);
                s_ChannelField.SetValue(null, channel);
                s_IsProcessingField.SetValue(null, true);
                BridgeHostState.SetEnabled(true);

                LogAssert.Expect(
                    LogType.Warning,
                    "[AgentBridge] cannot stop while an exchange is still processing.");
                AgentBridgeHost.Stop();

                Assert.That(AgentBridgeHost.IsRunning, Is.True);
                Assert.That(AgentBridgeHost.IsProcessing, Is.True);
                Assert.That(BridgeHostState.IsEnabled, Is.True);
                Assert.That(s_ChannelField.GetValue(null), Is.SameAs(channel));
            }
            finally
            {
                s_ChannelField.SetValue(null, originalChannel);
                s_IsProcessingField.SetValue(null, originalIsProcessing);
                if (hadPreference)
                {
                    EditorPrefs.SetBool(preferenceKey, originalEnabled);
                }
                else
                {
                    EditorPrefs.DeleteKey(preferenceKey);
                }
                Directory.Delete(root, true);
            }
        }

        private static FieldInfo RequireField(string name)
        {
            return typeof(AgentBridgeHost).GetField(
                       name,
                       BindingFlags.NonPublic | BindingFlags.Static)
                   ?? throw new InvalidOperationException(
                       $"AgentBridgeHost field '{name}' was not found.");
        }
    }
}
