using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AgentBridge
{
    /// <summary>
    /// get_hierarchy(只读):返回所有已加载场景的层级树。
    /// params 可选 root(ObjectRef,只返回该子树)/ maxDepth(限制深度,缺省无限)。
    /// </summary>
    public sealed class GetHierarchyHandler : ICommandHandler
    {
        public string Command => "get_hierarchy";
        public string Description => "返回所有已加载场景的层级树(节点 name/path/instanceId/active/children);可选 root/maxDepth 收窄";

        public object Execute(JObject @params)
        {
            var maxDepth = @params?["maxDepth"]?.ToObject<int?>() ?? -1; // -1 = 无限
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
                children
            };
        }

        public JObject GetParamsSchema()
        {
            return JObject.Parse(
                @"{""type"":""object"",""properties"":{""root"":{""type"":""object""},""maxDepth"":{""type"":""integer""}}}");
        }
    }
}
