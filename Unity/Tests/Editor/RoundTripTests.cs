using System.Collections;
using System.IO;
using NUnit.Framework;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine.TestTools;

namespace AgentBridge.Tests
{
    /// <summary>
    /// AI↔Unity 文件往返端到端,经**真实 update 轮询**:测试模拟 AI 写 request 文件,
    /// 让真 AgentBridgeHost 经 EditorApplication.update→Tick 处理(claim/dispatch/盖version/release),
    /// 再读 response 文件断言。[SetUp] 把真 host 重指向临时 root + Poll=0;[TearDown] 还原。
    /// </summary>
    public sealed class RoundTripTests : BridgeTestBase
    {
        private string _root;
        private string _savedRoot;
        private int _savedPoll;

        [SetUp]
        public void SetUpHost()
        {
            _savedRoot = BridgeSettings.RootDir;
            _savedPoll = BridgeSettings.PollIntervalMs;
            _root = NewTempRoot();

            AgentBridgeHost.Stop();
            BridgeSettings.RootDir = _root;
            BridgeSettings.PollIntervalMs = 0; // 免节流,update 每帧都轮询
            AgentBridgeHost.Start();
        }

        [TearDown]
        public void TearDownHost()
        {
            AgentBridgeHost.Stop();
            BridgeSettings.RootDir = _savedRoot;
            BridgeSettings.PollIntervalMs = _savedPoll;
            AgentBridgeHost.Start(); // 回真实 root
            DeleteTempRoot(_root);
        }

        private Response ReadResponse(string id) =>
            JsonConvert.DeserializeObject<Response>(File.ReadAllText(ResponsePath(_root, id)));

        [UnityTest]
        public IEnumerator RoundTrip_Ping_Ok()
        {
            WriteRequestFile(_root, "rt-ok", @"{""v"":1,""id"":""rt-ok"",""command"":""ping"",""params"":{}}");
            yield return WaitForResponse(_root, "rt-ok");

            var resp = ReadResponse("rt-ok");
            Assert.AreEqual("ok", resp.Status);
            Assert.AreEqual("pong", resp.Result?["message"]?.Value<string>());
            Assert.IsFalse(string.IsNullOrEmpty(resp.CommandsVersion), "commandsVersion 应被盖戳");
            // 单次认领:处理后 requests/processing 清空
            Assert.IsEmpty(Directory.GetFiles(Path.Combine(_root, "requests")));
            Assert.IsEmpty(Directory.GetFiles(Path.Combine(_root, "processing")));
        }

        [UnityTest]
        public IEnumerator RoundTrip_BadJson_InternalError()
        {
            WriteRequestFile(_root, "rt-bad", "{ this is not valid json ");
            yield return WaitForResponse(_root, "rt-bad");

            var resp = ReadResponse("rt-bad");
            Assert.AreEqual("error", resp.Status);
            Assert.AreEqual(ErrorCodes.InternalError, resp.Error.Code);
        }

        [UnityTest]
        public IEnumerator RoundTrip_UnknownCommand()
        {
            WriteRequestFile(_root, "rt-unk", @"{""v"":1,""id"":""rt-unk"",""command"":""__no_such_cmd"",""params"":{}}");
            yield return WaitForResponse(_root, "rt-unk");

            var resp = ReadResponse("rt-unk");
            Assert.AreEqual("error", resp.Status);
            Assert.AreEqual(ErrorCodes.UnknownCommand, resp.Error.Code);
        }

        [UnityTest]
        public IEnumerator RoundTrip_Orphan_Interrupted()
        {
            // 预置 processing/ 孤儿(模拟上次被 domain reload 打断)→ host 重启 ReclaimOrphans 补 INTERRUPTED。
            AgentBridgeHost.Stop();
            var orphan = Path.Combine(_root, "processing", "rt-orphan" + FileChannel.RequestSuffix);
            File.WriteAllText(orphan, @"{""v"":1,""id"":""rt-orphan"",""command"":""ping"",""params"":{}}");
            AgentBridgeHost.Start(); // ReclaimOrphans 同步跑
            yield return WaitForResponse(_root, "rt-orphan");

            var resp = ReadResponse("rt-orphan");
            Assert.AreEqual("error", resp.Status);
            Assert.AreEqual(ErrorCodes.Interrupted, resp.Error.Code);
        }
    }
}
