#nullable enable
using System;
using System.Collections.Generic;
using System.Text;

namespace HolySheet.Core.Csv
{
    /// <summary>The CSV text is malformed in a way that cannot be read around, such as a quote that is never closed.</summary>
    public sealed class CsvFormatException : Exception
    {
        public CsvFormatException(string message, int line) : base(message)
        {
            Line = line;
        }

        /// <summary>One-based line where the problem starts.</summary>
        public int Line { get; }
    }

    /// <summary>
    /// Splits CSV text into rows of fields. Every implementation must give the same result for the same text,
    /// so that which one is installed never changes what gets built. The rules are RFC 4180 plus what real exports need:
    /// <list type="bullet">
    /// <item>A field that starts with a double quote runs to the matching quote; inside it, two quotes are one quote and commas and line breaks are data.</item>
    /// <item>Rows end with CRLF, LF or CR. A final row needs no line break, and a trailing line break adds no row.</item>
    /// <item>A leading byte order mark is ignored.</item>
    /// <item>An empty line is a row with one empty field. Rows may have different field counts.</item>
    /// <item>Line breaks inside a field are returned as LF, whatever the file used.</item>
    /// <item>Nothing is trimmed. Spaces are data.</item>
    /// </list>
    /// </summary>
    public interface ICsvParser
    {
        /// <summary>Short name shown in logs, such as "builtin" or "sep".</summary>
        string Name { get; }

        List<string[]> Parse(string text);
    }

    /// <summary>The parser that is always available. It works on every runtime Unity has shipped since 2021.</summary>
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

                        // Text between the closing quote and the next separator is kept rather than dropped.
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
                            // The text ends right after a comma: that comma still separates one last, empty field.
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

    /// <summary>
    /// Writes rows as CSV that any RFC 4180 reader takes back unchanged: a field is quoted when it holds a comma, a quote,
    /// a line break or leading/trailing white space; quotes inside are doubled; rows end with LF and there is no byte order mark.
    /// </summary>
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

    /// <summary>
    /// Picks the CSV parser. The built-in one is always there and is what "auto" uses. The Sep backend can be asked for by name
    /// on runtimes able to run it (.NET 8 or newer: the .NET CLI and the CoreCLR editor of Unity 7).
    /// <para>
    /// Why "auto" is not Sep: this pipeline turns every cell into a string, because the sheet rules work on text, and creating
    /// the strings dominates. Measured on 100,000 rows of 11 columns (7.9 MB) under .NET 10, Sep scans the text in about 2 ms,
    /// but producing the cells takes about 12 ms with either parser, and over many files in parallel the built-in parser was the
    /// faster of the two. Both give identical results, so the choice only affects speed.
    /// </para>
    /// </summary>
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

        /// <summary>Registers a faster parser. Called by the backend's own assembly, or by a host that references it.</summary>
        public static void RegisterFast(ICsvParser parser)
        {
            Guard.NotNull(parser, nameof(parser));
            lock (Gate)
            {
                _fast = parser;
                _probed = true;
            }
        }

        /// <summary>The Sep parser if this runtime can load it, otherwise null. The reason it is missing is in <see cref="FastUnavailableReason"/>.</summary>
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
                            // Looked up by name so this assembly does not need the backend to exist at all.
                            var type = Type.GetType(SepTypeName, throwOnError: false);
                            if (type == null)
                            {
                                _unavailableReason = "the HolySheet.Csv.Sep assembly is not present on this runtime";
                            }
                            else
                            {
                                _fast = (ICsvParser)Activator.CreateInstance(type)!;
                                // Parse once so a runtime that loads the assembly but cannot run it is found out here, not mid-build.
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

        /// <summary>Resolves "auto", "builtin" or "sep". Asking for "sep" where it cannot run is an error rather than a silent fallback.</summary>
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
