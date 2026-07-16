namespace SmokyPluginV2.Discord
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text;

    /// <summary>
    /// Small dependency-free JSON codec for Discord Gateway and REST payloads.
    /// </summary>
    internal static class Json
    {
        public static string Serialize(object value)
        {
            StringBuilder builder = new StringBuilder(256);
            WriteValue(builder, value);
            return builder.ToString();
        }

        public static Dictionary<string, object> DeserializeObject(string value) => new Parser(value).ParseValue() as Dictionary<string, object>;

        public static Dictionary<string, object> Object(object value) => value as Dictionary<string, object>;

        public static object[] Array(object value) => value as object[];

        private static void WriteValue(StringBuilder builder, object value)
        {
            if (value is null)
            {
                builder.Append("null");
                return;
            }

            if (value is string text)
            {
                WriteString(builder, text);
                return;
            }

            if (value is bool boolean)
            {
                builder.Append(boolean ? "true" : "false");
                return;
            }

            if (value is IDictionary<string, object> dictionary)
            {
                builder.Append('{');
                bool first = true;
                foreach (KeyValuePair<string, object> pair in dictionary)
                {
                    if (!first)
                        builder.Append(',');

                    first = false;
                    WriteString(builder, pair.Key);
                    builder.Append(':');
                    WriteValue(builder, pair.Value);
                }

                builder.Append('}');
                return;
            }

            if (value is IEnumerable enumerable)
            {
                builder.Append('[');
                bool first = true;
                foreach (object item in enumerable)
                {
                    if (!first)
                        builder.Append(',');

                    first = false;
                    WriteValue(builder, item);
                }

                builder.Append(']');
                return;
            }

            if (value is Enum)
            {
                WriteString(builder, value.ToString());
                return;
            }

            if (value is IFormattable formattable)
            {
                builder.Append(formattable.ToString(null, CultureInfo.InvariantCulture));
                return;
            }

            WriteString(builder, value.ToString());
        }

        private static void WriteString(StringBuilder builder, string value)
        {
            builder.Append('"');
            foreach (char character in value)
            {
                switch (character)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\b': builder.Append("\\b"); break;
                    case '\f': builder.Append("\\f"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (character < 32)
                            builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            builder.Append(character);
                        break;
                }
            }

            builder.Append('"');
        }

        private sealed class Parser
        {
            private readonly string value;
            private int index;

            public Parser(string value) => this.value = value ?? string.Empty;

            public object ParseValue()
            {
                SkipWhitespace();
                if (index >= value.Length)
                    return null;

                switch (value[index])
                {
                    case '{': return ParseObject();
                    case '[': return ParseArray();
                    case '"': return ParseString();
                    case 't': return ParseLiteral("true", true);
                    case 'f': return ParseLiteral("false", false);
                    case 'n': return ParseLiteral("null", null);
                    default: return ParseNumber();
                }
            }

            private Dictionary<string, object> ParseObject()
            {
                Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.Ordinal);
                index++;
                SkipWhitespace();

                if (Consume('}'))
                    return result;

                while (index < value.Length)
                {
                    string key = ParseString();
                    SkipWhitespace();
                    Expect(':');
                    result[key] = ParseValue();
                    SkipWhitespace();

                    if (Consume('}'))
                        return result;

                    Expect(',');
                    SkipWhitespace();
                }

                throw new FormatException("Unterminated JSON object.");
            }

            private object[] ParseArray()
            {
                List<object> result = new List<object>();
                index++;
                SkipWhitespace();

                if (Consume(']'))
                    return result.ToArray();

                while (index < value.Length)
                {
                    result.Add(ParseValue());
                    SkipWhitespace();

                    if (Consume(']'))
                        return result.ToArray();

                    Expect(',');
                    SkipWhitespace();
                }

                throw new FormatException("Unterminated JSON array.");
            }

            private string ParseString()
            {
                SkipWhitespace();
                Expect('"');
                StringBuilder builder = new StringBuilder();

                while (index < value.Length)
                {
                    char character = value[index++];
                    if (character == '"')
                        return builder.ToString();

                    if (character != '\\')
                    {
                        builder.Append(character);
                        continue;
                    }

                    if (index >= value.Length)
                        throw new FormatException("Invalid JSON escape sequence.");

                    char escaped = value[index++];
                    switch (escaped)
                    {
                        case '"': builder.Append('"'); break;
                        case '\\': builder.Append('\\'); break;
                        case '/': builder.Append('/'); break;
                        case 'b': builder.Append('\b'); break;
                        case 'f': builder.Append('\f'); break;
                        case 'n': builder.Append('\n'); break;
                        case 'r': builder.Append('\r'); break;
                        case 't': builder.Append('\t'); break;
                        case 'u':
                            if (index + 4 > value.Length)
                                throw new FormatException("Invalid JSON unicode escape.");
                            builder.Append((char)int.Parse(value.Substring(index, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            index += 4;
                            break;
                        default: throw new FormatException($"Unsupported JSON escape: \\{escaped}");
                    }
                }

                throw new FormatException("Unterminated JSON string.");
            }

            private object ParseNumber()
            {
                int start = index;
                while (index < value.Length && "-+0123456789.eE".IndexOf(value[index]) >= 0)
                    index++;

                string number = value.Substring(start, index - start);
                if (number.IndexOfAny(new[] { '.', 'e', 'E' }) >= 0 && double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double floating))
                    return floating;

                if (long.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out long integer))
                    return integer;

                throw new FormatException($"Invalid JSON number: {number}");
            }

            private object ParseLiteral(string literal, object result)
            {
                if (index + literal.Length > value.Length || string.Compare(value, index, literal, 0, literal.Length, StringComparison.Ordinal) != 0)
                    throw new FormatException($"Invalid JSON literal at position {index}.");

                index += literal.Length;
                return result;
            }

            private void SkipWhitespace()
            {
                while (index < value.Length && char.IsWhiteSpace(value[index]))
                    index++;
            }

            private bool Consume(char character)
            {
                if (index < value.Length && value[index] == character)
                {
                    index++;
                    return true;
                }

                return false;
            }

            private void Expect(char character)
            {
                if (!Consume(character))
                    throw new FormatException($"Expected '{character}' at JSON position {index}.");
            }
        }
    }
}
