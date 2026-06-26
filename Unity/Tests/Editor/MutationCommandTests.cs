using System.Text.RegularExpressions;
using NUnit.Framework;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;

namespace AgentBridge.Tests
{
    /// <summary>写操作命令:set_property / create_object / delete_object / invoke_menu。</summary>
    public sealed class MutationCommandTests : BridgeTestBase
    {
        private static JObject CompRef(string path, string type) => new JObject
        {
            ["object"] = new JObject { ["path"] = path },
            ["type"] = type,
            ["index"] = 0
        };

        private static void DestroyById(JToken result)
        {
            var id = result?["object"]?["instanceId"]?.Value<int>();
            if (id.HasValue)
            {
#if UNITY_6000_0_OR_NEWER
                var o = EditorUtility.EntityIdToObject(id.Value);
#else
                var o = EditorUtility.InstanceIDToObject(id.Value);
#endif
                if (o != null) Object.DestroyImmediate(o);
            }
        }

        [Test]
        public void SetProperty_NestedPath_Applies()
        {
            var go = NewGo("Target");
            var r = Dispatch("set_property", new JObject
            {
                ["component"] = CompRef("Target", "Transform"),
                ["propertyPath"] = "m_LocalPosition.x",
                ["value"] = 3.5f
            });
            Assert.AreEqual("ok", r.Status);
            Assert.AreEqual(3.5f, go.transform.localPosition.x, 0.0001f);
            Assert.IsTrue(EditorSceneManager.GetActiveScene().isDirty, "set_property 应标脏场景");
        }

        [Test]
        public void SetProperty_TypeMismatch()
        {
            NewGo("Target");
            var r = Dispatch("set_property", new JObject
            {
                ["component"] = CompRef("Target", "Transform"),
                ["propertyPath"] = "m_LocalPosition.x",
                ["value"] = "not-a-number"
            });
            Assert.AreEqual("error", r.Status);
            Assert.AreEqual(MutationErrorCodes.PropertyTypeMismatch, r.Error.Code);
        }

        [Test]
        public void SetProperty_PropertyNotFound()
        {
            NewGo("Target");
            var r = Dispatch("set_property", new JObject
            {
                ["component"] = CompRef("Target", "Transform"),
                ["propertyPath"] = "__no_such_prop",
                ["value"] = 1
            });
            Assert.AreEqual("error", r.Status);
            Assert.AreEqual(MutationErrorCodes.PropertyNotFound, r.Error.Code);
        }

        [Test]
        public void SetProperty_ComponentNotFound()
        {
            NewGo("Target");
            var r = Dispatch("set_property", new JObject
            {
                ["component"] = CompRef("Target", "__NoSuchComponent"),
                ["propertyPath"] = "x",
                ["value"] = 1
            });
            Assert.AreEqual("error", r.Status);
            Assert.AreEqual(RefErrorCodes.ComponentNotFound, r.Error.Code);
        }

        [Test]
        public void CreateObject_Empty_ReturnsRef()
        {
            var r = Dispatch("create_object", new JObject { ["kind"] = "empty", ["name"] = "Created" });
            try
            {
                Assert.AreEqual("ok", r.Status);
                Assert.AreEqual("Created", r.Result["object"]["name"]?.Value<string>());
                Assert.IsTrue(r.Result["object"]["instanceId"].Value<int>() != 0);
            }
            finally { DestroyById(r.Result); }
        }

        [Test]
        public void CreateObject_Primitive_Cube()
        {
            var r = Dispatch("create_object", new JObject { ["kind"] = "primitive", ["primitive"] = "Cube", ["name"] = "C" });
            try
            {
                Assert.AreEqual("ok", r.Status);
#if UNITY_6000_0_OR_NEWER
                var go = (GameObject)EditorUtility.EntityIdToObject(r.Result["object"]["instanceId"].Value<int>());
#else
                var go = (GameObject)EditorUtility.InstanceIDToObject(r.Result["object"]["instanceId"].Value<int>());
#endif
                Assert.IsNotNull(go.GetComponent<MeshFilter>(), "Cube 应有 MeshFilter");
            }
            finally { DestroyById(r.Result); }
        }

        [Test]
        public void CreateObject_WithParent()
        {
            var parent = NewGo("Parent");
            var r = Dispatch("create_object", new JObject
            {
                ["kind"] = "empty", ["name"] = "Kid", ["parent"] = new JObject { ["path"] = "Parent" }
            });
            try
            {
                Assert.AreEqual("ok", r.Status);
                Assert.AreEqual(1, parent.transform.childCount);
                Assert.AreEqual("Kid", parent.transform.GetChild(0).name);
            }
            finally { DestroyById(r.Result); }
        }

        [Test]
        public void CreateObject_UnknownPrimitive_Fails()
        {
            var r = Dispatch("create_object", new JObject { ["kind"] = "primitive", ["primitive"] = "__nope" });
            Assert.AreEqual("error", r.Status);
            Assert.AreEqual(MutationErrorCodes.CreateFailed, r.Error.Code);
        }

        [Test]
        public void CreateObject_Prefab()
        {
            var src = NewGo("PrefabSrc");
            var prefabPath = EnsureTempAssetDir() + "/p.prefab";
            PrefabUtility.SaveAsPrefabAsset(src, prefabPath);

            var r = Dispatch("create_object", new JObject { ["kind"] = "prefab", ["prefabPath"] = prefabPath });
            try
            {
                Assert.AreEqual("ok", r.Status);
                Assert.IsTrue(r.Result["object"]["instanceId"].Value<int>() != 0);
            }
            finally { DestroyById(r.Result); }

            // 无效路径
            var bad = Dispatch("create_object", new JObject { ["kind"] = "prefab", ["prefabPath"] = "Assets/__nope.prefab" });
            Assert.AreEqual("error", bad.Status);
            Assert.AreEqual(MutationErrorCodes.CreateFailed, bad.Error.Code);
        }

        [Test]
        public void DeleteObject_ThenRepeatNotFound()
        {
            NewGo("Doomed");
            var r = Dispatch("delete_object", new JObject { ["object"] = new JObject { ["path"] = "Doomed" } });
            Assert.AreEqual("ok", r.Status);
            Assert.IsTrue(r.Result["deleted"].Value<bool>());

            var again = Dispatch("delete_object", new JObject { ["object"] = new JObject { ["path"] = "Doomed" } });
            Assert.AreEqual("error", again.Status);
            Assert.AreEqual(RefErrorCodes.ObjectNotFound, again.Error.Code);
        }

        [Test]
        public void InvokeMenu_KnownAndUnknown()
        {
            var ok = Dispatch("invoke_menu", new JObject { ["path"] = "Assets/Refresh" });
            Assert.AreEqual("ok", ok.Status);
            Assert.IsTrue(ok.Result["executed"].Value<bool>());

            // ExecuteMenuItem 对未知菜单会往 Console 打 Error,预告之(否则 Test Framework 判失败)。
            LogAssert.Expect(LogType.Error, new Regex("ExecuteMenuItem failed"));
            var bad = Dispatch("invoke_menu", new JObject { ["path"] = "No/Such/Menu/Item" });
            Assert.AreEqual("error", bad.Status);
            Assert.AreEqual(MutationErrorCodes.MenuNotFound, bad.Error.Code);
        }
    }
}
