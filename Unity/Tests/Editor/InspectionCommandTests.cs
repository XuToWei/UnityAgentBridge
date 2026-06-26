using System.IO;
using System.Linq;
using NUnit.Framework;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace AgentBridge.Tests
{
    /// <summary>只读查询命令:get_hierarchy / get_object / get_selection / list_assets。</summary>
    public sealed class InspectionCommandTests : BridgeTestBase
    {
        public override void TearDown()
        {
            Selection.objects = new Object[0];
            base.TearDown();
        }

        private static JToken FindNode(JToken roots, string name)
        {
            foreach (var n in roots)
            {
                if (n["name"]?.Value<string>() == name) return n;
                var hit = FindNode(n["children"], name);
                if (hit != null) return hit;
            }
            return null;
        }

        [Test]
        public void GetHierarchy_ContainsCreatedObject()
        {
            var root = NewGo("TestRoot");
            NewGo("Child", root);

            var r = Dispatch("get_hierarchy");
            Assert.AreEqual("ok", r.Status);
            JToken node = null;
            foreach (var scene in r.Result["scenes"]) { node = FindNode(scene["roots"], "TestRoot"); if (node != null) break; }
            Assert.IsNotNull(node, "层级树应含 TestRoot");
            Assert.IsNotNull(FindNode(node["children"], "Child"), "TestRoot 应有子 Child");
        }

        [Test]
        public void GetHierarchy_MaxDepthZero_NoChildren()
        {
            var root = NewGo("TestRoot");
            NewGo("Child", root);

            var r = Dispatch("get_hierarchy", new JObject { ["maxDepth"] = 0 });
            JToken node = null;
            foreach (var scene in r.Result["scenes"]) { node = FindNode(scene["roots"], "TestRoot"); if (node != null) break; }
            Assert.IsNotNull(node);
            Assert.AreEqual(0, ((JArray)node["children"]).Count, "maxDepth=0 应无 children");
        }

        [Test]
        public void GetObject_ByPath_ReturnsTransform()
        {
            NewGo("TestRoot");
            var r = Dispatch("get_object", new JObject { ["object"] = new JObject { ["path"] = "TestRoot" } });
            Assert.AreEqual("ok", r.Status);
            Assert.AreEqual("TestRoot", r.Result["object"]["name"]?.Value<string>());
            var types = ((JArray)r.Result["components"]).Select(c => c["type"]?.Value<string>()).ToList();
            Assert.IsTrue(types.Any(t => t != null && t.Contains("Transform")), "组件应含 Transform");
        }

        [Test]
        public void GetObject_ComponentTypesFilter()
        {
            var go = NewGo("TestRoot");
            go.AddComponent<BoxCollider>();
            var r = Dispatch("get_object", new JObject
            {
                ["object"] = new JObject { ["path"] = "TestRoot" },
                ["componentTypes"] = new JArray { "Transform" }
            });
            var types = ((JArray)r.Result["components"]).Select(c => c["type"]?.Value<string>()).ToList();
            Assert.IsTrue(types.All(t => t != null && t.Contains("Transform")), "过滤后只应有 Transform");
        }

        [Test]
        public void GetObject_NotFound()
        {
            var r = Dispatch("get_object", new JObject { ["object"] = new JObject { ["path"] = "__nope__" } });
            Assert.AreEqual("error", r.Status);
            Assert.AreEqual(RefErrorCodes.ObjectNotFound, r.Error.Code);
        }

        [Test]
        public void GetObject_InvalidRef()
        {
            var r = Dispatch("get_object", new JObject { ["object"] = new JObject() });
            Assert.AreEqual("error", r.Status);
            Assert.AreEqual(RefErrorCodes.InvalidObjectRef, r.Error.Code);
        }

        [Test]
        public void GetSelection_ReflectsSelection()
        {
            var go = NewGo("Picked");
            Selection.activeGameObject = go;
            var r = Dispatch("get_selection");
            Assert.AreEqual("ok", r.Status);
            var names = ((JArray)r.Result["selection"]).Select(s => s["name"]?.Value<string>()).ToList();
            Assert.Contains("Picked", names);
        }

        [Test]
        public void GetSelection_EmptyIsEmptyArray()
        {
            Selection.objects = new Object[0];
            var r = Dispatch("get_selection");
            Assert.AreEqual("ok", r.Status);
            Assert.AreEqual(0, ((JArray)r.Result["selection"]).Count);
        }

        [Test]
        public void ListAssets_ByFolder_FindsCreated()
        {
            var dir = EnsureTempAssetDir();
            var path = dir + "/probe.txt";
            File.WriteAllText(path, "probe");
            AssetDatabase.ImportAsset(path);

            var r = Dispatch("list_assets", new JObject { ["folder"] = dir });
            Assert.AreEqual("ok", r.Status);
            var paths = ((JArray)r.Result["assets"]).Select(a => a["path"]?.Value<string>()).ToList();
            Assert.IsTrue(paths.Any(p => p != null && p.EndsWith("probe.txt")), "应列出 probe.txt");
            Assert.IsFalse(r.Result["truncated"].Value<bool>(), "带 filter 不截断");
        }
    }
}
