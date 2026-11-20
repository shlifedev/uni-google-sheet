#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace HolySheet
{
    /// <summary>A JSON number, kept as the text it was written with so it is written back unchanged.</summary>
    internal sealed class ManifestJsonNumber
    {
        public string Text { get; }

        public ManifestJsonNumber(string text)
        {
            Text = text;
        }
    }

    /// <summary>A JSON object with its members in the order they were written.</summary>
    internal sealed class ManifestJsonObject
    {
        public List<KeyValuePair<string, object?>> Members { get; } = new List<KeyValuePair<string, object?>>();
    }

    /// <summary>
    /// The small, strict JSON reader and writer the manifest needs. The runtime has no dependencies, so it cannot use a JSON
    /// package, and Unity's JsonUtility cannot keep members it does not know.
    /// Values are <see cref="ManifestJsonObject"/>, <see cref="List{T}"/> of values, <see cref="string"/>,
    /// <see cref="ManifestJsonNumber"/>, <see cref="bool"/> or null.
    /// </summary>
    internal static class ManifestJson
    {
        /// <summary>Deep enough for any real manifest; stops a hostile file from exhausting the stack.</summary>
        private const int MaxDepth = 64;

        /// <exception cref="FormatException">The text is not JSON. The message names the line and column.</exception>
        public static object? Parse(string text)
        {
            var reader = new Reader(text);
            if (reader.Peek() == '﻿')
            {
                reader.Position++;
            }

            reader.SkipWhitespace();
            object? value = reader.ReadValue(0);
            reader.SkipWhitespace();
            if (!reader.AtEnd)
            {
                throw reader.Error("there is more text after the end of the JSON value");
            }

            return value;
        }

        /// <summary>Writes a JSON string literal: quotes, backslashes and control characters escaped, everything else as is.</summary>
        public static void WriteString(StringBuilder builder, string value)
        {
            builder.Append('"');
            for (int index = 0; index < value.Length; index++)
            {
                char character = value[index];
                switch (character)
                {
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    case '\b':
                        builder.Append("\\b");
                        break;
                    case '\f':
                        builder.Append("\\f");
                        break;
                    default:
                        if (character < 0x20)
                        {
                            builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            builder.Append(character);
                        }

                        break;
                }
            }

            builder.Append('"');
        }

        public static string Quote(string value)
        {
            var builder = new StringBuilder(value.Length + 2);
            WriteString(builder, value);
            return builder.ToString();
        }

        /// <summary>Writes a value with no whitespace, members in their original order. Two equal documents give equal text.</summary>
        public static void WriteMinified(StringBuilder builder, object? value)
        {
            switch (value)
            {
                case null:
                    builder.Append("null");
                    break;
                case bool flag:
                    builder.Append(flag ? "true" : "false");
                    break;
                case string text:
                    WriteString(builder, text);
                    break;
                case ManifestJsonNumber number:
                    builder.Append(number.Text);
                    break;
                case List<object?> items:
                    builder.Append('[');
                    for (int index = 0; index < items.Count; index++)
                    {
                        if (index > 0)
                        {
                            builder.Append(',');
                        }

                        WriteMinified(builder, items[index]);
                    }

                    builder.Append(']');
                    break;
                case ManifestJsonObject obj:
                    builder.Append('{');
                    for (int index = 0; index < obj.Members.Count; index++)
                    {
                        if (index > 0)
                        {
                            builder.Append(',');
                        }

                        WriteString(builder, obj.Members[index].Key);
                        builder.Append(':');
                        WriteMinified(builder, obj.Members[index].Value);
                    }

                    builder.Append('}');
                    break;
                default:
                    throw new ArgumentException("Not a JSON value: " + value.GetType().Name, nameof(value));
            }
        }

        public static string Minify(object? value)
        {
            var builder = new StringBuilder();
            WriteMinified(builder, value);
            return builder.ToString();
        }

        /// <summary>The value a caller sees for a member this runtime does not know: the string itself, or the JSON of anything else.</summary>
        public static string Describe(string rawJson)
        {
            if (rawJson.Length > 0 && rawJson[0] == '"')
            {
                return (string)Parse(rawJson)!;
            }

            return rawJson;
        }

        private sealed class Reader
        {
            private readonly string _text;

            public int Position;

            public Reader(string text)
            {
                _text = text;
            }

            public bool AtEnd => Position >= _text.Length;

            public char Peek() => Position < _text.Length ? _text[Position] : '\0';

            public void SkipWhitespace()
            {
                while (Position < _text.Length)
                {
                    char character = _text[Position];
                    if (character != ' ' && character != '\t' && character != '\n' && character != '\r')
                    {
                        return;
                    }

                    Position++;
                }
            }

            public object? ReadValue(int depth)
            {
                if (depth > MaxDepth)
                {
                    throw Error("values are nested more than " + MaxDepth + " levels deep");
                }

                if (AtEnd)
                {
                    throw Error("the text ends where a value was expected");
                }

                char character = _text[Position];
                switch (character)
                {
                    case '{':
                        return ReadObject(depth);
                    case '[':
                        return ReadArray(depth);
                    case '"':
                        return ReadString();
                    case 't':
                        ReadWord("true");
                        return true;
                    case 'f':
                        ReadWord("false");
                        return false;
                    case 'n':
                        ReadWord("null");
                        return null;
                    default:
                        if (character == '-' || (character >= '0' && character <= '9'))
                        {
                            return ReadNumber();
                        }

                        throw Error("unexpected '" + character + "'");
                }
            }

            private ManifestJsonObject ReadObject(int depth)
            {
                var obj = new ManifestJsonObject();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                Position++;
                SkipWhitespace();
                if (Peek() == '}')
                {
                    Position++;
                    return obj;
                }

                while (true)
                {
                    SkipWhitespace();
                    if (Peek() != '"')
                    {
                        throw Error("expected a member name in quotes");
                    }

                    int nameStart = Position;
                    string name = ReadString();
                    if (!seen.Add(name))
                    {
                        Position = nameStart;
                        throw Error("the member \"" + name + "\" appears twice");
                    }

                    SkipWhitespace();
                    Expect(':');
                    SkipWhitespace();
                    obj.Members.Add(new KeyValuePair<string, object?>(name, ReadValue(depth + 1)));
                    SkipWhitespace();
                    if (Peek() == ',')
                    {
                        Position++;
                        continue;
                    }

                    Expect('}');
                    return obj;
                }
            }

            private List<object?> ReadArray(int depth)
            {
                var items = new List<object?>();
                Position++;
                SkipWhitespace();
                if (Peek() == ']')
                {
                    Position++;
                    return items;
                }

                while (true)
                {
                    SkipWhitespace();
                    items.Add(ReadValue(depth + 1));
                    SkipWhitespace();
                    if (Peek() == ',')
                    {
                        Position++;
                        continue;
                    }

                    Expect(']');
                    return items;
                }
            }

            private string ReadString()
            {
                Position++;
                var builder = new StringBuilder();
                while (true)
                {
                    if (AtEnd)
                    {
                        throw Error("a string is not closed");
                    }

                    char character = _text[Position++];
                    if (character == '"')
                    {
                        return builder.ToString();
                    }

                    if (character < 0x20)
                    {
                        Position--;
                        throw Error("a string contains a control character; write it as an escape such as \\n");
                    }

                    if (character != '\\')
                    {
                        builder.Append(character);
                        continue;
                    }

                    if (AtEnd)
                    {
                        throw Error("a string is not closed");
                    }

                    char escape = _text[Position++];
                    switch (escape)
                    {
                        case '"':
                            builder.Append('"');
                            break;
                        case '\\':
                            builder.Append('\\');
                            break;
                        case '/':
                            builder.Append('/');
                            break;
                        case 'b':
                            builder.Append('\b');
                            break;
                        case 'f':
                            builder.Append('\f');
                            break;
                        case 'n':
                            builder.Append('\n');
                            break;
                        case 'r':
                            builder.Append('\r');
                            break;
                        case 't':
                            builder.Append('\t');
                            break;
                        case 'u':
                            if (Position + 4 > _text.Length
                                || !int.TryParse(_text.Substring(Position, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int code))
                            {
                                Position -= 2;
                                throw Error("\\u must be followed by four hex digits");
                            }

                            builder.Append((char)code);
                            Position += 4;
                            break;
                        default:
                            Position -= 2;
                            throw Error("unknown escape '\\" + escape + "'");
                    }
                }
            }

            private ManifestJsonNumber ReadNumber()
            {
                int start = Position;
                if (Peek() == '-')
                {
                    Position++;
                }

                if (Peek() == '0')
                {
                    Position++;
                }
                else if (!ReadDigits())
                {
                    throw Error("a number has no digits");
                }

                if (Peek() == '.')
                {
                    Position++;
                    if (!ReadDigits())
                    {
                        throw Error("a number has no digits after the decimal point");
                    }
                }

                if (Peek() == 'e' || Peek() == 'E')
                {
                    Position++;
                    if (Peek() == '+' || Peek() == '-')
                    {
                        Position++;
                    }

                    if (!ReadDigits())
                    {
                        throw Error("a number has no digits in its exponent");
                    }
                }

                return new ManifestJsonNumber(_text.Substring(start, Position - start));
            }

            private bool ReadDigits()
            {
                int start = Position;
                while (Position < _text.Length && _text[Position] >= '0' && _text[Position] <= '9')
                {
                    Position++;
                }

                return Position > start;
            }

            private void ReadWord(string word)
            {
                if (string.CompareOrdinal(_text, Position, word, 0, word.Length) != 0)
                {
                    throw Error("unexpected '" + _text[Position] + "'");
                }

                Position += word.Length;
            }

            private void Expect(char character)
            {
                if (Peek() != character)
                {
                    throw AtEnd ? Error("the text ends where '" + character + "' was expected") : Error("expected '" + character + "' but found '" + Peek() + "'");
                }

                Position++;
            }

            public FormatException Error(string detail)
            {
                int line = 1;
                int column = 1;
                for (int index = 0; index < Position && index < _text.Length; index++)
                {
                    if (_text[index] == '\n')
                    {
                        line++;
                        column = 1;
                    }
                    else
                    {
                        column++;
                    }
                }

                return new FormatException("line " + line + ", column " + column + ": " + detail + ".");
            }
        }
    }
}
