using nietras.SeparatedValues;
using HolySheet.Core.Csv;

namespace HolySheet.Csv;

/// <summary>
/// CSV parsing with Sep. It must return exactly what <see cref="BuiltinCsvParser"/> returns for the same text;
/// the conformance tests run both over the same inputs.
/// <para>
/// Sep finds separators with SIMD at several gigabytes per second. This pipeline needs every cell as a string, because the
/// sheet rules work on text, and creating those strings costs about five times what the scanning does. Measured end to end
/// the two parsers are therefore close; Sep's whole-row conversion is used here because it is the fastest way to get the strings.
/// </para>
/// </summary>
public sealed class SepCsvParser : ICsvParser
{
    private static readonly SepReaderOptions Options = new(Sep.New(','))
    {
        HasHeader = false,
        Unescape = true,
        DisableColCountCheck = true,
        DisableFastFloat = true,
    };

    public string Name => "sep";

    public List<string[]> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > 0 && text[0] == '﻿')
        {
            text = text[1..];
        }

        // Sep and the built-in parser agree on well-formed CSV. They differ on text RFC 4180 does not define:
        // a quote in the middle of an unquoted field, text after a closing quote, a quote that is never closed.
        // Such text goes to the built-in parser, so the result (or the error) never depends on the backend.
        if (!Inspect(text, out bool quotedCarriageReturn))
        {
            return BuiltinCsvParser.Instance.Parse(text);
        }

        var rows = new List<string[]>();
        using SepReader reader = Options.FromText(text);
        foreach (SepReader.Row row in reader)
        {
            rows.Add(row[..].ToStringsArray());
        }

        if (quotedCarriageReturn)
        {
            // Line breaks inside cells are returned as LF whatever the file used.
            foreach (string[] fields in rows)
            {
                for (int index = 0; index < fields.Length; index++)
                {
                    if (fields[index].Contains('\r'))
                    {
                        fields[index] = fields[index].Replace("\r\n", "\n").Replace('\r', '\n');
                    }
                }
            }
        }

        return rows;
    }

    // Returns true when every quote opens a field at its very start, is closed, and is followed by a separator or the end.
    // Also reports whether any quoted field holds a carriage return. Text without quotes passes after one vectorized search.
    private static bool Inspect(string text, out bool quotedCarriageReturn)
    {
        quotedCarriageReturn = false;
        ReadOnlySpan<char> span = text;
        int position = 0;
        while (true)
        {
            int quote = span[position..].IndexOf('"');
            if (quote < 0)
            {
                return true;
            }

            position += quote;
            if (position > 0 && span[position - 1] is not (',' or '\n' or '\r'))
            {
                return false;
            }

            position++;
            int start = position;
            while (true)
            {
                int close = span[position..].IndexOf('"');
                if (close < 0)
                {
                    return false;
                }

                position += close + 1;
                if (position < span.Length && span[position] == '"')
                {
                    position++;
                    continue;
                }

                break;
            }

            quotedCarriageReturn |= span[start..position].Contains('\r');
            if (position < span.Length && span[position] is not (',' or '\n' or '\r'))
            {
                return false;
            }

            if (position >= span.Length)
            {
                return true;
            }
        }
    }
}
