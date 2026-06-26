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
        public static JObject SerializeTopLevel(Component comp)
        {
            var result = new JObject();
            using (var so = new SerializedObject(comp))
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
                case SerializedPropertyType.Integer: return p.intValue;
                case SerializedPropertyType.Boolean: return p.boolValue;
                case SerializedPropertyType.Float: return p.floatValue;
                case SerializedPropertyType.String: return p.stringValue;
                case SerializedPropertyType.Enum:
                    return (p.enumValueIndex >= 0 && p.enumValueIndex < p.enumDisplayNames.Length)
                        ? (JToken)p.enumDisplayNames[p.enumValueIndex] : p.intValue;
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
                case SerializedPropertyType.ObjectReference:
                    return SerializeRef(p.objectReferenceValue);
                default:
                    return p.propertyType.ToString(); // 复杂/嵌套类型不展开,给类型名占位
            }
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
                return new JObject { ["assetPath"] = assetPath, ["type"] = o.GetType().FullName };
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
            }
            else if (o is Component comp)
            {
                jo["path"] = SceneObjectResolver.GetPath(comp.transform);
            }
            return jo;
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
    }
}
