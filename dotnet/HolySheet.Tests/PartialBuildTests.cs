using HolySheet.Core;
using HolySheet.Core.Config;
using HolySheet.Core.Output;
using Xunit;

namespace HolySheet.Tests;

public sealed class PartialBuildTests
{
    private sealed class Project : IDisposable
    {
        private readonly TempDir _temp = new();

        public Project()
        {
            Directory.CreateDirectory(Sheets);
            Write("Item", "Key<int>,int\nId,Price\n,\n1,10\n");
            Write("Item.Rare", "Key<int>,int\nId,Price\n,\n9,900\n");
            Write("Monster", "Key<int>,Ref<Item>\nId,Drop\n,\n1,1\n");
            Config = new HolySheetConfig { BaseDirectory = _temp.Path, Sources = { new SourceConfig { Type = SourceType.Csv, Path = "Sheets" } } };
        }

        public HolySheetConfig Config { get; }

        public string Sheets => _temp.Combine("Sheets");

        public string Data => Config.Resolve(Config.Output.Data);

        public void Write(string sheet, string csv) => File.WriteAllText(Path.Combine(Sheets, sheet + ".csv"), csv);

        public byte[] DataFile(string table) => File.ReadAllBytes(Path.Combine(Data, table + ".bytes"));

        public (BuildResult Result, CommitReport? Report) Build(string? only = null)
        {
            BuildResult result = TableBuild.Run(Config, new TableBuildOptions { Only = only == null ? null : SheetSelection.Parse([only]) });
            return (result, result.Success ? TableBuild.Commit(Config, result) : null);
        }

        public void Dispose() => _temp.Dispose();
    }

    [Fact]
    public void A_partial_build_takes_the_selected_sheets_from_the_source_and_the_others_from_the_last_build()
    {
        using var project = new Project();
        project.Build();
        byte[] monster = project.DataFile("Monster");
        byte[] item = project.DataFile("Item");

        project.Write("Item", "Key<int>,int\nId,Price\n,\n1,11\n");
        project.Write("Monster", "Key<int>,Ref<Item>\nId,Drop\n,\n1,1\n2,1\n");
        (BuildResult partial, CommitReport? report) = project.Build("Sheets/Item");

        Assert.True(partial.Success, Sheets.Errors(partial));
        Assert.Equal(["Sheets / Item"], partial.RefreshedSheets);
        Assert.Equal(HolySheet.Core.Config.SourceType.Csv, partial.WorkbookOrigins["Sheets"].Type);
        Assert.Equal("Sheets", partial.WorkbookOrigins["Sheets"].Source);
        Assert.NotEqual(item, project.DataFile("Item"));
        Assert.Equal(monster, project.DataFile("Monster"));
        Assert.Single(partial.Tables.Single(table => table.Schema.Name == "Monster").Rows);
        Assert.Equal(2, partial.Tables.Single(table => table.Schema.Name == "Item").Rows.Count);
        Assert.Contains("Item.bytes", report!.Written);
        Assert.DoesNotContain("Monster.bytes", report.Written);

        project.Build();
        Assert.NotEqual(monster, project.DataFile("Monster"));
    }

    [Fact]
    public void A_partial_build_of_unchanged_sheets_writes_nothing()
    {
        using var project = new Project();
        project.Build();
        (BuildResult result, CommitReport? report) = project.Build("Sheets");
        Assert.True(result.Success, Sheets.Errors(result));
        Assert.Equal(3, result.RefreshedSheets.Count);
        Assert.False(report!.HasChanges);
    }

    [Fact]
    public void A_selected_sheet_that_is_new_is_added_and_one_that_is_gone_is_dropped()
    {
        using var project = new Project();
        project.Build();
        project.Write("Item.Epic", "Key<int>,int\nId,Price\n,\n5,500\n");
        File.Delete(Path.Combine(project.Sheets, "Item.Rare.csv"));

        (BuildResult result, _) = project.Build("Sheets/Item.Epic,Sheets/Item.Rare");
        Assert.True(result.Success, Sheets.Errors(result));
        Assert.Equal(["1", "5"], result.RawTables.Single(table => table.Name == "Item").Rows.Select(row => row[0].Text));
        Assert.Contains(result.Diagnostics.Items, item => item.Message.Contains("not found in its source"));
    }

    [Fact]
    public void Without_a_cache_or_after_the_data_changed_elsewhere_a_partial_build_refuses()
    {
        using var project = new Project();
        BuildResult first = project.Build("Sheets").Result;
        Assert.False(first.Success);
        Assert.Contains("no sheet cache", Sheets.Errors(first));

        project.Build();
        File.AppendAllText(Path.Combine(project.Data, "tables.manifest.json"), "\n");
        BuildResult stale = project.Build("Sheets/Item").Result;
        Assert.False(stale.Success);
        Assert.Contains("not written by the last build on this machine", Sheets.Errors(stale));
    }

    [Fact]
    public void A_partial_build_can_reuse_a_version_three_cache_and_write_only_the_source_sheets()
    {
        using var project = new Project();
        project.Build();
        string index = Path.Combine(project.Config.Resolve(project.Config.CacheDirectory), "sheets.index");
        string original = File.ReadAllText(index);
        File.WriteAllText(index, original.Replace("holysheet-sheet-cache 2", "holysheet-sheet-cache 3")
            + "previews\t1\npreview\t00000000000000000000000000000000.sheet\tItem\n");

        project.Write("Item", "Key<int>,int\nId,Price\n,\n1,20\n");
        BuildResult result = project.Build("Sheets/Item").Result;
        Assert.True(result.Success, Sheets.Errors(result));
        string saved = File.ReadAllText(index);
        Assert.StartsWith("holysheet-sheet-cache 2\n", saved);
        Assert.DoesNotContain("preview", saved);
    }

    [Fact]
    public void Unknown_names_are_errors()
    {
        using var project = new Project();
        project.Build();
        BuildResult result = project.Build("Nope.xlsx,Sheets/Nope").Result;
        Assert.False(result.Success);
        Assert.Contains("No source has a workbook named 'Nope.xlsx'", Sheets.Errors(result));
        Assert.Contains("Sheets / Nope", Sheets.Errors(result));
    }

    [Fact]
    public void The_cache_keeps_every_character_of_every_cell()
    {
        using var temp = new TempDir();
        string[][] grid = [["a\tb", "line\r\nbreak", "back\\slash", " padded "], [], ["", "", "x"], ["\\t literal"]];
        var workbook = new Workbook("Book", [Sheet.FromGrid("Book", "Odd \t name", grid)]);
        SheetCache.Save(temp.Path, [workbook], "manifest");

        SheetCache cache = SheetCache.Load(temp.Path)!;
        Sheet sheet = cache.Workbooks.Single().Sheets.Single();
        Assert.Equal("Odd \t name", sheet.Name);
        Assert.Equal(SheetCache.HashManifest("manifest"), cache.ManifestHash);
        for (int row = 0; row < grid.Length; row++)
        {
            for (int column = 0; column < 4; column++)
            {
                Assert.Equal(column < grid[row].Length ? grid[row][column] : "", sheet.GetCell(row, column).Text);
            }
        }

        Assert.True(File.Exists(Path.Combine(temp.Path, ".gitignore")));
    }

    [Fact]
    public void Selections_parse_workbooks_and_sheets()
    {
        SheetSelection selection = SheetSelection.Parse(["Item.xlsx/Item.Rare, Monster.xlsx", "Item.xlsx / Item", "Monster.xlsx/Monster"]);
        Assert.True(selection.IsWholeWorkbook("Monster.xlsx"));
        Assert.True(selection.Includes("Monster.xlsx", "anything"));
        Assert.True(selection.Includes("Item.xlsx", "Item.Rare"));
        Assert.True(selection.Includes("Item.xlsx", "Item"));
        Assert.False(selection.Includes("Item.xlsx", "Equipment"));
        Assert.True(selection.Touches("Item.xlsx"));
        Assert.Equal("Monster.xlsx, Item.xlsx/Item, Item.xlsx/Item.Rare", selection.ToString());
    }
}
