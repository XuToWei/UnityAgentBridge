using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace AgentBridge
{
    public sealed class SetSelectionHandler : ICommandHandler
    {
        public string Command => "set_selection";
        public string Description => "原子设置最多 256 个场景对象选择;objects=[] 清空,active 必须属于 objects";
        public string Group => "Mutation";
        public bool CanDisable => true;
        public CommandBatchMode BatchMode => CommandBatchMode.Allowed;

        public async CommandTask<object> ExecuteAsync(JObject @params)
        {
            var refs = (JArray)@params["objects"];
            var resolved = new List<GameObject>(refs.Count);
            var ids = new HashSet<int>();
            foreach (var token in refs)
            {
                var go = SceneObjectResolver.ResolveObject(token.ToObject<ObjectRef>());
                if (ids.Add(go.GetInstanceID()))
                {
                    resolved.Add(go);
                }
            }

            GameObject active = null;
            var activeToken = @params?["active"];
            if (activeToken != null && activeToken.Type != JTokenType.Null)
            {
                active = SceneObjectResolver.ResolveObject(activeToken.ToObject<ObjectRef>());
                if (!ids.Contains(active.GetInstanceID()))
                {
                    throw new CommandException(ErrorCodes.InvalidParams,
                        "active 必须同时出现在 objects 中");
                }
            }
            else if (resolved.Count > 0)
            {
                active = resolved[0];
            }

            // Selection.activeObject=... collapses a multi-selection to one object. Unity sets
            // activeObject from the first item when Selection.objects is assigned, so order the
            // desired active object first and perform one atomic assignment.
            var ordered = active == null
                ? resolved
                : new[] { active }.Concat(resolved.Where(item => item != active)).ToList();
            Selection.objects = ordered.Cast<UnityEngine.Object>().ToArray();
            return new
            {
                count = Selection.gameObjects.Length,
                selection = Selection.gameObjects.Select(SceneObjectResolver.Describe).ToArray(),
                active = Selection.activeGameObject == null
                    ? null
                    : SceneObjectResolver.Describe(Selection.activeGameObject)
            };
        }

        public JObject ParamsSchema { get; } = new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["objects"] = new JObject
                {
                    ["type"] = "array", ["minItems"] = 0, ["maxItems"] = 256,
                    ["items"] = SceneObjectResolver.CreateObjectRefSchema()
                },
                ["active"] = SceneObjectResolver.CreateObjectRefSchema()
            },
            ["required"] = new JArray("objects")
        };
    }
}
