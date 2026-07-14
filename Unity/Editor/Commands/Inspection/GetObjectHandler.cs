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
        public string Description => "返回 GameObject 及组件;components[] 可直接作为 set_property.component";
        public string Group => "Inspection";
        public bool CanDisable => true;
        public CommandBatchMode BatchMode => CommandBatchMode.Allowed;

        public object Execute(JObject @params)
        {
            var objRef = @params?["object"]?.ToObject<ObjectRef>();
            var go = SceneObjectResolver.ResolveObject(objRef);
            var filter = @params?["componentTypes"]?.ToObject<string[]>();
            var hasFilter = filter != null && filter.Length > 0;
            var objectInfo = SceneObjectResolver.Describe(go);

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
                    @object = objectInfo,
                    type = full,
                    index = idx,
                    exactType = true,
                    properties = PropertySerializer.SerializeTopLevel(c)
                });
            }

            return new
            {
                @object = objectInfo,
                components = outComps
            };
        }

        public JObject ParamsSchema { get; } = CreateParamsSchema();

        private static JObject CreateParamsSchema()
        {
            var objectRef = SceneObjectResolver.CreateObjectRefSchema();
            objectRef["description"] = "GameObject 引用;path 或 instanceId 至少提供一个。";
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["object"] = objectRef,
                    ["componentTypes"] = new JObject
                    {
                        ["type"] = "array",
                        ["items"] = new JObject { ["type"] = "string", ["minLength"] = 1 },
                        ["description"] = "可选组件类型名过滤。"
                    }
                },
                ["required"] = new JArray("object")
            };
        }
    }
}
