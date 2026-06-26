using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace AgentBridge
{
    /// <summary>
    /// get_object(只读):返回某 GameObject 的组件及其顶层属性。
    /// params.object 必填(ObjectRef);params.componentTypes 可选(全名或短名过滤)。
    /// </summary>
    public sealed class GetObjectHandler : ICommandHandler
    {
        public string Command => "get_object";
        public string Description => "返回某 GameObject 的组件及其顶层属性;params.object 必填,componentTypes 可选过滤";

        public object Execute(JObject @params)
        {
            var objRef = @params?["object"]?.ToObject<ObjectRef>();
            var go = SceneObjectResolver.ResolveObject(objRef);
            var filter = @params?["componentTypes"]?.ToObject<string[]>();
            var hasFilter = filter != null && filter.Length > 0;

            var typeCounts = new Dictionary<string, int>();
            var outComps = new List<object>();
            foreach (var c in go.GetComponents<Component>())
            {
                if (c == null)
                {
                    continue; // 丢失脚本的组件
                }
                var full = c.GetType().FullName;
                var idx = typeCounts.TryGetValue(full, out var n) ? n : 0;
                typeCounts[full] = idx + 1;

                if (hasFilter && !filter.Contains(full) && !filter.Contains(c.GetType().Name))
                {
                    continue;
                }

                outComps.Add(new
                {
                    type = full,
                    index = idx,
                    properties = PropertySerializer.SerializeTopLevel(c)
                });
            }

            return new
            {
                @object = new
                {
                    name = go.name,
                    path = SceneObjectResolver.GetPath(go.transform),
                    instanceId = go.GetInstanceID(),
                    active = go.activeSelf
                },
                components = outComps
            };
        }

        public JObject GetParamsSchema()
        {
            return JObject.Parse(
                @"{""type"":""object"",""properties"":{""object"":{""type"":""object""},""componentTypes"":{""type"":""array"",""items"":{""type"":""string""}}},""required"":[""object""]}");
        }
    }
}
