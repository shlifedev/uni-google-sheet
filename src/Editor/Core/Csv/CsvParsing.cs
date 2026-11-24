#nullable enable
using System;
using System.Collections.Generic;
using System.Text;

namespace HolySheet.Core.Csv
{
    public sealed class CsvFormatException : Exception
    {
        public CsvFormatException(string message, int line) : base(message)
        {
            Line = line;
        }

        public int Line { get; }
    }

    public interface ICsvParser
    {
        string Name { get; }

        List<string[]> Parse(string text);
    }

    public sealed class BuiltinCsvParser : ICsvParser
    {
        public static BuiltinCsvParser Instance { get; } = new();

        public string Name => "builtin";

        public List<string[]> Parse(string text)
        {
            Guard.NotNull(text, nameof(text));
            var rows = new List<string[]>();
            var fields = new List<string>(16);
            ReadOnlySpan<char> span = text.AsSpan();
            int position = span.Length > 0 && span[0] == '﻿' ? 1 : 0;
            StringBuilder? buffer = null;

            while (position < span.Length)
            {
                fields.Clear();
                while (true)
                {
                    if (position < span.Length && span[position] == '"')
                    {
                        int start = position;
                        position++;
                        buffer ??= new StringBuilder();
                        buffer.Clear();
                        bool closed = false;
                        while (position < span.Length)
                        {
                            int quote = span[position..].IndexOf('"');
                            if (quote < 0)
                            {
                                break;
                            }

                            buffer.Append(span.Slice(position, quote));
                            position += quote + 1;
                            if (position < span.Length && span[position] == '"')
                            {
                                buffer.Append('"');
                                position++;
                                continue;
                            }

                            closed = true;
                            break;
                        }

                        if (!closed)
                        {
                            int line = CsvText.LineOf(span, start);
                            throw new CsvFormatException($"The quote opened on line {line} is never closed.", line);
                        }

                        int tail = span[position..].IndexOfAny(',', '\n', '\r');
                        if (tail < 0)
                        {
                            tail = span.Length - position;
                        }

                        if (tail > 0)
                        {
                            buffer.Append(span.Slice(position, tail));
                            position += tail;
                        }

                        fields.Add(CsvText.NormalizeLineBreaks(buffer.ToString()));
                    }
                    else
                    {
                        int length = span[position..].IndexOfAny(',', '\n', '\r');
                        if (length < 0)
                        {
                            length = span.Length - position;
                        }

                        fields.Add(length == 0 ? "" : span.Slice(position, length).ToString());
                        position += length;
                    }

                    if (position >= span.Length)
                    {
                        break;
                    }

                    char separator = span[position];
                    position++;
                    if (separator == ',')
                    {
                        if (position >= span.Length)
                        {
                            fields.Add("");
                            break;
                        }

                        continue;
                    }

                    if (separator == '\r' && position < span.Length && span[position] == '\n')
                    {
                        position++;
                    }

                    break;
                }

                rows.Add(fields.ToArray());
            }

            return rows;
        }
    }

    internal static class CsvText
    {
        public static string NormalizeLineBreaks(string value)
        {
            return value.Contains('\r') ? value.Replace("\r\n", "\n").Replace('\r', '\n') : value;
        }

        public static int LineOf(ReadOnlySpan<char> text, int position)
        {
            int line = 1;
            for (int index = 0; index < position && index < text.Length; index++)
            {
                if (text[index] == '\n' || (text[index] == '\r' && !(index + 1 < text.Length && text[index + 1] == '\n')))
                {
                    line++;
                }
            }

            return line;
        }
    }

    public static class CsvWriter
    {
        public static string Write(IEnumerable<IReadOnlyList<string>> rows)
        {
            var builder = new StringBuilder();
            foreach (IReadOnlyList<string> row in rows)
            {
                for (int index = 0; index < row.Count; index++)
                {
                    if (index > 0)
                    {
                        builder.Append(',');
                    }

                    AppendField(builder, row[index] ?? "");
                }

                builder.Append('\n');
            }

            return builder.ToString();
        }

        private static void AppendField(StringBuilder builder, string value)
        {
            bool quote = value.Length > 0 && (value.AsSpan().IndexOfAny(",\"\n\r".AsSpan()) >= 0 || char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]));
            if (!quote)
            {
                builder.Append(value);
                return;
            }

            builder.Append('"');
            foreach (char c in value)
            {
                if (c == '"')
                {
                    builder.Append('"');
                }

                builder.Append(c);
            }

            builder.Append('"');
        }
    }

    public static class CsvBackends
    {
        public const string Auto = "auto";
        public const string Builtin = "builtin";
        public const string Sep = "sep";

        private const string SepTypeName = "HolySheet.Csv.SepCsvParser, HolySheet.Csv.Sep";

        private static readonly object Gate = new();
        private static ICsvParser? _fast;
        private static bool _probed;
        private static string _unavailableReason = "";

        public static void RegisterFast(ICsvParser parser)
        {
            Guard.NotNull(parser, nameof(parser));
            lock (Gate)
            {
                _fast = parser;
                _probed = true;
            }
        }

        public static ICsvParser? Fast
        {
            get
            {
                lock (Gate)
                {
                    if (!_probed)
                    {
                        _probed = true;
                        try
                        {
                            var type = Type.GetType(SepTypeName, throwOnError: false);
                            if (type == null)
                            {
                                _unavailableReason = "the HolySheet.Csv.Sep assembly is not present on this runtime";
                            }
                            else
                            {
                                _fast = (ICsvParser)Activator.CreateInstance(type)!;
                                _fast.Parse("a,b\n1,2\n");
                            }
                        }
                        catch (Exception exception)
                        {
                            _fast = null;
                            _unavailableReason = exception.GetBaseException().Message;
                        }
                    }

                    return _fast;
                }
            }
        }

        public static string FastUnavailableReason
        {
            get
            {
                _ = Fast;
                return _unavailableReason;
            }
        }

        public static ICsvParser Resolve(string? name)
        {
            switch ((name ?? Auto).Trim().ToLowerInvariant())
            {
                case "":
                case Auto:
                case Builtin:
                    return BuiltinCsvParser.Instance;
                case Sep:
                    return Fast ?? throw new NotSupportedException(
                        $"The 'sep' CSV backend is not available: {FastUnavailableReason}. It needs .NET 8 or newer (the .NET CLI, or the Unity 7 editor). Use \"auto\" or \"builtin\" for the built-in parser.");
                default:
                    throw new ArgumentException($"Unknown CSV backend '{name}'. Use \"auto\", \"builtin\" or \"sep\".");
            }
        }
    }
}
