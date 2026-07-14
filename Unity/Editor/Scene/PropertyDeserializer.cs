using System;
using System.Reflection;
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
        private static readonly PropertyInfo GradientValueProperty = typeof(SerializedProperty).GetProperty(
            "gradientValue", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        public static void Apply(SerializedProperty p, JToken value)
        {
            if (!p.editable)
            {
                throw new CommandException(MutationErrorCodes.PropertyTypeMismatch,
                    $"属性 '{p.propertyPath}' 是只读属性,不能写入");
            }
            switch (p.propertyType)
            {
                case SerializedPropertyType.Integer: SetInteger(p, value); break;
                case SerializedPropertyType.Boolean: p.boolValue = Bool(value, p); break;
                case SerializedPropertyType.Float: p.doubleValue = Double(value, p); break;
                case SerializedPropertyType.String: p.stringValue = Str(value); break;
                case SerializedPropertyType.Enum: SetEnum(value, p); break;
                case SerializedPropertyType.Vector2: p.vector2Value = new Vector2(F(value, "x", p), F(value, "y", p)); break;
                case SerializedPropertyType.Vector3: p.vector3Value = new Vector3(F(value, "x", p), F(value, "y", p), F(value, "z", p)); break;
                case SerializedPropertyType.Vector4: p.vector4Value = new Vector4(F(value, "x", p), F(value, "y", p), F(value, "z", p), F(value, "w", p)); break;
                case SerializedPropertyType.Quaternion: p.quaternionValue = new Quaternion(F(value, "x", p), F(value, "y", p), F(value, "z", p), F(value, "w", p)); break;
                case SerializedPropertyType.Color: p.colorValue = new Color(F(value, "r", p), F(value, "g", p), F(value, "b", p), Opt(value, "a", 1f, p)); break;
                case SerializedPropertyType.Rect: p.rectValue = new Rect(F(value, "x", p), F(value, "y", p), F(value, "width", p), F(value, "height", p)); break;
                case SerializedPropertyType.Bounds:
                    var center = Child(value, "center", p);
                    var size = Child(value, "size", p);
                    p.boundsValue = new Bounds(
                        new Vector3(F(center, "x", p), F(center, "y", p), F(center, "z", p)),
                        new Vector3(F(size, "x", p), F(size, "y", p), F(size, "z", p)));
                    break;
                case SerializedPropertyType.Vector2Int:
                    p.vector2IntValue = new Vector2Int(I(value, "x", p), I(value, "y", p));
                    break;
                case SerializedPropertyType.Vector3Int:
                    p.vector3IntValue = new Vector3Int(I(value, "x", p), I(value, "y", p), I(value, "z", p));
                    break;
                case SerializedPropertyType.RectInt:
                    p.rectIntValue = new RectInt(I(value, "x", p), I(value, "y", p), I(value, "width", p), I(value, "height", p));
                    break;
                case SerializedPropertyType.BoundsInt:
                    var position = Child(value, "position", p);
                    var intSize = Child(value, "size", p);
                    p.boundsIntValue = new BoundsInt(
                        new Vector3Int(I(position, "x", p), I(position, "y", p), I(position, "z", p)),
                        new Vector3Int(I(intSize, "x", p), I(intSize, "y", p), I(intSize, "z", p)));
                    break;
                case SerializedPropertyType.AnimationCurve: p.animationCurveValue = Curve(value, p); break;
                case SerializedPropertyType.Gradient: SetGradient(p, GradientValue(value, p)); break;
                case SerializedPropertyType.ObjectReference:
                    var resolved = ResolveRef(value, p);
                    try
                    {
                        p.objectReferenceValue = resolved;
                    }
                    catch (System.Exception ex)
                    {
                        throw new CommandException(MutationErrorCodes.PropertyTypeMismatch,
                            $"属性 '{p.propertyPath}' 不接受 {resolved?.GetType().FullName ?? "null"}:{ex.Message}");
                    }
                    if (resolved != null && p.objectReferenceValue != resolved)
                    {
                        throw new CommandException(MutationErrorCodes.PropertyTypeMismatch,
                            $"属性 '{p.propertyPath}' 不接受对象类型 {resolved.GetType().FullName}");
                    }
                    break;
                default:
                    throw new CommandException(MutationErrorCodes.PropertyTypeMismatch,
                        $"属性 '{p.propertyPath}' 类型 {p.propertyType} 不支持写入");
            }
        }

        // —— 标量读取(类型不符统一抛 PROPERTY_TYPE_MISMATCH)——
        private static void SetInteger(SerializedProperty p, JToken value)
        {
#if UNITY_2022_1_OR_NEWER
            if (p.numericType == SerializedPropertyNumericType.UInt64)
            {
                try
                {
                    p.ulongValue = Require(value, p, JTokenType.Integer).Value<ulong>();
                    return;
                }
                catch (System.Exception ex) when (!(ex is CommandException))
                {
                    throw new CommandException(MutationErrorCodes.PropertyTypeMismatch,
                        $"属性 '{p.propertyPath}' 的无符号整数越界:{ex.Message}");
                }
            }
#endif
            try
            {
                p.longValue = Require(value, p, JTokenType.Integer).Value<long>();
            }
            catch (System.Exception ex) when (!(ex is CommandException))
            {
                throw new CommandException(MutationErrorCodes.PropertyTypeMismatch,
                    $"属性 '{p.propertyPath}' 的整数越界:{ex.Message}");
            }
        }

        private static double Double(JToken v, SerializedProperty p)
        {
            var token = Require(v, p, JTokenType.Integer, JTokenType.Float);
            try
            {
                var result = token.Value<double>();
                if (double.IsNaN(result) || double.IsInfinity(result))
                {
                    throw new OverflowException("number must be finite");
                }
                return result;
            }
            catch (Exception ex) when (!(ex is CommandException))
            {
                throw new CommandException(MutationErrorCodes.PropertyTypeMismatch,
                    $"属性 '{p.propertyPath}' 的 number 无效或越界:{ex.Message}");
            }
        }

        private static bool Bool(JToken v, SerializedProperty p)
        {
            return Require(v, p, JTokenType.Boolean).Value<bool>();
        }

        private static string Str(JToken v)
        {
            if (v == null || v.Type == JTokenType.Null)
            {
                return "";
            }
            if (v.Type != JTokenType.String)
            {
                throw new CommandException(MutationErrorCodes.PropertyTypeMismatch,
                    "string 属性的 value 必须是 string 或 null");
            }
            return v.Value<string>();
        }

        private static void SetEnum(JToken v, SerializedProperty p)
        {
            if (v != null && (v.Type == JTokenType.Integer))
            {
                var index = Int32(v, p, "enum index");
                if (index >= 0 && index < p.enumDisplayNames.Length)
                {
                    p.enumValueIndex = index;
                    return;
                }
                throw new CommandException(MutationErrorCodes.PropertyTypeMismatch,
                    $"属性 '{p.propertyPath}' 的枚举索引 {index} 越界(0..{p.enumDisplayNames.Length - 1})");
            }
            if (v is JObject flagObject && flagObject["enumValueFlag"]?.Type == JTokenType.Integer)
            {
                p.enumValueFlag = Int32(flagObject["enumValueFlag"], p, "enumValueFlag");
                return;
            }
            if (v != null && v.Type == JTokenType.String)
            {
                var name = v.Value<string>();
                var names = p.enumDisplayNames;
                for (int i = 0; i < names.Length; i++)
                {
                    if (names[i] == name)
                    {
                        p.enumValueIndex = i;
                        return;
                    }
                }
                // 退一步:匹配内部枚举名
                var raw = p.enumNames;
                for (int i = 0; i < raw.Length; i++)
                {
                    if (raw[i] == name)
                    {
                        p.enumValueIndex = i;
                        return;
                    }
                }
            }
            throw new CommandException(MutationErrorCodes.PropertyTypeMismatch,
                $"属性 '{p.propertyPath}' 是枚举,value 需为索引或有效枚举名");
        }

        // —— 结构字段读取 ——
        private static float F(JToken v, string key, SerializedProperty p)
        {
            return FiniteFloat(Child(v, key, p), p, key);
        }

        private static int I(JToken v, string key, SerializedProperty p)
        {
            return Int32(Require(Child(v, key, p), p, JTokenType.Integer), p, key);
        }

        private static float Opt(JToken v, string key, float fallback, SerializedProperty p)
        {
            var t = v?[key];
            return (t == null || t.Type == JTokenType.Null)
                ? fallback
                : FiniteFloat(t, p, key);
        }

        private static JToken Child(JToken v, string key, SerializedProperty p)
        {
            if (!(v is JObject obj))
            {
                throw new CommandException(MutationErrorCodes.PropertyTypeMismatch,
                    $"属性 '{p.propertyPath}' 的 value 必须是 object");
            }
            var t = obj[key];
            if (t == null)
            {
                throw new CommandException(MutationErrorCodes.PropertyTypeMismatch,
                    $"属性 '{p.propertyPath}' 需要对象字段 '{key}'");
            }
            return t;
        }

        private static float FiniteFloat(JToken token, SerializedProperty p, string name)
        {
            token = Require(token, p, JTokenType.Integer, JTokenType.Float);
            try
            {
                var number = token.Value<double>();
                if (double.IsNaN(number) || double.IsInfinity(number) ||
                    number < -float.MaxValue || number > float.MaxValue)
                {
                    throw new OverflowException("number must be finite and fit Single");
                }
                return (float)number;
            }
            catch (Exception ex) when (!(ex is CommandException))
            {
                throw new CommandException(MutationErrorCodes.PropertyTypeMismatch,
                    $"属性 '{p.propertyPath}' 的 {name} 无效或越界:{ex.Message}");
            }
        }

        private static int Int32(JToken token, SerializedProperty p, string name)
        {
            try
            {
                var number = token.Value<long>();
                if (number < int.MinValue || number > int.MaxValue)
                {
                    throw new OverflowException("integer must fit Int32");
                }
                return (int)number;
            }
            catch (Exception ex) when (!(ex is CommandException))
            {
                throw new CommandException(MutationErrorCodes.PropertyTypeMismatch,
                    $"属性 '{p.propertyPath}' 的 {name} 无效或越界:{ex.Message}");
            }
        }

        private static long Int64(JToken token, SerializedProperty p, string name)
        {
            try
            {
                return token.Value<long>();
            }
            catch (Exception ex) when (!(ex is CommandException))
            {
                throw new CommandException(MutationErrorCodes.PropertyTypeMismatch,
                    $"属性 '{p.propertyPath}' 的 {name} 无效或越界:{ex.Message}");
            }
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

        private static AnimationCurve Curve(JToken value, SerializedProperty p)
        {
            if (!(value is JObject obj) || !(obj["keys"] is JArray keyTokens))
            {
                throw new CommandException(MutationErrorCodes.PropertyTypeMismatch,
                    $"属性 '{p.propertyPath}' 的 AnimationCurve 需要 keys 数组");
            }

            var keys = new Keyframe[keyTokens.Count];
            for (var i = 0; i < keyTokens.Count; i++)
            {
                if (!(keyTokens[i] is JObject key))
                {
                    throw new CommandException(MutationErrorCodes.PropertyTypeMismatch,
                        $"属性 '{p.propertyPath}' 的 keys[{i}] 必须是 object");
                }
                var frame = new Keyframe(
                    Number(key["time"], p, "time"),
                    Number(key["value"], p, "value"),
                    Number(key["inTangent"], p, "inTangent", 0f),
                    Number(key["outTangent"], p, "outTangent", 0f));
                frame.inWeight = Number(key["inWeight"], p, "inWeight", 0f);
                frame.outWeight = Number(key["outWeight"], p, "outWeight", 0f);
                frame.weightedMode = ParseEnum(key["weightedMode"], WeightedMode.None, p, "weightedMode");
                keys[i] = frame;
            }

            var curve = new AnimationCurve(keys)
            {
                preWrapMode = ParseEnum(obj["preWrapMode"], WrapMode.Default, p, "preWrapMode"),
                postWrapMode = ParseEnum(obj["postWrapMode"], WrapMode.Default, p, "postWrapMode")
            };
            return curve;
        }

        private static Gradient GradientValue(JToken value, SerializedProperty p)
        {
            if (!(value is JObject obj) || !(obj["colorKeys"] is JArray colors) ||
                !(obj["alphaKeys"] is JArray alphas) || colors.Count < 2 || alphas.Count < 2)
            {
                throw new CommandException(MutationErrorCodes.PropertyTypeMismatch,
                    $"属性 '{p.propertyPath}' 的 Gradient 至少需要 2 个 colorKeys 和 2 个 alphaKeys");
            }

            var colorKeys = new GradientColorKey[colors.Count];
            for (var i = 0; i < colors.Count; i++)
            {
                if (!(colors[i] is JObject item) || !(item["color"] is JObject color))
                {
                    throw new CommandException(MutationErrorCodes.PropertyTypeMismatch,
                        $"属性 '{p.propertyPath}' 的 colorKeys[{i}] 格式无效");
                }
                colorKeys[i] = new GradientColorKey(
                    new Color(
                        Number(color["r"], p, "r"),
                        Number(color["g"], p, "g"),
                        Number(color["b"], p, "b"),
                        Number(color["a"], p, "a", 1f)),
                    Number(item["time"], p, "time"));
            }

            var alphaKeys = new GradientAlphaKey[alphas.Count];
            for (var i = 0; i < alphas.Count; i++)
            {
                if (!(alphas[i] is JObject item))
                {
                    throw new CommandException(MutationErrorCodes.PropertyTypeMismatch,
                        $"属性 '{p.propertyPath}' 的 alphaKeys[{i}] 格式无效");
                }
                alphaKeys[i] = new GradientAlphaKey(
                    Number(item["alpha"], p, "alpha"),
                    Number(item["time"], p, "time"));
            }

            var gradient = new Gradient
            {
                mode = ParseEnum(obj["mode"], GradientMode.Blend, p, "mode")
            };
            gradient.SetKeys(colorKeys, alphaKeys);
            return gradient;
        }

        private static void SetGradient(SerializedProperty property, Gradient gradient)
        {
            if (GradientValueProperty == null || !GradientValueProperty.CanWrite)
            {
                throw new CommandException(MutationErrorCodes.PropertyTypeMismatch,
                    $"当前 Unity 版本不支持写入 Gradient 属性 '{property.propertyPath}'");
            }
            try
            {
                GradientValueProperty.SetValue(property, gradient, null);
            }
            catch (TargetInvocationException ex)
            {
                throw new CommandException(MutationErrorCodes.PropertyTypeMismatch,
                    $"写入 Gradient 属性 '{property.propertyPath}' 失败:{ex.InnerException?.Message ?? ex.Message}");
            }
            catch (Exception ex)
            {
                throw new CommandException(MutationErrorCodes.PropertyTypeMismatch,
                    $"写入 Gradient 属性 '{property.propertyPath}' 失败:{ex.Message}");
            }
        }

        private static float Number(JToken token, SerializedProperty p, string name, float? fallback = null)
        {
            if ((token == null || token.Type == JTokenType.Null) && fallback.HasValue)
            {
                return fallback.Value;
            }
            if (token == null || (token.Type != JTokenType.Integer && token.Type != JTokenType.Float))
            {
                throw new CommandException(MutationErrorCodes.PropertyTypeMismatch,
                    $"属性 '{p.propertyPath}' 的 {name} 必须是 number");
            }
            return FiniteFloat(token, p, name);
        }

        private static T ParseEnum<T>(JToken token, T fallback, SerializedProperty p, string name)
            where T : struct
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return fallback;
            }
            if (token.Type == JTokenType.String && Enum.TryParse(token.Value<string>(), true, out T parsed) &&
                Enum.IsDefined(typeof(T), parsed))
            {
                return parsed;
            }
            if (token.Type == JTokenType.Integer)
            {
                var boxed = (T)Enum.ToObject(typeof(T), Int32(token, p, name));
                if (Enum.IsDefined(typeof(T), boxed))
                {
                    return boxed;
                }
            }
            throw new CommandException(MutationErrorCodes.PropertyTypeMismatch,
                $"属性 '{p.propertyPath}' 的 {name} 不是有效 {typeof(T).Name}");
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

            var objectValue = (JObject)v;
            foreach (var stringField in new[]
                     { "assetPath", "guid", "type", "name", "path", "scenePath", "componentType" })
            {
                OptionalString(objectValue, stringField, p);
            }
            var componentExactTypeToken = objectValue["componentExactType"];
            if (componentExactTypeToken != null &&
                componentExactTypeToken.Type != JTokenType.Boolean)
            {
                throw new CommandException(MutationErrorCodes.PropertyTypeMismatch,
                    $"属性 '{p.propertyPath}' 的 componentExactType 必须是 boolean");
            }
            var assetPath = OptionalString(objectValue, "assetPath", p);
            if (!string.IsNullOrEmpty(assetPath))
            {
                var typeHint = OptionalString(objectValue, "type", p);
                UnityEngine.Object asset = null;
                var localIdToken = v["localId"];
                if (localIdToken != null && localIdToken.Type != JTokenType.Integer)
                {
                    throw new CommandException(MutationErrorCodes.PropertyTypeMismatch,
                        $"属性 '{p.propertyPath}' 的 localId 必须是 integer");
                }
                var guidHint = OptionalString(objectValue, "guid", p);
                var actualGuid = AssetDatabase.AssetPathToGUID(assetPath);
                if (!string.IsNullOrEmpty(guidHint) &&
                    !string.Equals(guidHint, actualGuid, StringComparison.Ordinal))
                {
                    throw new CommandException(RefErrorCodes.ObjectRefStale,
                        $"资源引用 GUID 与路径不一致:'{assetPath}',期望 {guidHint},实际 {actualGuid}");
                }
                if (localIdToken?.Type == JTokenType.Integer)
                {
                    var wantedLocalId = Int64(localIdToken, p, "localId");
                    foreach (var candidate in AssetDatabase.LoadAllAssetsAtPath(assetPath))
                    {
                        if (candidate != null &&
                            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(candidate, out var guid, out long localId) &&
                            localId == wantedLocalId &&
                            (string.IsNullOrEmpty(guidHint) || string.Equals(guidHint, guid, StringComparison.Ordinal)))
                        {
                            asset = candidate;
                            break;
                        }
                    }
                }
                else
                {
                    asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
                }
                if (asset == null)
                {
                    throw new CommandException(RefErrorCodes.ObjectNotFound,
                        $"资源引用未找到:'{assetPath}' localId={localIdToken}");
                }
                if (!string.IsNullOrEmpty(typeHint) &&
                    typeHint != asset.GetType().FullName && typeHint != asset.GetType().Name)
                {
                    throw new CommandException(RefErrorCodes.ObjectRefStale,
                        $"资源引用类型与路径不一致:'{assetPath}',期望 {typeHint},实际 {asset.GetType().FullName}");
                }
                return asset;
            }

            int? instanceId = null;
            if (v["instanceId"] != null)
            {
                if (v["instanceId"].Type != JTokenType.Integer)
                {
                    throw new CommandException(MutationErrorCodes.PropertyTypeMismatch,
                        $"属性 '{p.propertyPath}' 的 instanceId 必须是 integer");
                }
                instanceId = Int32(v["instanceId"], p, "instanceId");
            }
            if (instanceId.HasValue)
            {
#if UNITY_6000_0_OR_NEWER
                var obj = EditorUtility.EntityIdToObject(instanceId.Value);
#else
                var obj = EditorUtility.InstanceIDToObject(instanceId.Value);
#endif
                if (obj != null)
                {
                    if (MatchesReferenceHints(obj, objectValue))
                    {
                        return obj;
                    }
                    throw new CommandException(RefErrorCodes.ObjectRefStale,
                        $"属性 '{p.propertyPath}' 的 instanceId={instanceId.Value} 与引用提示不一致");
                }
            }

            var path = OptionalString(objectValue, "path", p);
            if (!string.IsNullOrEmpty(path))
            {
                var objectRef = new ObjectRef
                {
                    Path = path,
                    ScenePath = OptionalString(objectValue, "scenePath", p)
                };
                var componentType = OptionalString(objectValue, "componentType", p);
                if (!string.IsNullOrEmpty(componentType))
                {
                    return SceneObjectResolver.ResolveComponent(new ComponentRef
                    {
                        Object = objectRef,
                        Type = componentType,
                        ExactType = componentExactTypeToken?.Value<bool>(),
                        Index = v["componentIndex"] == null
                            ? 0
                            : Int32(Require(v["componentIndex"], p, JTokenType.Integer), p, "componentIndex")
                    });
                }
                return SceneObjectResolver.ResolveObject(objectRef);
            }

            throw new CommandException(RefErrorCodes.ObjectNotFound,
                $"属性 '{p.propertyPath}' 的引用无法解析(assetPath/instanceId/path 均未命中)");
        }

        private static bool MatchesReferenceHints(UnityEngine.Object obj, JObject value)
        {
            var typeHint = value["type"]?.Value<string>();
            if (!string.IsNullOrEmpty(typeHint) &&
                typeHint != obj.GetType().FullName && typeHint != obj.GetType().Name)
            {
                return false;
            }
            var nameHint = value["name"]?.Value<string>();
            if (!string.IsNullOrEmpty(nameHint) && nameHint != obj.name)
            {
                return false;
            }

            GameObject go;
            if (obj is GameObject gameObject)
            {
                go = gameObject;
                if (!string.IsNullOrEmpty(value["componentType"]?.Value<string>()))
                {
                    return false;
                }
            }
            else if (obj is Component component)
            {
                go = component.gameObject;
                var componentType = value["componentType"]?.Value<string>();
                var exactType = value["componentExactType"]?.Value<bool>() == true;
                var declaredType = string.IsNullOrEmpty(componentType)
                    ? null
                    : SceneObjectResolver.FindType(componentType, out _);
                if (declaredType != null &&
                    (exactType
                        ? component.GetType() != declaredType
                        : !declaredType.IsAssignableFrom(component.GetType())))
                {
                    return false;
                }
                if (declaredType == null && !string.IsNullOrEmpty(componentType) &&
                    componentType != component.GetType().FullName && componentType != component.GetType().Name)
                {
                    return false;
                }
                if (value["componentIndex"] != null)
                {
                    var siblings = exactType
                        ? SceneObjectResolver.GetExactComponents(go, component.GetType())
                        : declaredType != null
                            ? go.GetComponents(declaredType)
                            : SceneObjectResolver.GetExactComponents(go, component.GetType());
                    var actualIndex = Array.IndexOf(siblings, component);
                    int expectedIndex;
                    try
                    {
                        expectedIndex = value["componentIndex"].Value<int>();
                    }
                    catch (Exception)
                    {
                        return false;
                    }
                    if (actualIndex != expectedIndex)
                    {
                        return false;
                    }
                }
            }
            else
            {
                return string.IsNullOrEmpty(value["path"]?.Value<string>());
            }

            var pathHint = value["path"]?.Value<string>();
            if (!SceneObjectResolver.MatchesPathHint(
                    go.transform, pathHint, value["scenePath"]?.Value<string>()))
            {
                return false;
            }
            var sceneHint = value["scenePath"]?.Value<string>();
            if (!string.IsNullOrEmpty(sceneHint) &&
                !string.Equals(sceneHint.Replace('\\', '/'),
                    (go.scene.path ?? "").Replace('\\', '/'),
                    Application.platform == RuntimePlatform.WindowsEditor ||
                    Application.platform == RuntimePlatform.OSXEditor
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal))
            {
                return false;
            }
            return true;
        }

        private static string OptionalString(JObject value, string name, SerializedProperty property)
        {
            var token = value[name];
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }
            if (token.Type != JTokenType.String)
            {
                throw new CommandException(MutationErrorCodes.PropertyTypeMismatch,
                    $"属性 '{property.propertyPath}' 的 {name} 必须是 string");
            }
            return token.Value<string>();
        }
    }
}
