using System;
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
                if (byId != null)
                {
                    return byId;
                }
                if (string.IsNullOrEmpty(r.Path))
                {
                    throw new CommandException(RefErrorCodes.ObjectNotFound, $"无 instanceId={r.InstanceId} 的 GameObject");
                }
            }

            var byPath = FindByPath(r.Path);
            if (byPath == null)
            {
                throw new CommandException(RefErrorCodes.ObjectNotFound, $"路径 '{r.Path}' 未找到 GameObject");
            }
            return byPath;
        }

        /// <summary>解析组件(供 mutation 复用;inspection 自身用 get_object 列举,不调此)。</summary>
        public static Component ResolveComponent(ComponentRef cr)
        {
            if (cr == null || cr.Object == null)
            {
                throw new CommandException(RefErrorCodes.InvalidObjectRef, "component ref 缺 object");
            }
            var go = ResolveObject(cr.Object);
            var type = FindType(cr.Type);
            if (type == null)
            {
                throw new CommandException(RefErrorCodes.ComponentNotFound, $"未知组件类型 '{cr.Type}'");
            }
            var comps = go.GetComponents(type);
            if (cr.Index < 0 || cr.Index >= comps.Length)
            {
                throw new CommandException(RefErrorCodes.ComponentNotFound, $"'{go.name}' 上无 {cr.Type}[{cr.Index}]");
            }
            return comps[cr.Index];
        }

        /// <summary>跨所有已加载场景按层级路径查 GameObject(路径含根名,如 "Player/Body")。</summary>
        public static GameObject FindByPath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }
            var parts = path.Split('/');
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded)
                {
                    continue;
                }
                foreach (var root in scene.GetRootGameObjects())
                {
                    if (root.name != parts[0])
                    {
                        continue;
                    }
                    var t = root.transform;
                    var ok = true;
                    for (int p = 1; p < parts.Length; p++)
                    {
                        var child = t.Find(parts[p]);
                        if (child == null)
                        {
                            ok = false;
                            break;
                        }
                        t = child;
                    }
                    if (ok)
                    {
                        return t.gameObject;
                    }
                }
            }
            return null;
        }

        /// <summary>GameObject 的层级路径(含根名)。</summary>
        public static string GetPath(Transform t)
        {
            var path = t.name;
            var p = t.parent;
            while (p != null)
            {
                path = p.name + "/" + path;
                p = p.parent;
            }
            return path;
        }

        /// <summary>按类型名找 Component 类型:先全名,再各程序集全名,最后短名(限 Component 子类)。</summary>
        public static Type FindType(string typeName)
        {
            return TypeFinder.Find(typeName, t => typeof(Component).IsAssignableFrom(t));
        }
    }
}
