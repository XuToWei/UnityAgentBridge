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
        private const int DefaultLimit = 5000;
        private const int MaxLimit = 50000;

        public string Command => "get_hierarchy";
        public string Description => "返回已加载场景层级树;默认 maxDepth=4/limit=5000,可用 root 收窄或 maxDepth=-1 取完整深度";
        public string Group => "Inspection";
        public bool CanDisable => true;
        public CommandBatchMode BatchMode => CommandBatchMode.Allowed;

        public object Execute(JObject @params)
        {
            var maxDepth = @params?["maxDepth"]?.ToObject<int?>() ?? DefaultMaxDepth;
            if (maxDepth < -1)
            {
                throw new CommandException(ErrorCodes.InvalidParams, "maxDepth 必须为 -1 或非负整数");
            }
            var limit = @params?["limit"]?.ToObject<int?>() ?? DefaultLimit;
            if (limit <= 0 || limit > MaxLimit)
            {
                throw new CommandException(ErrorCodes.InvalidParams,
                    $"limit 必须在 1 到 {MaxLimit} 之间");
            }
            var state = new BuildState(limit);
            var rootRef = @params?["root"]?.ToObject<ObjectRef>();

            if (rootRef != null)
            {
                var go = SceneObjectResolver.ResolveObject(rootRef);
                return new
                {
                    scenes = new[]
                    {
                        new
                        {
                            scene = go.scene.path,
                            roots = new[]
                            {
                                BuildTree(go.transform, maxDepth, state,
                                    SceneObjectResolver.GetPath(go.transform))
                            }
                        }
                    }
                    ,
                    count = state.Count,
                    truncated = state.Truncated
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
                var roots = new List<Node>();
                foreach (var root in sc.GetRootGameObjects())
                {
                    var node = BuildTree(root.transform, maxDepth, state,
                        SceneObjectResolver.EscapePathSegment(root.name));
                    if (node == null)
                    {
                        break;
                    }
                    roots.Add(node);
                }
                scenes.Add(new { scene = sc.path, roots = roots.ToArray() });
                if (state.Truncated)
                {
                    break;
                }
            }
            return new { scenes, count = state.Count, truncated = state.Truncated };
        }

        private static Node BuildTree(Transform root, int maxDepth, BuildState state, string rootPath)
        {
            if (!state.TryAdd())
            {
                return null;
            }

            var rootNode = CreateNode(root, rootPath);
            var stack = new Stack<Frame>();
            stack.Push(new Frame(root, rootNode, 0));
            while (stack.Count > 0 && !state.Truncated)
            {
                var frame = stack.Pop();
                if (maxDepth >= 0 && frame.Depth >= maxDepth)
                {
                    continue;
                }

                var childFrames = new List<Frame>(frame.Transform.childCount);
                for (var i = 0; i < frame.Transform.childCount; i++)
                {
                    if (!state.TryAdd())
                    {
                        break;
                    }
                    var child = frame.Transform.GetChild(i);
                    var childPath = $"{frame.Node.path}/{SceneObjectResolver.EscapePathSegment(child.name)}";
                    var childNode = CreateNode(child, childPath);
                    frame.Node.children.Add(childNode);
                    childFrames.Add(new Frame(child, childNode, frame.Depth + 1));
                }
                for (var i = childFrames.Count - 1; i >= 0; i--)
                {
                    stack.Push(childFrames[i]);
                }
            }
            return rootNode;
        }

        private static Node CreateNode(Transform t, string path)
        {
            return new Node
            {
                name = t.name,
                path = path,
                instanceId = t.gameObject.GetInstanceID(),
                scenePath = t.gameObject.scene.path,
                active = t.gameObject.activeSelf,
                hasChildren = t.childCount > 0,
                children = new List<Node>()
            };
        }

        private sealed class Node
        {
            public string name;
            public string path;
            public int instanceId;
            public string scenePath;
            public bool active;
            public bool hasChildren;
            public List<Node> children;
        }

        private sealed class Frame
        {
            public Frame(Transform transform, Node node, int depth)
            {
                Transform = transform;
                Node = node;
                Depth = depth;
            }

            public Transform Transform { get; }
            public Node Node { get; }
            public int Depth { get; }
        }

        private sealed class BuildState
        {
            public BuildState(int limit)
            {
                Limit = limit;
            }

            public int Limit { get; }
            public int Count { get; private set; }
            public bool Truncated { get; private set; }

            public bool TryAdd()
            {
                if (Count >= Limit)
                {
                    Truncated = true;
                    return false;
                }
                Count++;
                return true;
            }
        }

        public JObject ParamsSchema { get; } = CreateParamsSchema();

        private static JObject CreateParamsSchema()
        {
            var root = SceneObjectResolver.CreateObjectRefSchema();
            root["description"] = "可选根对象;path 或 instanceId 至少提供一个。";
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["root"] = root,
                    ["maxDepth"] = new JObject
                    {
                        ["type"] = "integer", ["minimum"] = -1, ["maximum"] = int.MaxValue,
                        ["default"] = 4, ["description"] = "返回深度;默认 4,-1 表示不限。"
                    },
                    ["limit"] = new JObject
                    {
                        ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 50000,
                        ["default"] = 5000, ["description"] = "返回节点总上限;命中上限时 truncated=true。"
                    }
                }
            };
        }
    }
}
