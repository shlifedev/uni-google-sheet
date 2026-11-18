using UGS.Core;
using UGS.Core.Config;
using UGS.Core.Schema;
using UGS.Core.Sources;
using UGS.Core.Transform;

namespace UGS.Tests;

public class XlsxReaderTests
{
    private static Sheet Find(Workbook workbook, string name) => workbook.Sheets.Single(sheet => sheet.Name == name);

    [Fact]
    public void Reads_typed_cells_as_the_text_the_author_sees()
    {
        Workbook workbook = XlsxReader.Read(Path.Combine(Repo.SampleData, "Sheets", "Game.xlsx"));
        Assert.Equal(["Enums", "Item", "Item.Rare", "Item.Col.Balance", "Monster", "Text", "Game.Config", "#Notes"], workbook.Sheets.Select(sheet => sheet.Name));

        Sheet item = Find(workbook, "Item");
        Assert.Equal("Key<int>", item.GetCell(0, 0).Text);
        Assert.Equal("1001", item.GetCell(3, 0).Text);          // a number cell holding 1001, not "1001.0"
        Assert.Equal("2.5", item.GetCell(3, 7).Text);
        Assert.Equal("4", item.GetCell(4, 7).Text);             // 4 typed into a decimal column
        Assert.Equal("true", item.GetCell(3, 9).Text);          // a boolean cell
        Assert.Equal("2026-01-31", item.GetCell(3, 10).Text);   // a date cell without a time
        Assert.Equal("2026-01-31 13:30:00", item.GetCell(4, 10).Text);
        Assert.Equal("포션 Potion", item.GetCell(5, 2).Text);
        Assert.Equal("Ore \"raw\", heavy\nsecond line", item.GetCell(8, 2).Text);
        Assert.Equal("9007199254740991", item.GetCell(8, 13).Text);
        Assert.True(item.GetCell(7, 0).IsBlank);                // the blank row keeps its place
        Assert.Equal("B4", item.GetCell(3, 1).Origin.A1);
    }

    [Fact]
    public void A_workbook_saved_by_another_program_reads_the_same()
    {
        // The same workbook written by openpyxl and re-saved by LibreOffice: different string storage, styles and date formats.
        Workbook original = XlsxReader.Read(Path.Combine(Repo.SampleData, "Sheets", "Game.xlsx"));
        Workbook resaved = XlsxReader.Read(Repo.Fixture("Game.libreoffice.xlsx"));
        Assert.Equal(original.Sheets.Select(sheet => sheet.Name), resaved.Sheets.Select(sheet => sheet.Name));
        foreach (Sheet expected in original.Sheets)
        {
            Sheet actual = Find(resaved, expected.Name);
            for (int row = 0; row < expected.RowCount; row++)
            {
                for (int column = 0; column < expected.ColumnCount; column++)
                {
                    Assert.True(expected.GetCell(row, column).Text == actual.GetCell(row, column).Text,
                        $"{expected.Name}!{expected.GetCell(row, column).Origin.A1}: '{expected.GetCell(row, column).Text}' vs '{actual.GetCell(row, column).Text}'");
                }
            }
        }
    }

    [Theory]
    [InlineData("yyyy-mm-dd", true)]
    [InlineData("h:mm AM/PM", true)]
    [InlineData("[$-409]d-mmm-yy", true)]
    [InlineData("General", false)]
    [InlineData("0.00", false)]
    [InlineData("#,##0 \"days\"", false)]
    [InlineData("[Red]0.0", false)]
    [InlineData("0.00E+00", false)]
    public void Tells_date_formats_from_number_formats(string code, bool isDate)
    {
        Assert.Equal(isDate, XlsxReader.IsDateFormatCode(code));
    }

    [Fact]
    public void A_file_that_is_not_a_workbook_is_reported_not_thrown_raw()
    {
        using var temp = new TempDir();
        string path = temp.Combine("Broken.xlsx");
        File.WriteAllText(path, "this is not a zip file");
        var diagnostics = new DiagnosticBag();
        IReadOnlyList<Workbook> workbooks = new XlsxSource(path).LoadAsync(diagnostics, default).Result;
        Assert.Empty(workbooks);
        Assert.Contains("could not be read as an xlsx workbook", Assert.Single(diagnostics.Items).Message);
    }

    [Fact]
    public void Excel_lock_files_are_not_read_as_workbooks()
    {
        using var temp = new TempDir();
        File.Copy(Path.Combine(Repo.SampleData, "Sheets", "Game.xlsx"), temp.Combine("Game.xlsx"));
        File.WriteAllText(temp.Combine("~$Game.xlsx"), "lock");
        var diagnostics = new DiagnosticBag();
        IReadOnlyList<Workbook> workbooks = new XlsxSource(temp.Path).LoadAsync(diagnostics, default).Result;
        Assert.Single(workbooks);
        Assert.Empty(diagnostics.Items);
    }
}

public class SheetNameTests
{
    [Theory]
    [InlineData("Item", SheetKind.Table, "Item")]
    [InlineData("Game.Config", SheetKind.Config, "Game")]
    [InlineData("Item.Rare", SheetKind.ChildRows, "Item")]
    [InlineData("Item.Col.Balance", SheetKind.ColumnExtension, "Item")]
    [InlineData("Enums", SheetKind.Enums, "")]
    [InlineData("Battle.Enums", SheetKind.Enums, "")]
    [InlineData("#Notes", SheetKind.Ignored, "")]
    [InlineData("~Draft", SheetKind.Ignored, "")]
    [InlineData("A.B.C.D", SheetKind.Invalid, "")]
    [InlineData("A.B.C", SheetKind.Invalid, "")]
    [InlineData("  ", SheetKind.Invalid, "")]
    public void Role_comes_from_the_name(string name, SheetKind kind, string table)
    {
        SheetRole role = SheetRole.FromName(name);
        Assert.Equal(kind, role.Kind);
        Assert.Equal(table, role.TableName);
    }
}

public class TransformRuleTests
{
    private static RawTable Table(BuildResult result, string name) => result.RawTables.Single(table => table.Name == name);

    [Fact]
    public void Ignored_columns_rows_and_blank_rows_are_dropped()
    {
        BuildResult result = Sheets.Build(Sheets.Of("Item", """
            Key<int>,string,#note,int,
            Id,Name,Memo,~Hidden,
            id,name,memo,hidden,#comment marks it too
            1,Sword,keep out,5,x
            ~2,Skipped,,,
            #3,Skipped too,,,
            ,,,,
            4,Shield,,7,y
            """));
        Assert.True(result.Success, Sheets.Errors(result));
        RawTable item = Table(result, "Item");
        Assert.Equal(["Id", "Name"], item.Columns.Select(column => column.Name));
        Assert.Equal(["1", "4"], item.Rows.Select(row => row[0].Text));
    }

    [Fact]
    public void A_row_is_ignored_by_its_first_cell_even_when_that_column_is_not_read()
    {
        BuildResult result = Sheets.Build(Sheets.Of("Item", "#flag,Key<int>,string\nFlag,Id,Name\n,,\n,1,A\n~,2,B\n,3,C\n"));
        Assert.True(result.Success, Sheets.Errors(result));
        Assert.Equal(["1", "3"], Table(result, "Item").Rows.Select(row => row[0].Text));
    }

    [Fact]
    public void A_config_sheet_is_read_sideways()
    {
        BuildResult result = Sheets.Build(Sheets.Of("Game.Config", "int,MaxLevel,highest level,60\nstring,Title,shown on start,Hello\n"));
        Assert.True(result.Success, Sheets.Errors(result));
        RawTable game = Table(result, "Game");
        Assert.Equal(TableKind.Config, game.Kind);
        Assert.Equal(["MaxLevel", "Title"], game.Columns.Select(column => column.Name));
        Assert.Equal(["60", "Hello"], game.Rows.Single().Select(cell => cell.Text));
        // A problem in a config value still points at the cell the author typed into.
        Assert.Equal("D1", game.Rows[0][0].Origin.A1);
    }

    [Fact]
    public void A_config_sheet_must_hold_exactly_one_set_of_values()
    {
        BuildResult result = Sheets.Build(Sheets.Of("Game.Config", "int,MaxLevel,c,60,61\n"));
        Assert.False(result.Success);
        Assert.Contains("exactly one set of values", Sheets.Errors(result));
    }

    [Fact]
    public void Child_sheets_add_rows_whatever_their_column_order()
    {
        BuildResult result = Sheets.Build(
            Sheets.Of("Item", "Key<int>,string\nId,Name\n,\n1,Sword\n"),
            Sheets.Of("Item.More", "string,Key<int>\nName,Id\n,\nShield,2\nBow,3\n"));
        Assert.True(result.Success, Sheets.Errors(result));
        Assert.Equal(["1:Sword", "2:Shield", "3:Bow"], Table(result, "Item").Rows.Select(row => row[0].Text + ":" + row[1].Text));
    }

    [Fact]
    public void Child_and_column_sheets_merge_in_name_order_whatever_order_the_source_lists_them()
    {
        Sheet item = Sheets.Of("Item", "Key<int>,string\nId,Name\n,\n1,base\n");
        Sheet zeta = Sheets.Of("Item.Zeta", "Key<int>,string\nId,Name\n,\n3,zeta\n");
        Sheet alpha = Sheets.Of("Item.Alpha", "Key<int>,string\nId,Name\n,\n2,alpha\n");
        Sheet colB = Sheets.Of("Item.Col.B", "Key<int>,int\nId,B\n,\n1,20\n");
        Sheet colA = Sheets.Of("Item.Col.A", "Key<int>,int\nId,A\n,\n1,10\n");

        // The order an xlsx author might arrange the tabs in, and the reverse.
        BuildResult first = Sheets.Build(item, zeta, alpha, colB, colA);
        BuildResult second = Sheets.Build(colA, colB, alpha, zeta, item);
        Assert.True(first.Success, Sheets.Errors(first));
        Assert.True(second.Success, Sheets.Errors(second));
        Assert.Equal(["1", "2", "3"], Table(first, "Item").Rows.Select(row => row[0].Text));
        Assert.Equal(["Id", "Name", "A", "B"], Table(first, "Item").Columns.Select(column => column.Name));
        Assert.Equal(first.Output!.ContentHash, second.Output!.ContentHash);
        Assert.Equal(first.Output.SchemaHash, second.Output.SchemaHash);
    }

    [Fact]
    public void A_child_sheet_with_other_columns_is_an_error()
    {
        BuildResult result = Sheets.Build(
            Sheets.Of("Item", "Key<int>,string\nId,Name\n,\n1,Sword\n"),
            Sheets.Of("Item.More", "Key<int>,int\nId,Price\n,\n2,5\n"));
        Assert.False(result.Success);
        string errors = Sheets.Errors(result);
        Assert.Contains("The column 'Name' (string) of table 'Item' is missing here", errors);
        Assert.Contains("has no column 'Price' (int)", errors);
    }

    [Fact]
    public void A_child_sheet_without_its_table_is_an_error()
    {
        BuildResult result = Sheets.Build(Sheets.Of("Item.More", "Key<int>\nId\n\n1\n"));
        Assert.Contains("There is no table 'Item' for this sheet to add rows to", Sheets.Errors(result));
    }

    [Fact]
    public void Column_extensions_join_by_key_and_leave_unlisted_rows_blank()
    {
        BuildResult result = Sheets.Build(
            Sheets.Of("Item", "Key<int>,string\nId,Name\n,\n1,Sword\n2,Shield\n3,Bow\n"),
            Sheets.Of("Item.Col.Balance", "Key<int>,int\nId,Attack\n,\n03,9\n1,5\n"));
        Assert.True(result.Success, Sheets.Errors(result));
        RawTable item = Table(result, "Item");
        Assert.Equal(["Id", "Name", "Attack"], item.Columns.Select(column => column.Name));
        // Keys match as numbers: "03" on the extension sheet is row 3.
        Assert.Equal(["5", "", "9"], item.Rows.Select(row => row[2].Text));
        Assert.Equal([5, 0, 9], result.Tables.Single().Rows.Select(row => (int)row.Values[2]));
    }

    [Theory]
    [InlineData("Key<int>,int\nId,Attack\n,\n9,5\n", "has no row with key '9'")]
    [InlineData("Key<int>,int\nId,Attack\n,\n1,5\n1,6\n", "appears twice on this sheet")]
    [InlineData("Key<string>,int\nId,Attack\n,\n1,5\n", "The key type here is 'Key<string>' but 'Item' uses 'Key<int>'")]
    [InlineData("int,int\nId,Attack\n,\n1,5\n", "This sheet has no Key<...> column")]
    public void Column_extension_mistakes_are_errors(string extension, string expected)
    {
        BuildResult result = Sheets.Build(
            Sheets.Of("Item", "Key<int>,string\nId,Name\n,\n1,Sword\n"),
            Sheets.Of("Item.Col.Balance", extension));
        Assert.False(result.Success);
        Assert.Contains(expected, Sheets.Errors(result));
    }

    [Fact]
    public void Repeated_field_names_merge_into_one_array()
    {
        BuildResult result = Sheets.Build(Sheets.Of("Item", "Key<int>,int,int,int[]\nId,Socket,Socket,Socket\n,,,\n1,3,,4|5\n2,,,\n"));
        Assert.True(result.Success, Sheets.Errors(result));
        TableSchema schema = result.Tables.Single().Schema;
        Assert.Equal("int[]", schema.Columns[1].Type.CanonicalName);
        Assert.Equal([3, 4, 5], ((object[])result.Tables.Single().Rows[0].Values[1]).Cast<int>());
        Assert.Empty((object[])result.Tables.Single().Rows[1].Values[1]);
    }

    [Fact]
    public void Repeated_field_names_need_one_type()
    {
        BuildResult result = Sheets.Build(Sheets.Of("Item", "Key<int>,int,string\nId,Socket,Socket\n,,\n1,3,x\n"));
        Assert.Contains("appears more than once with different types (int and string)", Sheets.Errors(result));
    }

    [Fact]
    public void The_header_layout_is_configurable()
    {
        // The comment, name, type order used by another convention.
        UgsConfig config = Sheets.Config();
        config.Sheet.Header = ["comment", "name", "type"];
        BuildResult result = Sheets.Build(config, Sheets.Of("Item", "item id,display name\nId,Name\nKey<int>,string\n1,Sword\n"));
        Assert.True(result.Success, Sheets.Errors(result));
        Assert.Equal("item id", result.Tables.Single().Schema.Columns[0].Comment);
        Assert.Equal(1, result.Tables.Single().Rows[0].Values[0]);
    }

    [Fact]
    public void A_layout_without_a_comment_row_starts_data_on_the_third_row()
    {
        UgsConfig config = Sheets.Config();
        config.Sheet.Header = ["name", "type"];
        BuildResult result = Sheets.Build(config, Sheets.Of("Item", "Id,Name\nKey<int>,string\n1,Sword\n"));
        Assert.True(result.Success, Sheets.Errors(result));
        Assert.Single(result.Tables.Single().Rows);
    }

    [Fact]
    public void Values_under_a_column_without_a_header_get_one_warning()
    {
        BuildResult result = Sheets.Build(Sheets.Of("Item", "Key<int>,,string\nId,,Name\n,,\n1,oops,A\n2,again,B\n"));
        Assert.True(result.Success, Sheets.Errors(result));
        Diagnostic warning = Assert.Single(result.Diagnostics.Items);
        Assert.Equal(Severity.Warning, warning.Severity);
        Assert.Equal("B4", warning.Origin!.Value.A1);
    }

    [Fact]
    public void Two_sheets_cannot_define_the_same_table()
    {
        var workbookA = new Workbook("A", [Sheets.Of("Item", "Key<int>\nId\n\n1\n", "A")]);
        var workbookB = new Workbook("B", [Sheets.Of("Item", "Key<int>\nId\n\n2\n", "B")]);
        BuildResult result = TableBuild.Run(Sheets.Config(), new TableBuildOptions { Sources = [new MemoryWorkbookSource(workbookA, workbookB)] });
        Assert.Contains("The table 'Item' is already defined by 'A / Item'", Sheets.Errors(result));
    }

    [Fact]
    public void Steps_can_be_added_to_the_pipeline()
    {
        TransformPipeline pipeline = TransformPipeline.CreateDefault().InsertAfter(ReadTablesStep.StepName, new DropTableStep("Scratch"));
        Assert.Equal(["classify", "transpose-config", "read-tables", "drop-table", "merge-child-rows", "join-column-extensions"], pipeline.Steps.Select(step => step.Name));
        var workbook = new Workbook("Book", [Sheets.Of("Item", "Key<int>\nId\n\n1\n"), Sheets.Of("Scratch", "Key<int>\nId\n\n1\n")]);
        BuildResult result = TableBuild.Run(Sheets.Config(), new TableBuildOptions { Sources = [new MemoryWorkbookSource(workbook)], Pipeline = pipeline });
        Assert.True(result.Success, Sheets.Errors(result));
        Assert.Equal(["Item"], result.Tables.Select(table => table.Schema.Name));
        Assert.Throws<ArgumentException>(() => pipeline.Remove("no-such-step"));
    }

    private sealed class DropTableStep(string tableName) : ITransformStep
    {
        public string Name => "drop-table";

        public void Run(TransformContext context) => context.Tables.RemoveAll(table => table.Name == tableName);
    }

    [Fact]
    public void Transformed_tables_can_be_written_as_csv()
    {
        BuildResult result = Sheets.Build(
            Sheets.Of("Item", "Key<int>,string,#x\nId,Name,Note\nid,\"name, shown\",n\n1,\"a \"\"quoted\"\" one\",n\n"),
            Sheets.Of("Item.More", "Key<int>,string\nId,Name\n,\n2,\"two\nlines\"\n"));
        Assert.True(result.Success, Sheets.Errors(result));
        Assert.Equal("Key<int>,string\nId,Name\nid,\"name, shown\"\n1,\"a \"\"quoted\"\" one\"\n2,\"two\nlines\"\n", Table(result, "Item").ToCsv(Sheets.Config().Sheet));
    }
}
