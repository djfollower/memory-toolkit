using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MemoryToolkit.Editor.Mcp
{
    /// <summary>
    /// The JSON shapes this server speaks are dynamic — a JSON-RPC envelope whose
    /// <c>params</c> differ per tool, and tool results that are trees, not records.
    /// <c>JsonUtility</c> only maps fixed <c>[Serializable]</c> classes, so it cannot
    /// express either side; this is the smallest thing that can.
    ///
    /// <para>Deliberately minimal: parse what a client sends, write what a tool
    /// returns. Not a general-purpose JSON library — no cycle detection, no big
    /// numbers, no reader over a stream.</para>
    /// </summary>
    internal sealed class JsonValue
    {
        internal enum JsonKind { Null, Bool, Number, String, Array, Object }

        // A private setter rather than `init`: `init` needs IsExternalInit, which
        // Unity's netstandard2.1 profile does not ship.
        internal JsonKind Kind { get; private set; }

        private bool _bool;
        private double _number;
        private string _string;
        private List<JsonValue> _array;
        private List<KeyValuePair<string, JsonValue>> _object;

        // Object members are kept in insertion order rather than a dictionary: a
        // reader (human or agent) sees fields in the order the tool wrote them.

        internal static JsonValue Null { get; } = new() { Kind = JsonKind.Null };

        internal static JsonValue Bool(bool value) => new() { Kind = JsonKind.Bool, _bool = value };

        internal static JsonValue Number(double value) => new() { Kind = JsonKind.Number, _number = value };

        internal static JsonValue String(string value)
            => value == null ? Null : new JsonValue { Kind = JsonKind.String, _string = value };

        internal static JsonValue Array() => new() { Kind = JsonKind.Array, _array = new List<JsonValue>() };

        internal static JsonValue Object()
            => new() { Kind = JsonKind.Object, _object = new List<KeyValuePair<string, JsonValue>>() };

        internal int Count => Kind switch
        {
            JsonKind.Array => _array.Count,
            JsonKind.Object => _object.Count,
            _ => 0,
        };

        /// <summary>Array element by index; <see cref="Null"/> when out of range.</summary>
        internal JsonValue this[int index]
            => Kind == JsonKind.Array && index >= 0 && index < _array.Count ? _array[index] : Null;

        /// <summary>
        /// Object member by name; <see cref="Null"/> when absent — so a chain of
        /// lookups on a missing branch reads as "not supplied" instead of throwing.
        /// </summary>
        internal JsonValue this[string name]
        {
            get
            {
                if (Kind != JsonKind.Object) return Null;
                for (int i = 0; i < _object.Count; i++)
                {
                    if (_object[i].Key == name) return _object[i].Value;
                }

                return Null;
            }
        }

        internal bool Has(string name) => Kind == JsonKind.Object && this[name] != Null;

        internal JsonValue Add(JsonValue value)
        {
            if (Kind != JsonKind.Array) throw new InvalidOperationException("Not an array.");
            _array.Add(value ?? Null);
            return this;
        }

        internal JsonValue Set(string name, JsonValue value)
        {
            if (Kind != JsonKind.Object) throw new InvalidOperationException("Not an object.");
            _object.Add(new KeyValuePair<string, JsonValue>(name, value ?? Null));
            return this;
        }

        /// <summary>Removes a member if present. Returns this, for chaining.</summary>
        internal JsonValue Remove(string name)
        {
            if (Kind != JsonKind.Object) return this;
            for (int i = _object.Count - 1; i >= 0; i--)
            {
                if (_object[i].Key == name) _object.RemoveAt(i);
            }

            return this;
        }

        internal JsonValue Set(string name, string value) => Set(name, String(value));
        internal JsonValue Set(string name, bool value) => Set(name, Bool(value));
        internal JsonValue Set(string name, double value) => Set(name, Number(value));
        internal JsonValue Set(string name, long value) => Set(name, Number(value));

        internal IEnumerable<KeyValuePair<string, JsonValue>> Members
            => Kind == JsonKind.Object ? _object : System.Linq.Enumerable.Empty<KeyValuePair<string, JsonValue>>();

        internal IEnumerable<JsonValue> Items
            => Kind == JsonKind.Array ? _array : System.Linq.Enumerable.Empty<JsonValue>();

        internal string AsString(string fallback = null)
            => Kind switch
            {
                JsonKind.String => _string,
                JsonKind.Number => _number.ToString(CultureInfo.InvariantCulture),
                JsonKind.Bool => _bool ? "true" : "false",
                _ => fallback,
            };

        internal bool AsBool(bool fallback = false)
            => Kind switch
            {
                JsonKind.Bool => _bool,
                JsonKind.Number => _number != 0,
                _ => fallback,
            };

        internal double AsDouble(double fallback = 0)
            => Kind == JsonKind.Number ? _number
                : Kind == JsonKind.String && double.TryParse(_string, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ? parsed
                : fallback;

        internal int AsInt(int fallback = 0)
        {
            double value = AsDouble(fallback);
            return double.IsNaN(value) || double.IsInfinity(value) ? fallback : (int)value;
        }

        public override string ToString()
        {
            var sb = new StringBuilder(256);
            Write(sb);
            return sb.ToString();
        }

        private void Write(StringBuilder sb)
        {
            switch (Kind)
            {
                case JsonKind.Null:
                    sb.Append("null");
                    break;
                case JsonKind.Bool:
                    sb.Append(_bool ? "true" : "false");
                    break;
                case JsonKind.Number:
                    // R round-trips; a NaN or infinity would emit an invalid literal,
                    // so those collapse to 0 rather than corrupting the whole message.
                    sb.Append(double.IsNaN(_number) || double.IsInfinity(_number)
                        ? "0"
                        : _number.ToString("R", CultureInfo.InvariantCulture));
                    break;
                case JsonKind.String:
                    WriteString(sb, _string);
                    break;
                case JsonKind.Array:
                    sb.Append('[');
                    for (int i = 0; i < _array.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        _array[i].Write(sb);
                    }

                    sb.Append(']');
                    break;
                case JsonKind.Object:
                    sb.Append('{');
                    for (int i = 0; i < _object.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        WriteString(sb, _object[i].Key);
                        sb.Append(':');
                        _object[i].Value.Write(sb);
                    }

                    sb.Append('}');
                    break;
            }
        }

        private static void WriteString(StringBuilder sb, string value)
        {
            sb.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        // Unity object names and asset paths can carry control
                        // characters; escape them rather than emitting raw bytes.
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }

            sb.Append('"');
        }

        /// <summary>Parses <paramref name="text"/>. Throws <see cref="FormatException"/> on malformed input.</summary>
        internal static JsonValue Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) throw new FormatException("Empty JSON document.");

            int index = 0;
            JsonValue value = ParseValue(text, ref index);
            SkipWhitespace(text, ref index);
            if (index != text.Length) throw new FormatException($"Trailing content at offset {index}.");
            return value;
        }

        internal static bool TryParse(string text, out JsonValue value)
        {
            try
            {
                value = Parse(text);
                return true;
            }
            catch (FormatException)
            {
                value = Null;
                return false;
            }
        }

        private static JsonValue ParseValue(string s, ref int i)
        {
            SkipWhitespace(s, ref i);
            if (i >= s.Length) throw new FormatException("Unexpected end of input.");

            switch (s[i])
            {
                case '{': return ParseObject(s, ref i);
                case '[': return ParseArray(s, ref i);
                case '"': return String(ParseString(s, ref i));
                case 't': Expect(s, ref i, "true"); return Bool(true);
                case 'f': Expect(s, ref i, "false"); return Bool(false);
                case 'n': Expect(s, ref i, "null"); return Null;
                default: return ParseNumber(s, ref i);
            }
        }

        private static JsonValue ParseObject(string s, ref int i)
        {
            JsonValue result = Object();
            i++; // '{'
            SkipWhitespace(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return result; }

            while (true)
            {
                SkipWhitespace(s, ref i);
                if (i >= s.Length || s[i] != '"') throw new FormatException($"Expected member name at offset {i}.");
                string name = ParseString(s, ref i);

                SkipWhitespace(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException($"Expected ':' at offset {i}.");
                i++;

                result.Set(name, ParseValue(s, ref i));

                SkipWhitespace(s, ref i);
                if (i >= s.Length) throw new FormatException("Unterminated object.");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return result; }
                throw new FormatException($"Expected ',' or '}}' at offset {i}.");
            }
        }

        private static JsonValue ParseArray(string s, ref int i)
        {
            JsonValue result = Array();
            i++; // '['
            SkipWhitespace(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return result; }

            while (true)
            {
                result.Add(ParseValue(s, ref i));

                SkipWhitespace(s, ref i);
                if (i >= s.Length) throw new FormatException("Unterminated array.");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return result; }
                throw new FormatException($"Expected ',' or ']' at offset {i}.");
            }
        }

        private static string ParseString(string s, ref int i)
        {
            i++; // opening quote
            var sb = new StringBuilder();
            while (true)
            {
                if (i >= s.Length) throw new FormatException("Unterminated string.");
                char c = s[i++];
                if (c == '"') return sb.ToString();

                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }

                if (i >= s.Length) throw new FormatException("Unterminated escape.");
                char escape = s[i++];
                switch (escape)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw new FormatException("Truncated \\u escape.");
                        sb.Append((char)ushort.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: throw new FormatException($"Unknown escape '\\{escape}'.");
                }
            }
        }

        private static JsonValue ParseNumber(string s, ref int i)
        {
            int start = i;
            if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == 'e' || s[i] == 'E' || s[i] == '+' || s[i] == '-'))
                i++;

            string literal = s.Substring(start, i - start);
            if (!double.TryParse(literal, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                throw new FormatException($"Invalid number '{literal}' at offset {start}.");
            return Number(value);
        }

        private static void Expect(string s, ref int i, string literal)
        {
            if (i + literal.Length > s.Length || string.CompareOrdinal(s, i, literal, 0, literal.Length) != 0)
                throw new FormatException($"Expected '{literal}' at offset {i}.");
            i += literal.Length;
        }

        private static void SkipWhitespace(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }
    }
}
