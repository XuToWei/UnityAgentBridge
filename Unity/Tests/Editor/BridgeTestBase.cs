using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace AgentBridge.Tests
{
    /// <summary>
    /// 测试基类:提供临时 GameObject / 临时资产目录的 setup-teardown 清理、Dispatch 助手、
    /// 以及 AI↔Unity 往返用的临时 root + 真实 Host 重指向 + WaitForResponse 协程。
    /// 每个测试自建自清,跑完仓库/EditorPrefs/host 无残留。
    /// </summary>
    public abstract class BridgeTestBase
    {
        protected const string TempAssetDir = "Assets/__AgentBridgeTests__";
        private readonly List<GameObject> _spawned = new List<GameObject>();
        private string[] _savedDisabled;

        [SetUp]
        public virtual void SetUp()
        {
            // 隔离环境禁用名单:测试期间清空 registry 内存禁用集(不碰 EditorPrefs,开发者配置不丢),
            // 否则宿主工程里被用户禁用的命令会让命令测试误得 COMMAND_DISABLED。
            _savedDisabled = CommandToggle.Disabled().ToArray();
            CommandRegistry.SetDisabledCommands(new string[0]);
        }

        [TearDown]
        public virtual void TearDown()
        {
            CommandRegistry.SetDisabledCommands(_savedDisabled ?? new string[0]); // 还原环境禁用名单(内存)

            foreach (var go in _spawned)
                if (go != null) UnityEngine.Object.DestroyImmediate(go);
            _spawned.Clear();

            if (AssetDatabase.IsValidFolder(TempAssetDir))
            {
                AssetDatabase.DeleteAsset(TempAssetDir);
                AssetDatabase.Refresh();
            }
        }

        /// <summary>建一个临时 GameObject(自动登记 TearDown 销毁)。</summary>
        protected GameObject NewGo(string name, GameObject parent = null)
        {
            var go = new GameObject(name);
            if (parent != null) go.transform.SetParent(parent.transform);
            _spawned.Add(go);
            return go;
        }

        /// <summary>确保临时资产目录存在(返回工程相对路径)。</summary>
        protected string EnsureTempAssetDir()
        {
            if (!AssetDatabase.IsValidFolder(TempAssetDir))
            {
                AssetDatabase.CreateFolder("Assets", "__AgentBridgeTests__");
                AssetDatabase.Refresh();
            }
            return TempAssetDir;
        }

        /// <summary>经分发器调命令(集成视角):构造 Request → Response。</summary>
        protected static Response Dispatch(string command, JObject p = null) =>
            CommandDispatcher.Dispatch(new Request
            {
                Id = Guid.NewGuid().ToString("N"),
                Command = command,
                Params = p ?? new JObject()
            });

        // —— 往返(真实 update)助手 ——

        /// <summary>新建一个临时 IPC 根目录(系统 temp 下),含 requests/processing/responses。</summary>
        protected static string NewTempRoot()
        {
            var root = Path.Combine(Path.GetTempPath(), "agentbridge-test-" + Guid.NewGuid().ToString("N"));
            new FileChannel(root); // 构造即建目录
            return root;
        }

        protected static void DeleteTempRoot(string root)
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { /* 忽略 */ }
        }

        /// <summary>AI 侧:原子写一个请求文件(tmp→rename)。</summary>
        protected static void WriteRequestFile(string root, string id, string json)
        {
            var final = Path.Combine(root, "requests", id + FileChannel.RequestSuffix);
            var tmp = final + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, final);
        }

        protected static string ResponsePath(string root, string id) =>
            Path.Combine(root, "responses", id + FileChannel.ResponseSuffix);

        /// <summary>yield 到 response 文件出现或超时(让真实 EditorApplication.update→Tick 跑)。</summary>
        protected static IEnumerator WaitForResponse(string root, string id, float timeoutSec = 5f)
        {
            var path = ResponsePath(root, id);
            var start = EditorApplication.timeSinceStartup;
            while (!File.Exists(path))
            {
                if (EditorApplication.timeSinceStartup - start > timeoutSec)
                {
                    Assert.Fail($"timeout waiting for response {id} at {path}");
                    yield break;
                }
                yield return null;
            }
            // 文件刚出现可能仍在写,读不到内容时再等一帧
            yield return null;
        }
    }
}
