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
        private static readonly FieldInfo s_IsProcessingField = RequireField("s_IsProcessing");
        private static readonly FieldInfo s_LastPollTimeField = RequireField("s_LastPollTime");
        private static readonly MethodInfo s_TickAsyncMethod = RequireMethod("TickAsync");
        private static readonly MethodInfo s_RestoreIfEnabledMethod = RequireMethod("RestoreIfEnabled");
        private static readonly FieldInfo s_NextRestoreTimeField = RequireField("s_NextRestoreTime");
        private static readonly FieldInfo s_IsWaitingForRootField = RequireField("s_IsWaitingForRoot");

        [Test]
        public void RuntimeLossKeepsEnabledIntentAndRecovers()
        {
            var originalChannel = s_ChannelField.GetValue(null);
            var originalLastPollTime = s_LastPollTimeField.GetValue(null);
            var originalIsProcessing = s_IsProcessingField.GetValue(null);
            var originalNextRestoreTime = s_NextRestoreTimeField.GetValue(null);
            var originalWaiting = s_IsWaitingForRootField.GetValue(null);
            var preferenceKey = BridgeHostState.PreferenceKey;
            var hadPreference = EditorPrefs.HasKey(preferenceKey);
            var originalEnabled = hadPreference && EditorPrefs.GetBool(preferenceKey);
            var root = BridgeSettings.RootDir;
            var rootExisted = Directory.Exists(root);
            var backup = root + ".AgentBridgeHostTests-" + Guid.NewGuid().ToString("N");

            try
            {
                if (rootExisted)
                {
                    Directory.Move(root, backup);
                }

                BridgeHostState.SetEnabled(true);
                s_ChannelField.SetValue(null, new FileChannel(root));
                s_LastPollTimeField.SetValue(null, double.NegativeInfinity);
                s_IsProcessingField.SetValue(null, false);
                LogAssert.Expect(
                    LogType.Warning,
                    $"[AgentBridge] bridge root unavailable; host remains enabled and will retry. root={root}");
                _ = (Task)s_TickAsyncMethod.Invoke(null, null);

                Assert.That(BridgeHostState.IsEnabled, Is.True);
                Assert.That(s_ChannelField.GetValue(null), Is.Null);
                Assert.That(s_IsWaitingForRootField.GetValue(null), Is.True);

                Directory.CreateDirectory(root);
                s_NextRestoreTimeField.SetValue(null, double.NegativeInfinity);
                s_RestoreIfEnabledMethod.Invoke(null, null);
                Assert.That(AgentBridgeHost.IsRunning, Is.True);
            }
            finally
            {
                AgentBridgeHost.Stop();
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
                if (rootExisted && Directory.Exists(backup))
                {
                    Directory.Move(backup, root);
                }
                else if (Directory.Exists(backup))
                {
                    Directory.Delete(backup, true);
                }

                s_ChannelField.SetValue(null, originalChannel);
                s_LastPollTimeField.SetValue(null, originalLastPollTime);
                s_IsProcessingField.SetValue(null, originalIsProcessing);
                s_NextRestoreTimeField.SetValue(null, originalNextRestoreTime);
                s_IsWaitingForRootField.SetValue(null, originalWaiting);
                if (hadPreference)
                {
                    EditorPrefs.SetBool(preferenceKey, originalEnabled);
                }
                else
                {
                    EditorPrefs.DeleteKey(preferenceKey);
                }
            }
        }

        [Test]
        public void StopWhileProcessingKeepsHostRunning()
        {
            var originalChannel = s_ChannelField.GetValue(null);
            var originalIsProcessing = s_IsProcessingField.GetValue(null);
            var preferenceKey = BridgeHostState.PreferenceKey;
            var hadPreference = EditorPrefs.HasKey(preferenceKey);
            var originalEnabled = hadPreference && EditorPrefs.GetBool(preferenceKey);
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

        private static MethodInfo RequireMethod(string name)
        {
            return typeof(AgentBridgeHost).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
                   ?? throw new InvalidOperationException($"AgentBridgeHost method '{name}' was not found.");
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
