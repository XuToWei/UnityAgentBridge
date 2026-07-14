using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace AgentBridge.Tests.ProductEditMode
{
    public sealed class JsonParamsValidatorTests
    {
        [Test]
        public void MaximumPreservesIntegerPrecisionPastDoubleSafeRange()
        {
            const long maximum = 9007199254740992L;
            var schema = NumberPropertySchema(new JObject
            {
                ["type"] = "integer",
                ["maximum"] = maximum
            });
            var value = new JObject { ["value"] = maximum + 1 };

            Assert.That(JsonParamsValidator.TryValidate(value, schema, out var error), Is.False);
            Assert.That(error, Does.Contain("must be <=").And.Contain(maximum.ToString()));
        }

        [Test]
        public void NumericExclusiveBoundsUseDraftSixSemantics()
        {
            var schema = NumberPropertySchema(new JObject
            {
                ["type"] = "integer",
                ["exclusiveMinimum"] = 5,
                ["exclusiveMaximum"] = 10
            });

            Assert.That(JsonParamsValidator.TryValidate(
                new JObject { ["value"] = 5 }, schema, out _), Is.False);
            Assert.That(JsonParamsValidator.TryValidate(
                new JObject { ["value"] = 6 }, schema, out _), Is.True);
            Assert.That(JsonParamsValidator.TryValidate(
                new JObject { ["value"] = 10 }, schema, out _), Is.False);
        }

        [Test]
        public void DraftFourBooleanExclusiveBoundIsRejectedInsteadOfIgnored()
        {
            var schema = NumberPropertySchema(new JObject
            {
                ["type"] = "number",
                ["minimum"] = 0,
                ["exclusiveMinimum"] = true
            });

            Assert.That(JsonParamsValidator.TryValidateSchema(schema, out var error), Is.False);
            Assert.That(error, Does.Contain("exclusiveMinimum").And.Contain("finite number"));
        }

        [Test]
        public void CollectionLimitBeyondRuntimeCountRangeIsRejectedAtRegistration()
        {
            var schema = new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject
                {
                    ["items"] = new JObject
                    {
                        ["type"] = "array",
                        ["maxItems"] = (long)int.MaxValue + 1
                    }
                }
            };

            Assert.That(JsonParamsValidator.TryValidateSchema(schema, out var error), Is.False);
            Assert.That(error, Does.Contain("maxItems").And.Contain("2147483647"));
        }

        [Test]
        public void UnknownAssertionIsRejectedInsteadOfSilentlyIgnored()
        {
            var schema = new JObject
            {
                ["type"] = "array",
                ["uniqueItems"] = true
            };

            Assert.That(JsonParamsValidator.TryValidateSchema(schema, out var error), Is.False);
            Assert.That(error, Does.Contain("uniqueItems").And.Contain("unsupported"));
        }

        private static JObject NumberPropertySchema(JObject propertySchema)
        {
            return new JObject
            {
                ["type"] = "object",
                ["properties"] = new JObject { ["value"] = propertySchema },
                ["required"] = new JArray("value"),
                ["additionalProperties"] = false
            };
        }
    }
}
