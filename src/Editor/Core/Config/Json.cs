#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace HolySheet.Core.Config
{
    public enum JsonKind
    {
        Null,
        Boolean,
        Number,
        String,
        Array,
        Object
    }

    /// <summary>The JSON text could not be read. The message names the line and column.</summary>
    public sealed class JsonFormatException : Exception
    {
        public JsonFormatException(string message) : base(message)
        {
        }
    }

    /// <summary>
    /// A small JSON document model. It exists so the engine needs no JSON package: the same DLL must load in the Unity 6 editor,
    /// the Unity 7 editor and the .NET CLI without bringing a dependency that could clash with one already in a project.
    /// The reader accepts <c>//</c> comments and trailing commas, because the config file is written by hand.
    /// </summary>
    public sealed class JsonValue
    {
        private readonly object? _value;

        public JsonKind Kind { get; }

        private JsonValue(JsonKind kind, object? value)
        {
            Kind = kind;
            _value = value;
        }

        public static JsonValue Null { get; } = new(JsonKind.Null, null);

        public static JsonValue From(string? value) => value == null ? Null : new JsonValue(JsonKind.String, value);

        public static JsonValue From(bool value) => new(JsonKind.Boolean, value);

        public static JsonValue From(double value) => new(JsonKind.Number, value);

        public static JsonValue From(long value) => new(JsonKind.Number, (double)value);

        public static JsonValue Array(IEnumerable<JsonValue> items) => new(JsonKind.Array, items.ToList());

        public static JsonValue Array(IEnumerable<string> items) => Array(items.Select(From));

        public static JsonObject Object() => new();

        internal static JsonValue FromObject(JsonObject value) => new(JsonKind.Object, value);

        public string? AsString => Kind == JsonKind.String ? (string)_value! : null;

        public bool? AsBoolean => Kind == JsonKind.Boolean ? (bool)_value! : null;

        public double? AsNumber => Kind == JsonKind.Number ? (double)_value! : null;

        public IReadOnlyList<JsonValue>? AsArray => Kind == JsonKind.Array ? (List<JsonValue>)_value! : null;

        public JsonObject? AsObject => Kind == JsonKind.Object ? (JsonObject)_value! : null;

        public static JsonValue Parse(string text) => new JsonReader(text).ReadDocument();

        public string ToJson(bool indented = true)
        {
            var builder = new StringBuilder();
            Write(builder, indented, 0);
            if (indented)
            {
                builder.Append('\n');
            }

            return builder.ToString();
        }

        internal void Write(StringBuilder builder, bool indented, int depth)
        {
            switch (Kind)
            {
                case JsonKind.Null:
                    builder.Append("null");
                    break;
                case JsonKind.Boolean:
                    builder.Append((bool)_value! ? "true" : "false");
                    break;
                case JsonKind.Number:
                    double number = (double)_value!;
                    builder.Append(Math.Floor(number) == number && Math.Abs(number) < 9007199254740992.0
                        ? ((long)number).ToString(CultureInfo.InvariantCulture)
                        : number.ToString("R", CultureInfo.InvariantCulture));
                    break;
                case JsonKind.String:
                    WriteString(builder, (string)_value!);
                    break;
                case JsonKind.Array:
                    var items = (List<JsonValue>)_value!;
                    // Arrays of plain values stay on one line: that is how people write them and it keeps diffs short.
                    bool inline = !indented || items.All(item => item.Kind is not (JsonKind.Array or JsonKind.Object));
                    builder.Append('[');
                    for (int index = 0; index < items.Count; index++)
                    {
                        builder.Append(index > 0 ? (inline ? (indented ? ", " : ",") : ",") : "");
                        if (!inline)
                        {
                            NewLine(builder, depth + 1);
                        }

                        items[index].Write(builder, indented, depth + 1);
                    }

                    if (!inline && items.Count > 0)
                    {
                        NewLine(builder, depth);
                    }

                    builder.Append(']');
                    break;
                default:
                    ((JsonObject)_value!).Write(builder, indented, depth);
                    break;
            }
        }

        internal static void NewLine(StringBuilder builder, int depth)
        {
            builder.Append('\n').Append(' ', depth * 2);
        }

        internal static void WriteString(StringBuilder builder, string value)
        {
            builder.Append('"');
            foreach (char c in value)
            {
                switch (c)
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
                    default:
                        if (c < ' ')
                        {
                            builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            builder.Append(c);
                        }

                        break;
                }
            }

            builder.Append('"');
        }
    }

    /// <summary>A JSON object that remembers the order its members were added in, so written files are stable.</summary>
    public sealed class JsonObject
    {
        private readonly List<KeyValuePair<string, JsonValue>> _members = new();

        public IReadOnlyList<KeyValuePair<string, JsonValue>> Members => _members;

        public JsonValue? this[string name]
        {
            get
            {
                foreach (KeyValuePair<string, JsonValue> member in _members)
                {
                    if (member.Key == name)
                    {
                        return member.Value;
                    }
                }

                return null;
            }
        }

        public JsonObject Set(string name, JsonValue value)
        {
            for (int index = 0; index < _members.Count; index++)
            {
                if (_members[index].Key == name)
                {
                    _members[index] = new KeyValuePair<string, JsonValue>(name, value);
                    return this;
                }
            }

            _members.Add(new KeyValuePair<string, JsonValue>(name, value));
            return this;
        }

        public JsonObject Set(string name, string? value) => Set(name, JsonValue.From(value));

        public JsonObject Set(string name, bool value) => Set(name, JsonValue.From(value));

        public JsonObject Set(string name, long value) => Set(name, JsonValue.From(value));

        public JsonObject Set(string name, JsonObject value) => Set(name, JsonValue.FromObject(value));

        public JsonValue ToValue() => JsonValue.FromObject(this);

        public string? GetString(string name) => this[name]?.AsString;

        public bool? GetBoolean(string name) => this[name]?.AsBoolean;

        public double? GetNumber(string name) => this[name]?.AsNumber;

        public JsonObject? GetObject(string name) => this[name]?.AsObject;

        public IReadOnlyList<JsonValue>? GetArray(string name) => this[name]?.AsArray;

        internal void Write(StringBuilder builder, bool indented, int depth)
        {
            builder.Append('{');
            for (int index = 0; index < _members.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                if (indented)
                {
                    JsonValue.NewLine(builder, depth + 1);
                }

                JsonValue.WriteString(builder, _members[index].Key);
                builder.Append(indented ? ": " : ":");
                _members[index].Value.Write(builder, indented, depth + 1);
            }

            if (indented && _members.Count > 0)
            {
                JsonValue.NewLine(builder, depth);
            }

            builder.Append('}');
        }
    }

    internal ref struct JsonReader
    {
        private readonly ReadOnlySpan<char> _text;
        private int _position;

        public JsonReader(string text)
        {
            _text = text.AsSpan();
            _position = _text.Length > 0 && _text[0] == '﻿' ? 1 : 0;
        }

        public JsonValue ReadDocument()
        {
            JsonValue value = ReadValue();
            SkipTrivia();
            if (_position < _text.Length)
            {
                throw Error("unexpected text after the end of the document");
            }

            return value;
        }

        private JsonValue ReadValue()
        {
            SkipTrivia();
            if (_position >= _text.Length)
            {
                throw Error("unexpected end of text");
            }

            char c = _text[_position];
            switch (c)
            {
                case '{':
                    return ReadObject();
                case '[':
                    return ReadArray();
                case '"':
                    return JsonValue.From(ReadString());
                case 't':
                    Expect("true");
                    return JsonValue.From(true);
                case 'f':
                    Expect("false");
                    return JsonValue.From(false);
                case 'n':
                    Expect("null");
                    return JsonValue.Null;
                default:
                    if (c == '-' || (c >= '0' && c <= '9'))
                    {
                        return ReadNumber();
                    }

                    throw Error($"unexpected character '{c}'");
            }
        }

        private JsonValue ReadObject()
        {
            _position++;
            JsonObject result = JsonValue.Object();
            while (true)
            {
                SkipTrivia();
                if (_position >= _text.Length)
                {
                    throw Error("an object is never closed");
                }

                if (_text[_position] == '}')
                {
                    _position++;
                    return result.ToValue();
                }

                if (_text[_position] != '"')
                {
                    throw Error("expected a member name in double quotes");
                }

                string name = ReadString();
                SkipTrivia();
                if (_position >= _text.Length || _text[_position] != ':')
                {
                    throw Error($"expected ':' after \"{name}\"");
                }

                _position++;
                result.Set(name, ReadValue());
                SkipTrivia();
                if (_position < _text.Length && _text[_position] == ',')
                {
                    _position++;
                }
                else if (_position >= _text.Length || _text[_position] != '}')
                {
                    throw Error("expected ',' or '}'");
                }
            }
        }

        private JsonValue ReadArray()
        {
            _position++;
            var items = new List<JsonValue>();
            while (true)
            {
                SkipTrivia();
                if (_position >= _text.Length)
                {
                    throw Error("an array is never closed");
                }

                if (_text[_position] == ']')
                {
                    _position++;
                    return JsonValue.Array(items);
                }

                items.Add(ReadValue());
                SkipTrivia();
                if (_position < _text.Length && _text[_position] == ',')
                {
                    _position++;
                }
                else if (_position >= _text.Length || _text[_position] != ']')
                {
                    throw Error("expected ',' or ']'");
                }
            }
        }

        private string ReadString()
        {
            _position++;
            var builder = new StringBuilder();
            while (_position < _text.Length)
            {
                char c = _text[_position++];
                if (c == '"')
                {
                    return builder.ToString();
                }

                if (c != '\\')
                {
                    builder.Append(c);
                    continue;
                }

                if (_position >= _text.Length)
                {
                    break;
                }

                char escape = _text[_position++];
                switch (escape)
                {
                    case 'n':
                        builder.Append('\n');
                        break;
                    case 'r':
                        builder.Append('\r');
                        break;
                    case 't':
                        builder.Append('\t');
                        break;
                    case 'b':
                        builder.Append('\b');
                        break;
                    case 'f':
                        builder.Append('\f');
                        break;
                    case 'u':
                        if (_position + 4 > _text.Length || !int.TryParse(_text.Slice(_position, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code))
                        {
                            throw Error("a \\u escape needs four hex digits");
                        }

                        builder.Append((char)code);
                        _position += 4;
                        break;
                    default:
                        builder.Append(escape);
                        break;
                }
            }

            throw Error("a string is never closed");
        }

        private JsonValue ReadNumber()
        {
            int start = _position;
            while (_position < _text.Length && (char.IsDigit(_text[_position]) || _text[_position] is '-' or '+' or '.' or 'e' or 'E'))
            {
                _position++;
            }

            if (!double.TryParse(_text[start.._position], NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
            {
                throw Error($"'{_text[start.._position].ToString()}' is not a number");
            }

            return JsonValue.From(number);
        }

        private void Expect(string word)
        {
            if (_position + word.Length > _text.Length || !_text.Slice(_position, word.Length).SequenceEqual(word.AsSpan()))
            {
                throw Error($"expected '{word}'");
            }

            _position += word.Length;
        }

        private void SkipTrivia()
        {
            while (_position < _text.Length)
            {
                char c = _text[_position];
                if (char.IsWhiteSpace(c))
                {
                    _position++;
                }
                else if (c == '/' && _position + 1 < _text.Length && _text[_position + 1] == '/')
                {
                    while (_position < _text.Length && _text[_position] != '\n')
                    {
                        _position++;
                    }
                }
                else if (c == '/' && _position + 1 < _text.Length && _text[_position + 1] == '*')
                {
                    int end = _text[(_position + 2)..].IndexOf("*/".AsSpan());
                    _position = end < 0 ? _text.Length : _position + 2 + end + 2;
                }
                else
                {
                    return;
                }
            }
        }

        private readonly JsonFormatException Error(string detail)
        {
            int line = 1;
            int column = 1;
            for (int index = 0; index < _position && index < _text.Length; index++)
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

            return new JsonFormatException($"line {line}, column {column}: {detail}");
        }
    }
}
