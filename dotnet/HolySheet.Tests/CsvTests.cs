using System.Text;
using HolySheet.Core.Csv;
using HolySheet.Csv;

namespace HolySheet.Tests;

public class CsvTests
{
    public static IEnumerable<object[]> Parsers() =>
    [
        [BuiltinCsvParser.Instance],
        [new SepCsvParser()]
    ];

    public static IEnumerable<object[]> Cases()
    {
        (string Name, string Text, string Expected)[] cases =
        [
            ("plain", "a,b,c\n1,2,3\n", "a¦b¦c⏎1¦2¦3"),
            ("no trailing line break", "a,b\n1,2", "a¦b⏎1¦2"),
            ("crlf line breaks", "a,b\r\n1,2\r\n", "a¦b⏎1¦2"),
            ("cr line breaks", "a,b\r1,2\r", "a¦b⏎1¦2"),
            ("byte order mark", "﻿a,b\n1,2\n", "a¦b⏎1¦2"),
            ("empty fields", ",,\na,,c\n", "¦¦⏎a¦¦c"),
            ("trailing comma", "a,b,\n", "a¦b¦"),
            ("trailing comma at end of text", "a,b,", "a¦b¦"),
            ("ragged rows", "a\nb,c\nd,e,f\n", "a⏎b¦c⏎d¦e¦f"),
            ("quoted comma", "\"a,b\",c\n", "a,b¦c"),
            ("doubled quote", "\"say \"\"hi\"\"\",x\n", "say \"hi\"¦x"),
            ("quoted line break", "\"line1\nline2\",x\n", "line1\nline2¦x"),
            ("quoted crlf becomes lf", "\"line1\r\nline2\",x\r\n", "line1\nline2¦x"),
            ("quoted empty", "\"\",x\n", "¦x"),
            ("spaces are data", " a , b \n", " a ¦ b "),
            ("unicode", "이름,값\n포션,10\n", "이름¦값⏎포션¦10"),
            ("quote inside unquoted field", "a\"b,c\n", "a\"b¦c"),
            ("only a quoted field", "\"x\"", "x"),
            ("quoted field then end", "a,\"b\"", "a¦b"),
            ("many commas in quotes", "\",,,\",\",\"\n", ",,,¦,"),
        ];
        return cases.Select(item => new object[] { item.Name, item.Text, item.Expected });
    }

    private static string Flatten(List<string[]> rows) => string.Join("⏎", rows.Select(row => string.Join("¦", row)));

    [Theory]
    [MemberData(nameof(Cases))]
    public void Builtin_parses_by_the_rules(string name, string text, string expected)
    {
        Assert.True(expected == Flatten(BuiltinCsvParser.Instance.Parse(text)), name);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Sep_gives_the_same_rows_as_builtin(string name, string text, string expected)
    {
        Assert.True(expected == Flatten(new SepCsvParser().Parse(text)), name);
    }

    [Theory]
    [MemberData(nameof(Parsers))]
    public void Empty_text_has_no_rows(ICsvParser parser)
    {
        Assert.Empty(parser.Parse(""));
    }

    [Theory]
    [MemberData(nameof(Parsers))]
    public void Blank_lines_keep_their_place_so_row_numbers_match_the_file(ICsvParser parser)
    {
        List<string[]> rows = parser.Parse("a,b\n\n1,2\n");
        Assert.Equal(3, rows.Count);
        Assert.Equal(["1", "2"], rows[2]);
        Assert.True(rows[1].All(field => field.Length == 0));
    }

    [Fact]
    public void An_unclosed_quote_is_reported_with_its_line()
    {
        var exception = Assert.Throws<CsvFormatException>(() => BuiltinCsvParser.Instance.Parse("a,b\n1,\"oops\n2,3\n"));
        Assert.Equal(2, exception.Line);
    }

    [Fact]
    public void Both_parsers_agree_on_generated_text()
    {
        string[] pieces = ["a", "", " ", "1.5", "x,y", "he said \"hi\"", "two\nlines", "tab\there", "한글", "\"", ",", "a b", "-1", "end "];
        var random = new Random(20261118);
        var builtin = BuiltinCsvParser.Instance;
        var sep = new SepCsvParser();
        for (int document = 0; document < 300; document++)
        {
            var rows = new List<string[]>();
            int rowCount = random.Next(1, 12);
            int columnCount = random.Next(1, 7);
            for (int row = 0; row < rowCount; row++)
            {
                var fields = new string[columnCount];
                for (int column = 0; column < columnCount; column++)
                {
                    fields[column] = pieces[random.Next(pieces.Length)];
                }

                rows.Add(fields);
            }

            string text = CsvWriter.Write(rows);
            string expected = Flatten(rows);
            Assert.Equal(expected, Flatten(builtin.Parse(text)));
            Assert.Equal(expected, Flatten(sep.Parse(text)));
        }
    }

    [Fact]
    public void Writer_quotes_only_what_needs_it()
    {
        string text = CsvWriter.Write([["plain", "a,b", "say \"hi\"", "two\nlines", " padded ", ""]]);
        Assert.Equal("plain,\"a,b\",\"say \"\"hi\"\"\",\"two\nlines\",\" padded \",\n", text);
    }

    [Fact]
    public void Auto_is_the_builtin_parser_and_sep_is_available_by_name_on_this_runtime()
    {
        Assert.Equal("builtin", CsvBackends.Resolve("auto").Name);
        Assert.Equal("builtin", CsvBackends.Resolve(null).Name);
        Assert.Equal("builtin", CsvBackends.Resolve("builtin").Name);
        Assert.Equal("sep", CsvBackends.Resolve("sep").Name);
        Assert.NotNull(CsvBackends.Fast);
        Assert.Throws<ArgumentException>(() => CsvBackends.Resolve("fastest"));
    }

    [Fact]
    public void Both_parsers_agree_on_a_large_file()
    {
        var builder = new StringBuilder("Id,Name,Price,Tags\n");
        for (int index = 0; index < 100_000; index++)
        {
            builder.Append(index).Append(",\"Item, \"\"").Append(index).Append("\"\"\",").Append(index).Append(".5,a|b|c\n");
        }

        string text = builder.ToString();
        List<string[]> expected = BuiltinCsvParser.Instance.Parse(text);
        List<string[]> actual = new SepCsvParser().Parse(text);
        Assert.Equal(expected.Count, actual.Count);
        Assert.Equal(expected[^1], actual[^1]);
    }
}

public class CsvEdgeTests
{
    [Theory]
    [InlineData("a\"b,c\n")]
    [InlineData("\"a\"tail,c\n")]
    [InlineData("x,\"a\" ,c\n")]
    public void Text_outside_the_standard_parses_the_same_with_either_backend(string text)
    {
        List<string[]> expected = BuiltinCsvParser.Instance.Parse(text);
        List<string[]> actual = new SepCsvParser().Parse(text);
        Assert.Equal(expected.Select(row => string.Join("¦", row)), actual.Select(row => string.Join("¦", row)));
    }

    [Fact]
    public void An_unclosed_quote_is_an_error_with_either_backend()
    {
        Assert.Throws<CsvFormatException>(() => new SepCsvParser().Parse("a,b\n1,\"oops\n2,3\n"));
    }
}
