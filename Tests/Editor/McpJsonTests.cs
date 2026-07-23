using System;
using MemoryToolkit.Editor.Mcp;
using NUnit.Framework;

namespace MemoryToolkit.Tests
{
    public class McpJsonTests
    {
        [Test]
        public void Parse_ReadsNestedDocument()
        {
            JsonValue value = JsonValue.Parse(
                "{\"name\":\"validate_prefab\",\"arguments\":{\"assetPath\":\"Assets/A.prefab\",\"limit\":12,\"deep\":true},\"list\":[1,2,3]}");

            Assert.AreEqual("validate_prefab", value["name"].AsString());
            Assert.AreEqual("Assets/A.prefab", value["arguments"]["assetPath"].AsString());
            Assert.AreEqual(12, value["arguments"]["limit"].AsInt());
            Assert.IsTrue(value["arguments"]["deep"].AsBool());
            Assert.AreEqual(3, value["list"].Count);
            Assert.AreEqual(2, value["list"][1].AsInt());
        }

        [Test]
        public void MissingMembers_ChainWithoutThrowing()
        {
            JsonValue value = JsonValue.Parse("{}");

            // Arguments are optional far more often than not; a missing branch must
            // read as "not supplied" all the way down rather than throw mid-chain.
            Assert.AreEqual("fallback", value["params"]["arguments"]["scope"].AsString("fallback"));
            Assert.AreEqual(7, value["params"]["limit"].AsInt(7));
            Assert.IsFalse(value["params"]["deep"].AsBool());
            Assert.IsFalse(value.Has("params"));
        }

        [Test]
        public void Write_EscapesControlCharactersAndQuotes()
        {
            string json = JsonValue.Object().Set("name", "a\"b\\c\nd\te").ToString();

            Assert.AreEqual("{\"name\":\"a\\\"b\\\\c\\nd\\te\"}", json);
            Assert.AreEqual("a\"b\\c\nd\te", JsonValue.Parse(json)["name"].AsString());
        }

        [Test]
        public void Write_RoundTripsNumbersAndPreservesMemberOrder()
        {
            JsonValue original = JsonValue.Object()
                .Set("first", 1)
                .Set("second", 0.25)
                .Set("third", -12345678901L);

            string json = original.ToString();

            StringAssert.StartsWith("{\"first\":", json);
            JsonValue parsed = JsonValue.Parse(json);
            Assert.AreEqual(0.25, parsed["second"].AsDouble(), 1e-9);
            Assert.AreEqual(-12345678901L, (long)parsed["third"].AsDouble());
        }

        [Test]
        public void Write_NeverEmitsInvalidLiterals()
        {
            // NaN has no JSON literal; emitting one would corrupt the whole message
            // rather than the single field that produced it.
            Assert.AreEqual("{\"value\":0}", JsonValue.Object().Set("value", double.NaN).ToString());
        }

        [Test]
        public void TryParse_RejectsMalformedInput()
        {
            Assert.IsFalse(JsonValue.TryParse("{\"a\":}", out _));
            Assert.IsFalse(JsonValue.TryParse("{\"a\":1} trailing", out _));
            Assert.IsFalse(JsonValue.TryParse("", out _));
            Assert.Throws<FormatException>(() => JsonValue.Parse("[1,"));
        }
    }
}
