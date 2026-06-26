using System;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace AgentBridge
{
    /// <summary>
    /// 把 JSON 值写入一个**顶层或嵌套**的 `SerializedProperty`(就地修改,调用方负责
    /// ApplyModifiedProperties)。是 PropertySerializer 的对偶:基本类型/向量/颜色/枚举/对象引用。
    /// 类型不符或不支持 → 抛 CommandException(PROPERTY_TYPE_MISMATCH)。对应 cmd-mutation design D3/D7。
    /// </summary>
    public static class PropertyDeserializer
    {
        public static void Apply(SerializedProperty p, JToken value)
        {
            switch (p.propertyType)
            {
                case SerializedPropertyType.Integer: p.intValue = Int(value, p); break;
                case SerializedPropertyType.Boolean: p.boolValue = Bool(value, p); break;
                case SerializedPropertyType.Float: p.floatValue = Float(value, p); break;
                case SerializedPropertyType.String: p.stringValue = Str(value); break;
                case SerializedPropertyType.Enum: p.enumValueIndex = EnumIndex(value, p); break;
                case SerializedPropertyType.Vector2: p.vector2Value = new Vector2(F(value, "x", p), F(value, "y", p)); break;
                case SerializedPropertyType.Vector3: p.vector3Value = new Vector3(F(value, "x", p), F(value, "y", p), F(value, "z", p)); break;
                case SerializedPropertyType.Vector4: p.vector4Value = new Vector4(F(value, "x", p), F(value, "y", p), F(value, "z", p), F(value, "w", p)); break;
                case SerializedPropertyType.Quaternion: p.quaternionValue = new Quaternion(F(value, "x", p), F(value, "y", p), F(value, "z", p), F(value, "w", p)); break;
                case SerializedPropertyType.Color: p.colorValue = new Color(F(value, "r", p), F(value, "g", p), F(value, "b", p), Opt(value, "a", 1f)); break;
                case SerializedPropertyType.Rect: p.rectValue = new Rect(F(value, "x", p), F(value, "y", p), F(value, "width", p), F(value, "height", p)); break;
                case SerializedPropertyType.Bounds:
                    var center = Child(value, "center", p);
                    var size = Child(value, "size", p);
                    p.boundsValue = new Bounds(
                        new Vector3(F(center, "x", p), F(center, "y", p), F(center, "z", p)),
                        new Vector3(F(size, "x", p), F(size, "y", p), F(size, "z", p)));
                    break;
                case SerializedPropertyType.ObjectReference: p.objectReferenceValue = ResolveRef(value, p); break;
                default:
                    throw new CommandException(MutationErrorCodes.PropertyTypeMismatch,
                        $"属性 '{p.propertyPath}' 类型 {p.propertyType} 不支持写入");
            }
        }

        // —— 标量读取(类型不符统一抛 PROPERTY_TYPE_MISMATCH)——
        private static int Int(JToken v, SerializedProperty p)
        {
            return Require(v, p, JTokenType.Integer, JTokenType.Float).Value<int>();
        }

        private static float Float(JToken v, SerializedProperty p)
        {
            return Require(v, p, JTokenType.Integer, JTokenType.Float).Value<float>();
        }

        private static bool Bool(JToken v, SerializedProperty p)
        {
            return Require(v, p, JTokenType.Boolean).Value<bool>();
        }

        private static string Str(JToken v)
        {
            return v == null || v.Type == JTokenType.Null ? "" : v.Value<string>();
        }

        private static int EnumIndex(JToken v, SerializedProperty p)
        {
            if (v != null && (v.Type == JTokenType.Integer))
            {
                return v.Value<int>(); // 直接给索引
            }
            if (v != null && v.Type == JTokenType.String)
            {
                var name = v.Value<string>();
                var names = p.enumDisplayNames;
                for (int i = 0; i < names.Length; i++)
                {
                    if (names[i] == name)
                    {
                        return i;
                    }
                }
                // 退一步:匹配内部枚举名
                var raw = p.enumNames;
                for (int i = 0; i < raw.Length; i++)
                {
                    if (raw[i] == name)
                    {
                        return i;
                    }
                }
            }
            throw new CommandException(MutationErrorCodes.PropertyTypeMismatch,
                $"属性 '{p.propertyPath}' 是枚举,value 需为索引或有效枚举名");
        }

        // —— 结构字段读取 ——
        private static float F(JToken v, string key, SerializedProperty p)
        {
            var t = Child(v, key, p);
            return Require(t, p, JTokenType.Integer, JTokenType.Float).Value<float>();
        }

        private static float Opt(JToken v, string key, float fallback)
        {
            var t = v?[key];
            return (t == null || t.Type == JTokenType.Null) ? fallback : t.Value<float>();
        }

        private static JToken Child(JToken v, string key, SerializedProperty p)
        {
            var t = v?[key];
            if (t == null)
            {
                throw new CommandException(MutationErrorCodes.PropertyTypeMismatch,
                    $"属性 '{p.propertyPath}' 需要对象字段 '{key}'");
            }
            return t;
        }

        private static JToken Require(JToken v, SerializedProperty p, params JTokenType[] allowed)
        {
            if (v != null)
            {
                foreach (var a in allowed)
                {
                    if (v.Type == a)
                    {
                        return v;
                    }
                }
            }
            throw new CommandException(MutationErrorCodes.PropertyTypeMismatch,
                $"属性 '{p.propertyPath}' value 类型不符(需 {string.Join("/", allowed)})");
        }

        /// <summary>解析对象引用值:{assetPath} → 资源;{instanceId} 或 {path} → 场景对象;null → 清空。</summary>
        private static UnityEngine.Object ResolveRef(JToken v, SerializedProperty p)
        {
            if (v == null || v.Type == JTokenType.Null)
            {
                return null;
            }
            if (v.Type != JTokenType.Object)
            {
                throw new CommandException(MutationErrorCodes.PropertyTypeMismatch,
                    $"属性 '{p.propertyPath}' 是对象引用,value 需为 {{assetPath}} 或 {{instanceId/path}} 或 null");
            }

            var assetPath = v["assetPath"]?.Value<string>();
            if (!string.IsNullOrEmpty(assetPath))
            {
                var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
                if (asset == null)
                {
                    throw new CommandException(RefErrorCodes.ObjectNotFound, $"资源路径未找到:'{assetPath}'");
                }
                return asset;
            }

            var instanceId = v["instanceId"]?.ToObject<int?>();
            if (instanceId.HasValue)
            {
#if UNITY_6000_0_OR_NEWER
                var obj = EditorUtility.EntityIdToObject(instanceId.Value);
#else
                var obj = EditorUtility.InstanceIDToObject(instanceId.Value);
#endif
                if (obj != null)
                {
                    return obj;
                }
            }

            var path = v["path"]?.Value<string>();
            if (!string.IsNullOrEmpty(path))
            {
                var go = SceneObjectResolver.FindByPath(path);
                if (go != null)
                {
                    return go;
                }
            }

            throw new CommandException(RefErrorCodes.ObjectNotFound,
                $"属性 '{p.propertyPath}' 的引用无法解析(assetPath/instanceId/path 均未命中)");
        }
    }
}
