using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace AgentBridge
{
    public sealed class FrameObjectHandler : ICommandHandler
    {
        public string Command => "frame_object";
        public string Description => "在已有 SceneView 中框选对象及子级渲染/碰撞范围;不修改 Selection";
        public string Group => "Mutation";
        public bool CanDisable => true;
        public CommandBatchMode BatchMode => CommandBatchMode.Allowed;

        public async CommandTask<object> ExecuteAsync(JObject @params)
        {
            var go = SceneObjectResolver.ResolveObject(@params?["object"]?.ToObject<ObjectRef>());
            var view = SceneView.lastActiveSceneView ?? Resources.FindObjectsOfTypeAll<SceneView>().FirstOrDefault();
            if (view == null)
            {
                throw new CommandException("SCENE_VIEW_UNAVAILABLE", "当前没有已打开的 SceneView");
            }
            var bounds = CalculateBounds(go);
            var instant = SceneCommandSupport.ReadBool(@params, "instant", true);
            bool framed;
            try
            {
                framed = view.Frame(bounds, instant);
                view.Repaint();
            }
            catch (Exception ex)
            {
                throw new CommandException("FRAME_OBJECT_FAILED", ex.Message);
            }
            if (!framed)
            {
                throw new CommandException("FRAME_OBJECT_FAILED",
                    $"SceneView 无法框选对象:'{go.name}'");
            }
            return new
            {
                framed = true,
                instant,
                @object = SceneObjectResolver.Describe(go),
                bounds = new
                {
                    center = new { x = bounds.center.x, y = bounds.center.y, z = bounds.center.z },
                    size = new { x = bounds.size.x, y = bounds.size.y, z = bounds.size.z }
                }
            };
        }

        private static Bounds CalculateBounds(GameObject go)
        {
            var hasBounds = false;
            var result = new Bounds(go.transform.position, Vector3.one);
            foreach (var renderer in go.GetComponentsInChildren<Renderer>(true))
            {
                Encapsulate(renderer.bounds, ref result, ref hasBounds);
            }
            foreach (var collider in go.GetComponentsInChildren<Collider>(true))
            {
                Encapsulate(collider.bounds, ref result, ref hasBounds);
            }
            foreach (var collider in go.GetComponentsInChildren<Collider2D>(true))
            {
                Encapsulate(collider.bounds, ref result, ref hasBounds);
            }
            foreach (var rect in go.GetComponentsInChildren<RectTransform>(true))
            {
                var corners = new Vector3[4];
                rect.GetWorldCorners(corners);
                foreach (var corner in corners)
                {
                    if (!hasBounds)
                    {
                        result = new Bounds(corner, Vector3.zero);
                        hasBounds = true;
                    }
                    else
                    {
                        result.Encapsulate(corner);
                    }
                }
            }
            if (!IsFinite(result.center) || !IsFinite(result.size))
            {
                throw new CommandException("FRAME_OBJECT_FAILED", "对象 Bounds 包含 NaN/Infinity");
            }
            if (result.size.sqrMagnitude < 0.000001f)
            {
                result.size = Vector3.one;
            }
            return result;
        }

        private static void Encapsulate(Bounds value, ref Bounds result, ref bool hasBounds)
        {
            if (!IsFinite(value.center) || !IsFinite(value.size))
            {
                return;
            }
            if (!hasBounds)
            {
                result = value;
                hasBounds = true;
            }
            else
            {
                result.Encapsulate(value);
            }
        }

        private static bool IsFinite(Vector3 value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        public JObject ParamsSchema { get; } = new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["object"] = SceneObjectResolver.CreateObjectRefSchema(),
                ["instant"] = new JObject { ["type"] = "boolean", ["default"] = true }
            },
            ["required"] = new JArray("object")
        };
    }
}
