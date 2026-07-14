using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace AgentBridge
{
    /// <summary>
    /// 把组件的**顶层**可序列化属性转 JSON(不递归深入嵌套)。基本类型给值;
    /// 对象/资产引用渲染为 资源路径 或 ObjectRef;复杂/不支持类型给类型名占位。
    /// 对应 cmd-inspection design D1。
    /// </summary>
    public static class PropertySerializer
    {
        private static readonly PropertyInfo GradientValueProperty = typeof(SerializedProperty).GetProperty(
            "gradientValue", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        public static JObject SerializeTopLevel(Component comp)
        {
            return SerializeTopLevel((Object)comp);
        }

        /// <summary>把任意 UnityEngine.Object(含资产与 AssetImporter)的顶层可见序列化属性转 JSON。</summary>
        public static JObject SerializeTopLevel(Object target)
        {
            var result = new JObject();
            if (target == null)
            {
                return result;
            }
            using (var so = new SerializedObject(target))
            {
                var prop = so.GetIterator();
                var enter = true;
                while (prop.NextVisible(enter))
                {
                    enter = false; // 只遍历顶层,不下钻
                    if (prop.name == "m_Script")
                    {
                        continue;
                    }
                    result[prop.name] = SerializeValue(prop);
                }
            }
            return result;
        }

        private static JToken SerializeValue(SerializedProperty p)
        {
            switch (p.propertyType)
            {
                case SerializedPropertyType.Integer: return SerializeInteger(p);
                case SerializedPropertyType.Boolean: return p.boolValue;
                case SerializedPropertyType.Float: return p.doubleValue;
                case SerializedPropertyType.String: return p.stringValue;
                case SerializedPropertyType.Enum:
                    return (p.enumValueIndex >= 0 && p.enumValueIndex < p.enumDisplayNames.Length)
                        ? (JToken)p.enumDisplayNames[p.enumValueIndex]
                        : new JObject { ["enumValueFlag"] = p.enumValueFlag };
                case SerializedPropertyType.Vector2: return ToJ(p.vector2Value);
                case SerializedPropertyType.Vector3: return ToJ(p.vector3Value);
                case SerializedPropertyType.Vector4: return ToJ(p.vector4Value);
                case SerializedPropertyType.Quaternion:
                    var q = p.quaternionValue;
                    return new JObject { ["x"] = q.x, ["y"] = q.y, ["z"] = q.z, ["w"] = q.w };
                case SerializedPropertyType.Color:
                    var c = p.colorValue;
                    return new JObject { ["r"] = c.r, ["g"] = c.g, ["b"] = c.b, ["a"] = c.a };
                case SerializedPropertyType.Rect:
                    var r = p.rectValue;
                    return new JObject { ["x"] = r.x, ["y"] = r.y, ["width"] = r.width, ["height"] = r.height };
                case SerializedPropertyType.Bounds:
                    var b = p.boundsValue;
                    return new JObject { ["center"] = ToJ(b.center), ["size"] = ToJ(b.size) };
                case SerializedPropertyType.Vector2Int: return ToJ(p.vector2IntValue);
                case SerializedPropertyType.Vector3Int: return ToJ(p.vector3IntValue);
                case SerializedPropertyType.RectInt:
                    var ri = p.rectIntValue;
                    return new JObject { ["x"] = ri.x, ["y"] = ri.y, ["width"] = ri.width, ["height"] = ri.height };
                case SerializedPropertyType.BoundsInt:
                    var bi = p.boundsIntValue;
                    return new JObject { ["position"] = ToJ(bi.position), ["size"] = ToJ(bi.size) };
                case SerializedPropertyType.AnimationCurve: return SerializeCurve(p.animationCurveValue);
                case SerializedPropertyType.Gradient: return SerializeGradient(GetGradient(p));
                case SerializedPropertyType.ObjectReference:
                    return SerializeRef(p.objectReferenceValue);
                default:
                    return p.propertyType.ToString(); // 复杂/嵌套类型不展开,给类型名占位
            }
        }

        private static JToken SerializeInteger(SerializedProperty p)
        {
#if UNITY_2022_1_OR_NEWER
            if (p.numericType == SerializedPropertyNumericType.UInt64)
            {
                return JToken.FromObject(p.ulongValue);
            }
#endif
            return JToken.FromObject(p.longValue);
        }

        private static JToken SerializeRef(Object o)
        {
            if (o == null)
            {
                return JValue.CreateNull();
            }

            var assetPath = AssetDatabase.GetAssetPath(o);
            if (!string.IsNullOrEmpty(assetPath))
            {
                var assetRef = new JObject { ["assetPath"] = assetPath, ["type"] = o.GetType().FullName };
                if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(o, out var guid, out long localId))
                {
                    assetRef["guid"] = guid;
                    assetRef["localId"] = localId;
                }
                return assetRef;
            }

            var jo = new JObject
            {
                ["instanceId"] = o.GetInstanceID(),
                ["name"] = o.name,
                ["type"] = o.GetType().FullName
            };
            if (o is GameObject g)
            {
                jo["path"] = SceneObjectResolver.GetPath(g.transform);
                jo["scenePath"] = g.scene.path;
            }
            else if (o is Component comp)
            {
                jo["path"] = SceneObjectResolver.GetPath(comp.transform);
                jo["scenePath"] = comp.gameObject.scene.path;
                jo["componentType"] = comp.GetType().FullName;
                jo["componentExactType"] = true;
                var sameType = SceneObjectResolver.GetExactComponents(comp.gameObject, comp.GetType());
                jo["componentIndex"] = System.Array.IndexOf(sameType, comp);
            }
            return jo;
        }

        private static JObject SerializeCurve(AnimationCurve curve)
        {
            var keys = new JArray();
            if (curve != null)
            {
                foreach (var key in curve.keys)
                {
                    keys.Add(new JObject
                    {
                        ["time"] = key.time,
                        ["value"] = key.value,
                        ["inTangent"] = key.inTangent,
                        ["outTangent"] = key.outTangent,
                        ["inWeight"] = key.inWeight,
                        ["outWeight"] = key.outWeight,
                        ["weightedMode"] = key.weightedMode.ToString()
                    });
                }
            }
            return new JObject
            {
                ["keys"] = keys,
                ["preWrapMode"] = (curve?.preWrapMode ?? WrapMode.Default).ToString(),
                ["postWrapMode"] = (curve?.postWrapMode ?? WrapMode.Default).ToString()
            };
        }

        private static JObject SerializeGradient(Gradient gradient)
        {
            var colors = new JArray();
            var alphas = new JArray();
            if (gradient != null)
            {
                foreach (var key in gradient.colorKeys)
                {
                    var color = key.color;
                    colors.Add(new JObject
                    {
                        ["color"] = new JObject { ["r"] = color.r, ["g"] = color.g, ["b"] = color.b, ["a"] = color.a },
                        ["time"] = key.time
                    });
                }
                foreach (var key in gradient.alphaKeys)
                {
                    alphas.Add(new JObject { ["alpha"] = key.alpha, ["time"] = key.time });
                }
            }
            return new JObject
            {
                ["colorKeys"] = colors,
                ["alphaKeys"] = alphas,
                ["mode"] = (gradient?.mode ?? GradientMode.Blend).ToString()
            };
        }

        private static Gradient GetGradient(SerializedProperty property)
        {
            // gradientValue is internal in Unity 2021.3 and public in newer releases.
            // Reflection keeps the package compatible with its declared 2021.3 minimum.
            return GradientValueProperty?.GetValue(property, null) as Gradient;
        }

        private static JObject ToJ(Vector2 v)
        {
            return new JObject { ["x"] = v.x, ["y"] = v.y };
        }

        private static JObject ToJ(Vector3 v)
        {
            return new JObject { ["x"] = v.x, ["y"] = v.y, ["z"] = v.z };
        }

        private static JObject ToJ(Vector4 v)
        {
            return new JObject { ["x"] = v.x, ["y"] = v.y, ["z"] = v.z, ["w"] = v.w };
        }

        private static JObject ToJ(Vector2Int v)
        {
            return new JObject { ["x"] = v.x, ["y"] = v.y };
        }

        private static JObject ToJ(Vector3Int v)
        {
            return new JObject { ["x"] = v.x, ["y"] = v.y, ["z"] = v.z };
        }
    }
}
