using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AgentBridge
{
    /// <summary>
    /// get_hierarchy(只读):返回所有已加载场景的层级树。
    /// params 可选 root(ObjectRef,只返回该子树)/ maxDepth(默认 4,-1 为无限)。
    /// </summary>
    public sealed class GetHierarchyHandler : ICommandHandler
    {
        private const int DefaultMaxDepth = 4;

        public string Command => "get_hierarchy";
        public string Description => "返回已加载场景层级树;默认 maxDepth=4,可用 root 收窄或 maxDepth=-1 取完整层级";
        public string Group => "Inspection";
        public bool CanDisable => true;

        public object Execute(JObject @params)
        {
            var maxDepth = @params?["maxDepth"]?.ToObject<int?>() ?? DefaultMaxDepth;
            if (maxDepth < -1)
            {
                throw new CommandException(ErrorCodes.InvalidParams, "maxDepth 必须为 -1 或非负整数");
            }
            var rootRef = @params?["root"]?.ToObject<ObjectRef>();

            if (rootRef != null)
            {
                var go = SceneObjectResolver.ResolveObject(rootRef);
                return new
                {
                    scenes = new[]
                    {
                        new { scene = go.scene.path, roots = new[] { BuildNode(go.transform, maxDepth, 0) } }
                    }
                };
            }

            var scenes = new List<object>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var sc = SceneManager.GetSceneAt(i);
                if (!sc.isLoaded)
                {
                    continue;
                }
                var roots = sc.GetRootGameObjects().Select(r => BuildNode(r.transform, maxDepth, 0)).ToArray();
                scenes.Add(new { scene = sc.path, roots });
            }
            return new { scenes };
        }

        private static object BuildNode(Transform t, int maxDepth, int depth)
        {
            object[] children;
            if (maxDepth >= 0 && depth >= maxDepth)
            {
                children = new object[0];
            }
            else
            {
                var list = new List<object>(t.childCount);
                for (int i = 0; i < t.childCount; i++)
                {
                    list.Add(BuildNode(t.GetChild(i), maxDepth, depth + 1));
                }
                children = list.ToArray();
            }

            return new
            {
                name = t.name,
                path = SceneObjectResolver.GetPath(t),
                instanceId = t.gameObject.GetInstanceID(),
                active = t.gameObject.activeSelf,
                hasChildren = t.childCount > 0,
                children
            };
        }

        public JObject GetParamsSchema()
        {
            return JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""root"": {
      ""type"": ""object"",
      ""description"": ""可选根对象;path 或 instanceId 至少提供一个。"",
      ""properties"": {
        ""path"": { ""type"": ""string"" },
        ""instanceId"": { ""type"": ""integer"" }
      }
    },
    ""maxDepth"": {
      ""type"": ""integer"",
      ""minimum"": -1,
      ""default"": 4,
      ""description"": ""返回深度;默认 4,-1 表示不限。""
    }
  }
}");
        }
    }
}
