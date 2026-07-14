using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace AgentBridge
{
    /// <summary>
    /// Validates command parameters against the small JSON Schema subset exposed by handlers.
    /// Keeping validation at the dispatcher boundary prevents Json.NET coercion from turning
    /// strings into numbers/booleans and gives every command the same INVALID_PARAMS contract.
    /// </summary>
    internal static class JsonParamsValidator
    {
        private static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(100);
        private static readonly HashSet<string> SupportedTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "object", "array", "string", "integer", "number", "boolean", "null"
        };
        private static readonly HashSet<string> SupportedKeywords =
            new HashSet<string>(StringComparer.Ordinal)
            {
                // Assertions implemented below.
                "type", "properties", "required", "additionalProperties",
                "items", "minItems", "maxItems", "minLength", "maxLength",
                "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum",
                "pattern", "enum", "const", "allOf", "anyOf", "oneOf", "not",
                // Metadata annotations that do not alter validation.
                "description", "default", "title", "examples", "$schema",
                "readOnly", "writeOnly", "deprecated"
            };

        internal static bool TryValidateSchema(JObject schema, out string error)
        {
            if (schema == null)
            {
                error = "schema must be an object";
                return false;
            }
            try
            {
                return ValidateSchemaNode(schema, "$schema", out error);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        internal static bool TryValidate(JObject value, JObject schema, out string error)
        {
            if (schema == null || !schema.HasValues)
            {
                error = null;
                return true;
            }

            try
            {
                return TryValidateToken(value, schema, "$", out error);
            }
            catch (Exception ex)
            {
                error = "invalid params schema: " + ex.Message;
                return false;
            }
        }

        private static bool TryValidateToken(JToken value, JObject schema, string path, out string error)
        {
            if (schema.TryGetValue("allOf", out var allOfToken) && allOfToken is JArray allOf)
            {
                foreach (var branch in allOf.OfType<JObject>())
                {
                    if (!TryValidateToken(value, branch, path, out error))
                    {
                        return false;
                    }
                }
            }

            if (schema.TryGetValue("anyOf", out var anyOfToken) && anyOfToken is JArray anyOf)
            {
                var matched = false;
                foreach (var branch in anyOf.OfType<JObject>())
                {
                    if (TryValidateToken(value, branch, path, out _))
                    {
                        matched = true;
                        break;
                    }
                }
                if (!matched)
                {
                    error = path + " does not match any allowed schema";
                    return false;
                }
            }

            if (schema.TryGetValue("oneOf", out var oneOfToken) && oneOfToken is JArray oneOf)
            {
                var matches = 0;
                foreach (var branch in oneOf.OfType<JObject>())
                {
                    if (TryValidateToken(value, branch, path, out _))
                    {
                        matches++;
                    }
                }
                if (matches != 1)
                {
                    error = path + " must match exactly one allowed schema";
                    return false;
                }
            }

            if (schema.TryGetValue("not", out var notToken) && notToken is JObject notSchema &&
                TryValidateToken(value, notSchema, path, out _))
            {
                error = path + " matches a forbidden schema";
                return false;
            }

            if (schema.TryGetValue("const", out var constToken) && !JToken.DeepEquals(value, constToken))
            {
                error = path + " must equal " + constToken.ToString(Newtonsoft.Json.Formatting.None);
                return false;
            }

            if (schema.TryGetValue("type", out var typeToken) && typeToken.Type == JTokenType.String)
            {
                var expectedType = typeToken.Value<string>();
                if (!MatchesType(value, expectedType))
                {
                    error = path + " must be " + expectedType + ", got " + TypeName(value);
                    return false;
                }
            }

            if (schema.TryGetValue("enum", out var enumToken) && enumToken is JArray allowed &&
                !allowed.Any(candidate => JToken.DeepEquals(candidate, value)))
            {
                error = path + " must be one of " + allowed.ToString(Newtonsoft.Json.Formatting.None);
                return false;
            }

            if (value.Type == JTokenType.Object && !ValidateObject((JObject)value, schema, path, out error))
            {
                return false;
            }
            if (value.Type == JTokenType.Array && !ValidateArray((JArray)value, schema, path, out error))
            {
                return false;
            }
            if (value.Type == JTokenType.String && !ValidateString(value.Value<string>(), schema, path, out error))
            {
                return false;
            }
            if ((value.Type == JTokenType.Integer || value.Type == JTokenType.Float) &&
                !ValidateNumber(value, schema, path, out error))
            {
                return false;
            }

            error = null;
            return true;
        }

        private static bool ValidateObject(JObject value, JObject schema, string path, out string error)
        {
            if (schema.TryGetValue("required", out var requiredToken) && requiredToken is JArray required)
            {
                foreach (var nameToken in required)
                {
                    var name = nameToken.Value<string>();
                    if (!string.IsNullOrEmpty(name) && value.Property(name, StringComparison.Ordinal) == null)
                    {
                        error = path + "." + name + " is required";
                        return false;
                    }
                }
            }

            var properties = schema["properties"] as JObject;
            foreach (var property in value.Properties())
            {
                if (properties?.Property(property.Name, StringComparison.Ordinal)?.Value is JObject propertySchema)
                {
                    if (!TryValidateToken(property.Value, propertySchema, path + "." + property.Name, out error))
                    {
                        return false;
                    }
                }
                else if (schema["additionalProperties"]?.Type == JTokenType.Boolean &&
                         !schema["additionalProperties"].Value<bool>())
                {
                    error = path + "." + property.Name + " is not allowed";
                    return false;
                }
                else if (schema["additionalProperties"] is JObject additionalSchema)
                {
                    if (!TryValidateToken(property.Value, additionalSchema,
                            path + "." + property.Name, out error))
                    {
                        return false;
                    }
                }
            }

            error = null;
            return true;
        }

        private static bool ValidateSchemaNode(JObject schema, string path, out string error)
        {
            foreach (var property in schema.Properties())
            {
                if (!SupportedKeywords.Contains(property.Name))
                {
                    error = path + "." + property.Name + " is unsupported";
                    return false;
                }
            }

            if (schema.TryGetValue("type", out var typeToken))
            {
                if (typeToken.Type != JTokenType.String || !SupportedTypes.Contains(typeToken.Value<string>()))
                {
                    error = path + ".type is unsupported";
                    return false;
                }
            }

            if (schema.TryGetValue("properties", out var propertiesToken))
            {
                if (!(propertiesToken is JObject properties))
                {
                    error = path + ".properties must be an object";
                    return false;
                }
                foreach (var property in properties.Properties())
                {
                    if (!(property.Value is JObject child))
                    {
                        error = path + ".properties." + property.Name + " must be an object";
                        return false;
                    }
                    if (!ValidateSchemaNode(child, path + ".properties." + property.Name, out error))
                    {
                        return false;
                    }
                }
            }

            if (schema.TryGetValue("required", out var requiredToken) &&
                (!(requiredToken is JArray required) || required.Any(item => item.Type != JTokenType.String)))
            {
                error = path + ".required must be an array of strings";
                return false;
            }
            if (schema.TryGetValue("enum", out var enumToken) && !(enumToken is JArray))
            {
                error = path + ".enum must be an array";
                return false;
            }

            foreach (var keyword in new[] { "allOf", "anyOf", "oneOf" })
            {
                if (!schema.TryGetValue(keyword, out var branchesToken))
                {
                    continue;
                }
                if (!(branchesToken is JArray branches) || branches.Count == 0 ||
                    branches.Any(branch => !(branch is JObject)))
                {
                    error = path + "." + keyword + " must be a non-empty array of schemas";
                    return false;
                }
                for (var i = 0; i < branches.Count; i++)
                {
                    if (!ValidateSchemaNode((JObject)branches[i], path + "." + keyword + "[" + i + "]", out error))
                    {
                        return false;
                    }
                }
            }

            foreach (var keyword in new[] { "not", "items" })
            {
                if (!schema.TryGetValue(keyword, out var childToken))
                {
                    continue;
                }
                if (!(childToken is JObject child))
                {
                    error = path + "." + keyword + " must be a schema object";
                    return false;
                }
                if (!ValidateSchemaNode(child, path + "." + keyword, out error))
                {
                    return false;
                }
            }

            if (schema.TryGetValue("additionalProperties", out var additional) &&
                additional.Type != JTokenType.Boolean)
            {
                if (!(additional is JObject additionalSchema))
                {
                    error = path + ".additionalProperties must be boolean or schema";
                    return false;
                }
                if (!ValidateSchemaNode(additionalSchema, path + ".additionalProperties", out error))
                {
                    return false;
                }
            }

            foreach (var keyword in new[] { "minItems", "maxItems", "minLength", "maxLength" })
            {
                if (!schema.TryGetValue(keyword, out var limit))
                {
                    continue;
                }
                if (limit.Type != JTokenType.Integer ||
                    !TryReadNonNegativeInt32(limit, out _))
                {
                    error = path + "." + keyword + " must be an integer in 0..2147483647";
                    return false;
                }
            }
            // Use the draft-06+ numeric form for exclusive bounds. Rejecting the draft-04
            // boolean form is safer than accepting a schema whose bound would be ignored.
            foreach (var keyword in new[]
                     {
                         "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum"
                     })
            {
                if (!schema.TryGetValue(keyword, out var number))
                {
                    continue;
                }
                if ((number.Type != JTokenType.Integer && number.Type != JTokenType.Float) ||
                    !IsFiniteNumber(number))
                {
                    error = path + "." + keyword + " must be a finite number";
                    return false;
                }
            }

            if (schema["pattern"] != null)
            {
                if (schema["pattern"].Type != JTokenType.String)
                {
                    error = path + ".pattern must be a string";
                    return false;
                }
                _ = new Regex(schema["pattern"].Value<string>(), RegexOptions.CultureInvariant, PatternTimeout);
            }

            error = null;
            return true;
        }

        private static bool ValidateArray(JArray value, JObject schema, string path, out string error)
        {
            if (schema["minItems"]?.Type == JTokenType.Integer && value.Count < schema["minItems"].Value<int>())
            {
                error = path + " has too few items";
                return false;
            }
            if (schema["maxItems"]?.Type == JTokenType.Integer && value.Count > schema["maxItems"].Value<int>())
            {
                error = path + " has too many items";
                return false;
            }

            if (schema["items"] is JObject itemSchema)
            {
                for (var i = 0; i < value.Count; i++)
                {
                    if (!TryValidateToken(value[i], itemSchema, path + "[" + i + "]", out error))
                    {
                        return false;
                    }
                }
            }

            error = null;
            return true;
        }

        private static bool ValidateString(string value, JObject schema, string path, out string error)
        {
            if (schema["minLength"]?.Type == JTokenType.Integer && value.Length < schema["minLength"].Value<int>())
            {
                error = path + " is shorter than minLength";
                return false;
            }
            if (schema["maxLength"]?.Type == JTokenType.Integer && value.Length > schema["maxLength"].Value<int>())
            {
                error = path + " is longer than maxLength";
                return false;
            }

            if (schema["pattern"]?.Type == JTokenType.String)
            {
                try
                {
                    if (!Regex.IsMatch(value, schema["pattern"].Value<string>(), RegexOptions.CultureInvariant, PatternTimeout))
                    {
                        error = path + " does not match its required pattern";
                        return false;
                    }
                }
                catch (ArgumentException ex)
                {
                    throw new InvalidOperationException("invalid schema pattern at " + path, ex);
                }
            }

            error = null;
            return true;
        }

        private static bool ValidateNumber(JToken value, JObject schema, string path, out string error)
        {
            if (!IsFiniteNumber(value))
            {
                error = path + " must be finite";
                return false;
            }
            if (schema["minimum"] != null && CompareNumbers(value, schema["minimum"]) < 0)
            {
                error = path + " must be >= " + FormatNumber(schema["minimum"]);
                return false;
            }
            if (schema["maximum"] != null && CompareNumbers(value, schema["maximum"]) > 0)
            {
                error = path + " must be <= " + FormatNumber(schema["maximum"]);
                return false;
            }
            if (schema["exclusiveMinimum"] != null &&
                CompareNumbers(value, schema["exclusiveMinimum"]) <= 0)
            {
                error = path + " must be > " + FormatNumber(schema["exclusiveMinimum"]);
                return false;
            }
            if (schema["exclusiveMaximum"] != null &&
                CompareNumbers(value, schema["exclusiveMaximum"]) >= 0)
            {
                error = path + " must be < " + FormatNumber(schema["exclusiveMaximum"]);
                return false;
            }

            error = null;
            return true;
        }

        private static bool TryReadNonNegativeInt32(JToken token, out int value)
        {
            value = 0;
            try
            {
                var number = token.Value<long>();
                if (number < 0 || number > int.MaxValue)
                {
                    return false;
                }
                value = (int)number;
                return true;
            }
            catch (Exception ex) when (ex is OverflowException || ex is FormatException ||
                                       ex is InvalidCastException)
            {
                return false;
            }
        }

        private static bool IsFiniteNumber(JToken token)
        {
            try
            {
                var number = token.Value<double>();
                return !double.IsNaN(number) && !double.IsInfinity(number);
            }
            catch (Exception ex) when (ex is OverflowException || ex is FormatException ||
                                       ex is InvalidCastException)
            {
                return false;
            }
        }

        private static int CompareNumbers(JToken left, JToken right)
        {
            if (decimal.TryParse(FormatNumber(left), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var leftDecimal) &&
                decimal.TryParse(FormatNumber(right), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var rightDecimal))
            {
                return leftDecimal.CompareTo(rightDecimal);
            }

            return left.Value<double>().CompareTo(right.Value<double>());
        }

        private static string FormatNumber(JToken token)
        {
            return token.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static bool MatchesType(JToken value, string expected)
        {
            switch (expected)
            {
                case "object": return value.Type == JTokenType.Object;
                case "array": return value.Type == JTokenType.Array;
                case "string": return value.Type == JTokenType.String;
                case "integer": return value.Type == JTokenType.Integer;
                case "number": return value.Type == JTokenType.Integer || value.Type == JTokenType.Float;
                case "boolean": return value.Type == JTokenType.Boolean;
                case "null": return value.Type == JTokenType.Null;
                default: throw new InvalidOperationException("unsupported schema type '" + expected + "'");
            }
        }

        private static string TypeName(JToken value)
        {
            return value == null ? "missing" : value.Type.ToString().ToLowerInvariant();
        }
    }
}
