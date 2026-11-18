using UGS.Core;
using UGS.Core.Building;
using UGS.Core.Schema;

namespace UGS.Tests;

public class TypeExpressionTests
{
    [Theory]
    [InlineData("int", TypeExpressionKind.Named, "int", ColumnRole.None, false, false)]
    [InlineData(" float[] ", TypeExpressionKind.Named, "float", ColumnRole.None, true, false)]
    [InlineData("List<string>", TypeExpressionKind.Named, "string", ColumnRole.None, true, false)]
    [InlineData("list<int>", TypeExpressionKind.Named, "int", ColumnRole.None, true, false)]
    [InlineData("Key<int>", TypeExpressionKind.Named, "int", ColumnRole.Key, false, false)]
    [InlineData("Key< string >", TypeExpressionKind.Named, "string", ColumnRole.Key, false, false)]
    [InlineData("Index<Enum<EKind>>", TypeExpressionKind.Enum, "EKind", ColumnRole.Index, false, false)]
    [InlineData("Enum<EKind>", TypeExpressionKind.Enum, "EKind", ColumnRole.None, false, false)]
    [InlineData("Enum<EKind>[]", TypeExpressionKind.Enum, "EKind", ColumnRole.None, true, false)]
    [InlineData("Enum<EKind[]>", TypeExpressionKind.Enum, "EKind", ColumnRole.None, true, false)]
    [InlineData("EKind[]", TypeExpressionKind.Named, "EKind", ColumnRole.None, true, false)]
    [InlineData("Ref<Item>", TypeExpressionKind.Reference, "Item", ColumnRole.None, false, false)]
    [InlineData("Ref<Item>?", TypeExpressionKind.Reference, "Item", ColumnRole.None, false, true)]
    [InlineData("Ref<Item>[]", TypeExpressionKind.Reference, "Item", ColumnRole.None, true, false)]
    [InlineData("Mapping<EMonsterId>", TypeExpressionKind.Mapping, "EMonsterId", ColumnRole.None, false, false)]
    public void Parses(string text, TypeExpressionKind kind, string name, ColumnRole role, bool isArray, bool isOptional)
    {
        Assert.True(TypeExpression.TryParse(text, out TypeExpression? expression, out string reason), reason);
        Assert.Equal(kind, expression!.Kind);
        Assert.Equal(name, expression.Name);
        Assert.Equal(role, expression.Role);
        Assert.Equal(isArray, expression.IsArray);
        Assert.Equal(isOptional, expression.IsOptional);
    }

    [Theory]
    [InlineData("")]
    [InlineData("int[][]")]
    [InlineData("Key<int[]>")]
    [InlineData("Key<Ref<Item>>")]
    [InlineData("Dictionary<int, string>")]
    [InlineData("int?")]
    [InlineData("Ref<Item>[]?")]
    [InlineData("Mapping<E>[]")]
    [InlineData("Enum<>")]
    public void Rejects(string text)
    {
        Assert.False(TypeExpression.TryParse(text, out _, out string reason));
        Assert.NotEmpty(reason);
    }
}

public class ScalarTests
{
    [Theory]
    [InlineData(ScalarKind.Int, "42", 42)]
    [InlineData(ScalarKind.Int, " -7 ", -7)]
    [InlineData(ScalarKind.Int, "", 0)]
    [InlineData(ScalarKind.Bool, "TRUE", true)]
    [InlineData(ScalarKind.Bool, "0", false)]
    [InlineData(ScalarKind.Bool, "", false)]
    [InlineData(ScalarKind.Double, "1e-3", 0.001)]
    [InlineData(ScalarKind.Float, "1.5", 1.5f)]
    [InlineData(ScalarKind.String, " kept as is ", " kept as is ")]
    public void Parses_text(ScalarKind kind, string text, object expected)
    {
        Assert.True(Scalars.TryParse(kind, text, out object value, out string reason), reason);
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData(ScalarKind.Int, "1.5", "is not a whole number")]
    [InlineData(ScalarKind.Int, "1,000", "is not a whole number")]
    [InlineData(ScalarKind.Int, "abc", "is not a whole number")]
    [InlineData(ScalarKind.Byte, "256", "does not fit in byte (0 to 255)")]
    [InlineData(ScalarKind.UInt, "-1", "does not fit in uint")]
    [InlineData(ScalarKind.Int, "2147483648", "does not fit in int")]
    [InlineData(ScalarKind.Bool, "yes", "is not a boolean")]
    [InlineData(ScalarKind.Float, "NaN", "is not a number")]
    [InlineData(ScalarKind.Double, "1,5", "is not a number")]
    [InlineData(ScalarKind.DateTime, "31/01/2026", "is not a date")]
    public void Explains_what_is_wrong(ScalarKind kind, string text, string expected)
    {
        Assert.False(Scalars.TryParse(kind, text, out _, out string reason));
        Assert.Contains(expected, reason);
    }

    [Theory]
    [InlineData("2026-01-31", 2026, 1, 31, 0, 0, 0)]
    [InlineData("2026-01-31 13:30:00", 2026, 1, 31, 13, 30, 0)]
    [InlineData("2026-01-31T13:30:05Z", 2026, 1, 31, 13, 30, 5)]
    [InlineData("2026/01/31", 2026, 1, 31, 0, 0, 0)]
    public void Dates_are_utc_whatever_the_machine_time_zone(string text, int year, int month, int day, int hour, int minute, int second)
    {
        Assert.True(Scalars.TryParse(ScalarKind.DateTime, text, out object value, out _));
        var date = (DateTime)value;
        Assert.Equal(new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc), date);
        Assert.Equal(DateTimeKind.Utc, date.Kind);
    }

    [Fact]
    public void Numbers_parse_the_same_under_any_culture()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            // German uses a comma as the decimal separator; the sheet text must not be read by the machine's rules.
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.True(Scalars.TryParse(ScalarKind.Double, "1.5", out object value, out _));
            Assert.Equal(1.5, value);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }
}

public class EnumSheetTests
{
    private const string EnumsSheet = """
        byte,,Flags<int>,,
        EKind,,EAccess,,ESimple
        kinds,,access rights,,
        None,,None,0,A
        Sword,,Read,1 << 0,B
        Bow,10,Write,1 << 1,
        Staff,,All,Read | Write,
        #Draft,,Admin,0x10,
        """;

    [Fact]
    public void Reads_enums_with_and_without_value_columns()
    {
        BuildResult result = Sheets.Build(Sheets.Of("Enums", EnumsSheet), Sheets.Of("T", "Key<int>,EKind\nId,Kind\n,\n1,Bow\n"));
        Assert.True(result.Success, Sheets.Errors(result));
        EnumDefinition kind = result.Enums.Single(definition => definition.Name == "EKind");
        Assert.Equal(ScalarKind.Byte, kind.Underlying);
        Assert.False(kind.IsFlags);
        Assert.Equal("kinds", kind.Comment);
        // A member without a value follows the previous one; the row starting with # is left out.
        Assert.Equal(["None=0", "Sword=1", "Bow=10", "Staff=11"], kind.Members.Select(member => member.Name + "=" + member.Value));

        EnumDefinition access = result.Enums.Single(definition => definition.Name == "EAccess");
        Assert.True(access.IsFlags);
        Assert.Equal(["None=0", "Read=1", "Write=2", "All=3", "Admin=16"], access.Members.Select(member => member.Name + "=" + member.Value));

        EnumDefinition simple = result.Enums.Single(definition => definition.Name == "ESimple");
        Assert.Equal(ScalarKind.Int, simple.Underlying);
        Assert.Equal(["A=0", "B=1"], simple.Members.Select(member => member.Name + "=" + member.Value));
    }

    [Theory]
    [InlineData("string\nEKind\n\nA\n", "is not an integer type for an enum")]
    [InlineData("byte\nEKind\n\nA\nA\n", "already has a member 'A'")]
    [InlineData("byte\nEKind\n\nNot Valid\n", "cannot be used as an enum member name")]
    [InlineData("byte,\nEKind,\n,\nA,300\n", "does not fit in byte")]
    [InlineData("int,\nEKind,\n,\nA,B | 1\n", "neither a number nor an earlier member")]
    [InlineData("int\nclass\n\nA\n", "cannot be used as an enum name")]
    public void Enum_sheet_mistakes_are_errors(string sheet, string expected)
    {
        BuildResult result = Sheets.Build(Sheets.Of("Enums", sheet), Sheets.Of("T", "Key<int>\nId\n\n1\n"));
        Assert.False(result.Success);
        Assert.Contains(expected, Sheets.Errors(result));
    }

    [Fact]
    public void Flags_cells_combine_members()
    {
        BuildResult result = Sheets.Build(Sheets.Of("Enums", EnumsSheet),
            Sheets.Of("T", "Key<int>,EAccess,EAccess[]\nId,Access,Each\n,,\n1,\"Read, Write\",Read|Write\n2,Read Write,\n3,,All\n"));
        Assert.True(result.Success, Sheets.Errors(result));
        Assert.Equal([3L, 3L, 0L], result.Tables.Single().Rows.Select(row => (long)row.Values[1]));
        Assert.Equal([1L, 2L], ((object[])result.Tables.Single().Rows[0].Values[2]).Cast<long>());
    }

    [Fact]
    public void An_empty_enum_cell_needs_a_zero_member()
    {
        BuildResult result = Sheets.Build(Sheets.Of("Enums", "int,\nEStart,\n,\nOne,1\n"), Sheets.Of("T", "Key<int>,EStart\nId,Start\n,\n1,\n"));
        Assert.Contains("has no member with the value 0", Sheets.Errors(result));
    }

    [Fact]
    public void Mapping_columns_make_an_enum_from_names_and_keys()
    {
        BuildResult result = Sheets.Build(Sheets.Of("Monster", "Key<short>,Mapping<EMonsterId>,string\nId,Code,Name\n,,\n10,Slime,S\n20,,nameless\n30,Goblin,G\n"));
        Assert.True(result.Success, Sheets.Errors(result));
        EnumDefinition mapping = Assert.Single(result.Enums);
        Assert.Equal(ScalarKind.Short, mapping.Underlying);
        Assert.Equal(["Slime=10", "Goblin=30"], mapping.Members.Select(member => member.Name + "=" + member.Value));
        // The Mapping column itself is not stored.
        Assert.Equal(["Id", "Name"], result.Tables.Single().Schema.Columns.Select(column => column.Field));
    }

    [Theory]
    [InlineData("Key<string>,Mapping<E>\nId,Code\n,\na,A\n", "needs the table to have an integer key")]
    [InlineData("Key<int>,Mapping<E>\nId,Code\n,\n1,A\n2,A\n", "appears twice in this Mapping column")]
    [InlineData("Key<int>,Mapping<E>\nId,Code\n,\n1,not valid\n", "cannot be used as an enum member name")]
    public void Mapping_mistakes_are_errors(string sheet, string expected)
    {
        BuildResult result = Sheets.Build(Sheets.Of("Monster", sheet));
        Assert.False(result.Success);
        Assert.Contains(expected, Sheets.Errors(result));
    }
}

public class ValidationTests
{
    private static Diagnostic SingleError(BuildResult result)
    {
        Assert.False(result.Success);
        return Assert.Single(result.Diagnostics.Items, item => item.Severity == Severity.Error);
    }

    [Fact]
    public void A_bad_cell_is_reported_with_its_position_and_a_fix()
    {
        Diagnostic error = SingleError(Sheets.Build(Sheets.Of("Item", "Key<int>,int\nId,Price\n,\n1,10\n2,ten\n")));
        Assert.Equal("Book / Item!B5", error.Location);
        Assert.Equal("'ten' is not a whole number. Write it like 10, without decimals or thousands separators.", error.Message);
        Assert.Equal("The column 'Price' is int. Example: 10", error.Hint);
    }

    [Fact]
    public void Every_bad_cell_is_reported_not_just_the_first()
    {
        BuildResult result = Sheets.Build(Sheets.Of("Item", "Key<int>,int,bool\nId,Price,Flag\n,,\n1,x,maybe\n2,y,true\n"));
        Assert.Equal(3, result.Diagnostics.ErrorCount);
        Assert.Null(result.Output);
    }

    [Fact]
    public void Duplicate_keys_point_at_both_rows()
    {
        Diagnostic error = SingleError(Sheets.Build(Sheets.Of("Item", "Key<int>,string\nId,Name\n,\n7,A\n07,B\n")));
        Assert.Equal("Book / Item!A5", error.Location);
        Assert.Contains("The key '07' is already used at A4", error.Message);
    }

    [Fact]
    public void Secondary_keys_must_be_unique_too()
    {
        Diagnostic error = SingleError(Sheets.Build(Sheets.Of("Item", "Key<int>,Key<string>\nId,Code\n,\n1,sword\n2,sword\n")));
        Assert.Contains("The key 'sword' is already used at B4", error.Message);
    }

    [Fact]
    public void An_empty_key_is_an_error()
    {
        Diagnostic error = SingleError(Sheets.Build(Sheets.Of("Item", "Key<int>,string\nId,Name\n,\n,orphan\n")));
        Assert.Equal("The key 'Id' is empty.", error.Message);
    }

    [Fact]
    public void References_must_point_at_existing_rows()
    {
        BuildResult result = Sheets.Build(
            Sheets.Of("Item", "Key<int>\nId\n\n1\n2\n"),
            Sheets.Of("Monster", "Key<int>,Ref<Item>,Ref<Item>[],Ref<Item>?\nId,Weapon,Drops,Rare\n,,,\n1,1,1|2,\n2,9,1|8,7\n"));
        Assert.False(result.Success);
        Assert.Equal(["has no row with key '9'", "has no row with key '8'", "has no row with key '7'"],
            result.Diagnostics.Items.Select(item => item.Message.Replace("The table 'Item' ", "").TrimEnd('.')));
    }

    [Fact]
    public void A_required_reference_cannot_be_empty_but_an_optional_one_can()
    {
        Diagnostic error = SingleError(Sheets.Build(
            Sheets.Of("Item", "Key<int>\nId\n\n1\n"),
            Sheets.Of("Monster", "Key<int>,Ref<Item>,Ref<Item>?\nId,Weapon,Rare\n,,\n1,,\n")));
        Assert.Equal("Book / Monster!B4", error.Location);
        Assert.Contains("a Ref<Item> must point at a row", error.Message);
    }

    [Theory]
    [InlineData("Key<int>,Ref<Nowhere>\nId,X\n,\n1,1\n", "There is no table 'Nowhere' to refer to")]
    [InlineData("Key<int>,Ref<Keyless>\nId,X\n,\n1,1\n", "The table 'Keyless' has no key to refer to")]
    [InlineData("Key<int>,money\nId,X\n,\n1,1\n", "The type 'money' is not known")]
    [InlineData("Key<float>\nId\n\n1\n", "cannot be a key")]
    [InlineData("Key<int>,int\nId,class\n,\n1,1\n", "'class' cannot be used as a field name")]
    [InlineData("Key<int>,int\nId,ToString\n,\n1,1\n", "is a name the generated row class already uses")]
    [InlineData("Key<int>,int,int\nId,Price,price\n,,\n1,1,1\n", "differ only by letter case")]
    [InlineData("Key<int>,int\nId,\n,\n1,1\n", "has no field name")]
    [InlineData("Key<int>,\nId,Price\n,\n1,1\n", "The field 'Price' has no type")]
    [InlineData("Mapping<E>\nCode\n\nA\n", "needs the table to have an integer key")]
    public void Schema_mistakes_are_errors(string sheet, string expected)
    {
        BuildResult result = Sheets.Build(Sheets.Of("Item", sheet), Sheets.Of("Keyless", "int\nValue\n\n1\n"));
        Assert.False(result.Success);
        Assert.Contains(expected, Sheets.Errors(result));
    }

    [Theory]
    [InlineData("class", "'class' cannot be used as a table name")]
    [InlineData("Tables", "'Tables' cannot be a table name")]
    [InlineData("A.B.C", "The sheet name is not understood")]
    public void Table_name_mistakes_are_errors(string sheetName, string expected)
    {
        BuildResult result = Sheets.Build(Sheets.Of(sheetName, "Key<int>\nId\n\n1\n"));
        Assert.False(result.Success);
        Assert.Contains(expected, Sheets.Errors(result));
    }

    [Fact]
    public void Generated_type_names_must_not_collide()
    {
        // The table "Item" produces ItemRow; a table named "ItemRow" would produce ItemRowRow and ItemRowTable, which is fine,
        // but an enum named ItemRow collides.
        BuildResult result = Sheets.Build(Sheets.Of("Enums", "int\nItemRow\n\nA\n"), Sheets.Of("Item", "Key<int>\nId\n\n1\n"));
        Assert.Contains("The generated name 'ItemRow' is also used by", Sheets.Errors(result));
    }

    [Fact]
    public void Arrays_accept_both_separator_and_bracket_forms()
    {
        BuildResult result = Sheets.Build(Sheets.Of("T", "Key<int>,int[],string[],float[]\nId,A,B,C\n,,,\n1,1|2|3,\"[x, y z, w]\",[1.5|2]\n2,[],,[ ]\n"));
        Assert.True(result.Success, Sheets.Errors(result));
        RowData first = result.Tables.Single().Rows[0];
        Assert.Equal([1, 2, 3], ((object[])first.Values[1]).Cast<int>());
        Assert.Equal(["x", "y z", "w"], ((object[])first.Values[2]).Cast<string>());
        Assert.Equal([1.5f, 2f], ((object[])first.Values[3]).Cast<float>());
        RowData second = result.Tables.Single().Rows[1];
        Assert.Empty((object[])second.Values[1]);
        Assert.Empty((object[])second.Values[2]);
        Assert.Empty((object[])second.Values[3]);
    }

    [Theory]
    [InlineData("1||2", "has an empty element at position 2")]
    [InlineData("1|x|3", "has a bad element at position 2: 'x' is not a whole number")]
    [InlineData("|1", "has an empty element at position 1")]
    public void Array_mistakes_name_the_element(string cell, string expected)
    {
        Diagnostic error = SingleError(Sheets.Build(Sheets.Of("T", $"Key<int>,int[]\nId,A\n,\n1,{cell}\n")));
        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public void A_custom_array_separator_is_honoured()
    {
        var config = Sheets.Config();
        config.Sheet.ArraySeparator = ';';
        BuildResult result = Sheets.Build(config, Sheets.Of("T", "Key<int>,string[]\nId,A\n,\n1,a|b;c\n"));
        Assert.True(result.Success, Sheets.Errors(result));
        Assert.Equal(["a|b", "c"], ((object[])result.Tables.Single().Rows[0].Values[1]).Cast<string>());
    }

    [Fact]
    public void A_table_cannot_store_more_values_per_row_than_the_format_allows()
    {
        string types = "Key<int>," + string.Join(",", Enumerable.Repeat("int", SchemaBuilder.MaxStoredMembers));
        string names = "Id," + string.Join(",", Enumerable.Range(0, SchemaBuilder.MaxStoredMembers).Select(index => "F" + index));
        BuildResult result = Sheets.Build(Sheets.Of("Wide", types + "\n" + names + "\n\n1\n"));
        Assert.Contains($"stores {SchemaBuilder.MaxStoredMembers + 1} values per row; the limit is {SchemaBuilder.MaxStoredMembers}", Sheets.Errors(result));
    }

    [Fact]
    public void No_tables_is_an_error_with_a_hint()
    {
        BuildResult result = Sheets.Build(Sheets.Of("#Ignored", "x\n"));
        Assert.Contains("No table was found", Sheets.Errors(result));
    }
}

public class SchemaHashTests
{
    private static BuildResult Build(string item, string monster = "Key<int>\nId\n\n1\n") =>
        Sheets.Build(Sheets.Of("Item", item), Sheets.Of("Monster", monster));

    private static string Hash(BuildResult result, string table) => result.Tables.Single(data => data.Schema.Name == table).Schema.Hash;

    [Fact]
    public void Comments_and_data_do_not_change_the_schema_hash()
    {
        BuildResult first = Build("Key<int>,string\nId,Name\nid,name\n1,A\n");
        BuildResult second = Build("Key<int>,string\nId,Name\nan id,a display name\n1,B\n2,C\n");
        Assert.Equal(first.Output!.SchemaHash, second.Output!.SchemaHash);
        Assert.NotEqual(first.Output.ContentHash, second.Output.ContentHash);
    }

    [Theory]
    [InlineData("Key<int>,int\nId,Name\n,\n1,1\n")]        // a type changed
    [InlineData("Key<int>,string\nId,Title\n,\n1,A\n")]    // a field was renamed
    [InlineData("Key<int>,string,int\nId,Name,X\n,,\n1,A,1\n")] // a column was added
    [InlineData("Key<int>,Index<string>\nId,Name\n,\n1,A\n")] // a role changed
    public void Layout_changes_change_the_schema_hash_of_that_table_only(string changed)
    {
        BuildResult first = Build("Key<int>,string\nId,Name\n,\n1,A\n");
        BuildResult second = Build(changed);
        Assert.NotEqual(first.Output!.SchemaHash, second.Output!.SchemaHash);
        Assert.NotEqual(Hash(first, "Item"), Hash(second, "Item"));
        Assert.Equal(Hash(first, "Monster"), Hash(second, "Monster"));
    }

    [Fact]
    public void A_key_type_change_reaches_the_tables_that_refer_to_it()
    {
        const string monster = "Key<int>,Ref<Item>\nId,Weapon\n,\n1,1\n";
        BuildResult first = Build("Key<int>,string\nId,Name\n,\n1,A\n", monster);
        BuildResult second = Build("Key<long>,string\nId,Name\n,\n1,A\n", monster);
        Assert.NotEqual(Hash(first, "Monster"), Hash(second, "Monster"));
    }

    [Fact]
    public void The_serializer_is_part_of_the_schema()
    {
        Sheet sheet = Sheets.Of("Item", "Key<int>\nId\n\n1\n");
        Assert.NotEqual(Sheets.Build(Sheets.Config("memorypack"), sheet).Output!.SchemaHash, Sheets.Build(Sheets.Config("messagepack"), sheet).Output!.SchemaHash);
    }

    [Fact]
    public void Builds_are_deterministic_and_do_not_depend_on_threading()
    {
        using var sequential = new SampleBuild("memorypack", config => config.Parallel = false);
        using var parallel = new SampleBuild("memorypack", config => config.Parallel = true);
        Assert.Equal(sequential.Result.Output!.ManifestText, parallel.Result.Output!.ManifestText);
        Assert.Equal(sequential.Result.Output.CodeFiles, parallel.Result.Output.CodeFiles);
        foreach (KeyValuePair<string, byte[]> file in sequential.Result.Output.DataFiles)
        {
            Assert.Equal(file.Value, parallel.Result.Output.DataFiles[file.Key]);
        }
    }
}
