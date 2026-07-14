using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AgentBridge
{
    /// <summary>
    /// 把 ObjectRef/ComponentRef 解析成 Unity 对象,供查询和修改命令复用。
    /// 解析规则:instanceId 优先;未命中或未提供时,按层级路径在所有已加载场景中查找。
    /// </summary>
    public static class SceneObjectResolver
    {
        public static GameObject ResolveObject(ObjectRef r)
        {
            if (r == null || (!r.InstanceId.HasValue && string.IsNullOrEmpty(r.Path)))
            {
                throw new CommandException(RefErrorCodes.InvalidObjectRef, "object ref 需要 path 或 instanceId");
            }

            if (r.InstanceId.HasValue)
            {
#if UNITY_6000_0_OR_NEWER
                var byId = EditorUtility.EntityIdToObject(r.InstanceId.Value) as GameObject;
#else
                var byId = EditorUtility.InstanceIDToObject(r.InstanceId.Value) as GameObject;
#endif
                if (byId != null && !EditorUtility.IsPersistent(byId) && byId.scene.IsValid())
                {
                    if (MatchesHints(byId, r))
                    {
                        return byId;
                    }
                    throw new CommandException(RefErrorCodes.ObjectRefStale,
                        $"instanceId={r.InstanceId} 与 path/scenePath 提示不一致;引用可能已过期");
                }
                if (string.IsNullOrEmpty(r.Path))
                {
                    if (byId != null)
                    {
                        return RequireSceneObject(byId);
                    }
                    throw new CommandException(RefErrorCodes.ObjectNotFound,
                        $"无 instanceId={r.InstanceId} 的 GameObject");
                }
            }

            var matches = FindByPathMatches(r.Path, r.ScenePath);
            if (matches.Count == 0)
            {
                throw new CommandException(RefErrorCodes.ObjectNotFound, $"路径 '{r.Path}' 未找到 GameObject");
            }
            if (matches.Count > 1)
            {
                throw new CommandException(RefErrorCodes.ObjectRefAmbiguous,
                    $"路径 '{r.Path}' 命中 {matches.Count} 个 GameObject;请传 instanceId 或 scenePath 消歧");
            }
            return RequireSceneObject(matches[0]);
        }

        /// <summary>解析组件(供 mutation 复用;inspection 自身用 get_object 列举,不调此)。</summary>
        public static Component ResolveComponent(ComponentRef cr)
        {
            if (cr == null || cr.Object == null)
            {
                throw new CommandException(RefErrorCodes.InvalidObjectRef, "component ref 缺 object");
            }
            var go = ResolveObject(cr.Object);
            var type = FindType(cr.Type, out var ambiguous);
            if (type == null)
            {
                if (ambiguous)
                {
                    throw new CommandException(RefErrorCodes.ComponentTypeAmbiguous,
                        $"组件短类型名 '{cr.Type}' 命中多个类型;请传完整命名空间类型名");
                }
                throw new CommandException(RefErrorCodes.ComponentNotFound, $"未知组件类型 '{cr.Type}'");
            }
            // 新返回的引用显式 exactType=true，索引只在相同 runtime type 内计算。
            // 缺少该字段的是 v1 早期引用，继续使用 GetComponents(baseType) 的 assignable 顺序。
            var comps = cr.ExactType == true
                ? GetExactComponents(go, type)
                : go.GetComponents(type);
            if (cr.Index >= 0 && cr.Index < comps.Length)
            {
                return comps[cr.Index];
            }
            throw new CommandException(RefErrorCodes.ComponentNotFound, $"'{go.name}' 上无 {cr.Type}[{cr.Index}]");
        }

        /// <summary>跨所有已加载场景按层级路径查 GameObject(路径含根名,如 "Player/Body")。</summary>
        public static GameObject FindByPath(string path)
        {
            var matches = FindByPathMatches(path, null);
            return matches.Count == 1 ? matches[0] : null;
        }

        public static GameObject FindByPath(string path, string scenePath)
        {
            var matches = FindByPathMatches(path, scenePath);
            return matches.Count == 1 ? matches[0] : null;
        }

        private static List<GameObject> FindByPathMatches(string path, string scenePath)
        {
            if (TryDecodePath(path, out var canonicalSegments))
            {
                var canonicalMatches = FindBySegments(canonicalSegments, scenePath);
                if (path.IndexOf('~') < 0)
                {
                    return canonicalMatches;
                }

                // v1 早期版本直接返回原始 Transform 名称。只有规范编码未命中时才
                // 回退 legacy 路径，避免把新协议中的 ~0/~1/~2 优先解释为字面量。
                var legacySegments = path.Split('/');
                if (legacySegments.All(segment => segment.Length > 0) &&
                    !legacySegments.SequenceEqual(canonicalSegments))
                {
                    var legacyMatches = FindBySegments(legacySegments, scenePath);
                    if (canonicalMatches.Count == 0)
                    {
                        return legacyMatches;
                    }
                    if (legacyMatches.Count == 0)
                    {
                        return canonicalMatches;
                    }
                    foreach (var legacyMatch in legacyMatches)
                    {
                        if (!canonicalMatches.Contains(legacyMatch))
                        {
                            canonicalMatches.Add(legacyMatch);
                        }
                    }
                }
                return canonicalMatches;
            }

            var rawSegments = path?.Split('/');
            return rawSegments != null && rawSegments.Length > 0 &&
                   rawSegments.All(segment => segment.Length > 0)
                ? FindBySegments(rawSegments, scenePath)
                : new List<GameObject>();
        }

        private static List<GameObject> FindBySegments(string[] segments, string scenePath)
        {
            var matches = new List<GameObject>();
            var normalizedScenePath = scenePath?.Replace('\\', '/');
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded)
                {
                    continue;
                }
                if (normalizedScenePath != null &&
                    !string.Equals((scene.path ?? "").Replace('\\', '/'), normalizedScenePath,
                        ScenePathComparison))
                {
                    continue;
                }

                foreach (var root in scene.GetRootGameObjects())
                {
                    if (!string.Equals(root.name, segments[0], StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var currentMatches = new List<Transform> { root.transform };
                    for (var segmentIndex = 1;
                         segmentIndex < segments.Length && currentMatches.Count > 0;
                         segmentIndex++)
                    {
                        var next = new List<Transform>();
                        foreach (var parent in currentMatches)
                        {
                            for (var childIndex = 0; childIndex < parent.childCount; childIndex++)
                            {
                                var child = parent.GetChild(childIndex);
                                if (string.Equals(child.name, segments[segmentIndex], StringComparison.Ordinal))
                                {
                                    next.Add(child);
                                }
                            }
                        }
                        currentMatches = next;
                    }
                    foreach (var current in currentMatches)
                    {
                        matches.Add(current.gameObject);
                    }
                }
            }
            return matches;
        }

        private static GameObject RequireSceneObject(GameObject go)
        {
            if (go == null)
            {
                return null;
            }
            if (EditorUtility.IsPersistent(go) || !go.scene.IsValid())
            {
                throw new CommandException(RefErrorCodes.PersistentObjectNotAllowed,
                    $"'{go.name}' 是 Project 资产或非场景对象;场景命令不允许修改 persistent object");
            }
            return go;
        }

        /// <summary>GameObject 的层级路径(含根名)。</summary>
        public static string GetPath(Transform t)
        {
            var segments = new List<string>();
            for (var cursor = t; cursor != null; cursor = cursor.parent)
            {
                segments.Add(EscapePathSegment(cursor.name));
            }
            segments.Reverse();
            return string.Join("/", segments);
        }

        internal static bool MatchesPathHint(
            Transform transform,
            string pathHint,
            string scenePath = null)
        {
            if (string.IsNullOrEmpty(pathHint))
            {
                return true;
            }
            var effectiveScenePath = scenePath ?? transform.gameObject.scene.path;
            var matches = FindByPathMatches(pathHint, effectiveScenePath);
            return matches.Count == 1 && matches[0] == transform.gameObject;
        }

        /// <summary>生成可直接回传给其它命令的稳定场景对象引用。</summary>
        public static object Describe(GameObject go)
        {
            return new
            {
                name = go.name,
                path = GetPath(go.transform),
                instanceId = go.GetInstanceID(),
                active = go.activeSelf,
                scenePath = go.scene.path
            };
        }

        /// <summary>生成与 ResolveComponent 完全对称的组件引用。</summary>
        public static object Describe(Component component)
        {
            var exact = GetExactComponents(component.gameObject, component.GetType());
            return new
            {
                @object = Describe(component.gameObject),
                type = component.GetType().FullName,
                index = Array.IndexOf(exact, component),
                exactType = true
            };
        }

        /// <summary>每次返回独立 Schema，调用者可安全附加 description/default。</summary>
        public static JObject CreateObjectRefSchema()
        {
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["path"] = new JObject { ["type"] = "string", ["minLength"] = 1 },
                    ["instanceId"] = new JObject
                    {
                        ["type"] = "integer", ["minimum"] = int.MinValue, ["maximum"] = int.MaxValue
                    },
                    ["scenePath"] = new JObject { ["type"] = "string" }
                },
                ["anyOf"] = new JArray(
                    new JObject { ["required"] = new JArray("path") },
                    new JObject { ["required"] = new JArray("instanceId") })
            };
        }

        public static JObject CreateComponentRefSchema()
        {
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["object"] = CreateObjectRefSchema(),
                    ["type"] = new JObject { ["type"] = "string", ["minLength"] = 1 },
                    ["index"] = new JObject
                    {
                        ["type"] = "integer", ["minimum"] = 0, ["maximum"] = int.MaxValue,
                        ["default"] = 0
                    },
                    ["exactType"] = new JObject
                    {
                        ["type"] = "boolean",
                        ["description"] = "true 时 index 按精确 runtime type 计算;缺省保留 v1 assignable 顺序。"
                    }
                },
                ["required"] = new JArray("object", "type")
            };
        }

        public static string EscapePathSegment(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return "~2";
            }
            return name.Replace("~", "~0").Replace("/", "~1");
        }

        private static bool TryDecodePath(string path, out string[] segments)
        {
            segments = null;
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }
            var encoded = path.Split('/');
            var decoded = new string[encoded.Length];
            for (var i = 0; i < encoded.Length; i++)
            {
                if (encoded[i].Length == 0 || !TryUnescapePathSegment(encoded[i], out decoded[i]))
                {
                    return false;
                }
            }
            segments = decoded;
            return true;
        }

        private static bool TryUnescapePathSegment(string encoded, out string value)
        {
            if (encoded == "~2")
            {
                value = "";
                return true;
            }
            var result = new System.Text.StringBuilder(encoded.Length);
            for (var i = 0; i < encoded.Length; i++)
            {
                if (encoded[i] != '~')
                {
                    result.Append(encoded[i]);
                    continue;
                }
                if (i + 1 >= encoded.Length || (encoded[i + 1] != '0' && encoded[i + 1] != '1'))
                {
                    value = null;
                    return false;
                }
                result.Append(encoded[++i] == '0' ? '~' : '/');
            }
            value = result.ToString();
            return true;
        }

        private static bool MatchesHints(GameObject go, ObjectRef reference)
        {
            if (!MatchesPathHint(go.transform, reference.Path, reference.ScenePath))
            {
                return false;
            }
            return string.IsNullOrEmpty(reference.ScenePath) ||
                   string.Equals((go.scene.path ?? "").Replace('\\', '/'),
                       reference.ScenePath.Replace('\\', '/'), ScenePathComparison);
        }

        private static StringComparison ScenePathComparison =>
            Application.platform == RuntimePlatform.WindowsEditor ||
            Application.platform == RuntimePlatform.OSXEditor
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

        /// <summary>通过 TypeCache 索引按完整名/短名查找 Component 类型。</summary>
        public static Type FindType(string typeName)
        {
            return TypeFinder.Find(typeName, typeof(Component));
        }

        public static Type FindType(string typeName, out bool ambiguous)
        {
            return TypeFinder.Find(typeName, typeof(Component), out ambiguous);
        }

        /// <summary>按精确 runtime type 取组件,与 ComponentRef.index 的生成/解析契约一致。</summary>
        public static Component[] GetExactComponents(GameObject go, Type type)
        {
            return go.GetComponents(type).Where(component => component.GetType() == type).ToArray();
        }
    }
}
