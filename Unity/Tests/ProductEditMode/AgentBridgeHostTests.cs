using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace AgentBridge.Tests.ProductEditMode
{
    public sealed class AgentBridgeHostTests
    {
        private static readonly FieldInfo s_ChannelField = RequireField("s_Channel");
        private static readonly FieldInfo s_LastPollTimeField = RequireField("s_LastPollTime");
        private static readonly FieldInfo s_IsProcessingField =
            RequireField("s_IsProcessing");
        private static readonly MethodInfo s_TickAsyncMethod = RequireMethod("TickAsync");
        private static readonly MethodInfo s_ActivateMethod = RequireMethod("Activate");

        [Test]
        public async Task RuntimeLossKeepsEnabledIntent()
        {
            var originalChannel = s_ChannelField.GetValue(null);
            var originalLastPollTime = s_LastPollTimeField.GetValue(null);
            var originalIsProcessing = s_IsProcessingField.GetValue(null);
            var preferenceKey = BridgeHostState.PreferenceKey;
            var hadPreference = EditorPrefs.HasKey(preferenceKey);
            var originalEnabled = hadPreference && EditorPrefs.GetBool(preferenceKey);
            var missingRoot = Path.Combine(Path.GetTempPath(), "AgentBridge.HostTests", Guid.NewGuid().ToString("N"));

            try
            {
                s_ChannelField.SetValue(null, new FileChannel(missingRoot));
                s_LastPollTimeField.SetValue(null, double.NegativeInfinity);
                s_IsProcessingField.SetValue(null, false);
                BridgeHostState.SetEnabled(true);

                await (Task)s_TickAsyncMethod.Invoke(null, null);

                Assert.That(BridgeHostState.IsEnabled, Is.True);
                Assert.That(s_ChannelField.GetValue(null), Is.Null);
            }
            finally
            {
                AgentBridgeHost.Stop();
                if (originalChannel != null) s_ActivateMethod.Invoke(null, new[] { originalChannel });
                else s_ChannelField.SetValue(null, null);
                s_LastPollTimeField.SetValue(null, originalLastPollTime);
                s_IsProcessingField.SetValue(null, originalIsProcessing);
                if (hadPreference) EditorPrefs.SetBool(preferenceKey, originalEnabled);
                else EditorPrefs.DeleteKey(preferenceKey);
                if (Directory.Exists(missingRoot)) Directory.Delete(missingRoot, true);
            }
        }

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

        private static MethodInfo RequireMethod(string name)
        {
            return typeof(AgentBridgeHost).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
                   ?? throw new InvalidOperationException($"AgentBridgeHost method '{name}' was not found.");
        }
    }
}
