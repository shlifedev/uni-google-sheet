using nietras.SeparatedValues;
using HolySheet.Core.Csv;

namespace HolySheet.Csv;

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
