using MessagePack;
using HolySheet.Core;
using HolySheet.Core.Config;
using HolySheet.Core.Output;
using HolySheet.Core.Sources;
using Xunit;

namespace HolySheet.Tests;

/// <summary>What a build must never do: delete files it did not write, or write over what another build wrote.</summary>
public sealed class DataSafetyTests
{
    /// <summary>A CSV-sourced project in a temp folder, with every output folder inside it.</summary>
    private sealed class Project : IDisposable
    {
        private readonly TempDir _temp = new();

        public Project()
        {
            Directory.CreateDirectory(Sheets);
            Write("Item", "Key<int>,int\nId,Price\n,\n1,10\n");
            Write("Monster", "Key<int>,int\nId,Hp\n,\n1,100\n");
            Config = new HolySheetConfig { BaseDirectory = _temp.Path, Parallel = false, Sources = { new SourceConfig { Type = SourceType.Csv, Path = "Sheets" } } };
        }

        public HolySheetConfig Config { get; }

        public string Sheets => _temp.Combine("Sheets");

        public string Combine(params string[] parts) => _temp.Combine(parts);

        public string Data => Config.Resolve(Config.Output.Data);

        public string Code => Config.Resolve(Config.Output.Code);

        public void Write(string sheet, string csv) => File.WriteAllText(Path.Combine(Sheets, sheet + ".csv"), csv);

        public BuildResult Run(string? only = null)
        {
            BuildResult result = TableBuild.Run(Config, new TableBuildOptions { Only = only == null ? null : SheetSelection.Parse([only]) });
            Assert.True(result.Success, HolySheet.Tests.Sheets.Errors(result));
            return result;
        }

        public void Dispose() => _temp.Dispose();
    }

    [Fact]
    public void The_csv_output_removes_only_the_files_it_wrote()
    {
        using var project = new Project();
        project.Config.Output.Csv = "Out";
        string output = project.Combine("Out");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "Notes.csv"), "kept by a person\n");

        TableBuild.Commit(project.Config, project.Run());
        Assert.True(File.Exists(Path.Combine(output, "Monster.csv")));

        // The monster table goes away: its CSV goes with it, the person's file stays.
        File.Delete(Path.Combine(project.Sheets, "Monster.csv"));
        CommitReport report = TableBuild.Commit(project.Config, project.Run());
        Assert.Contains("Monster.csv", report.Deleted);
        Assert.False(File.Exists(Path.Combine(output, "Monster.csv")));
        Assert.Equal("kept by a person\n", File.ReadAllText(Path.Combine(output, "Notes.csv")));
    }

    [Theory]
    [InlineData("New.bytes")]
    [InlineData("New.csv")]
    public void A_rebuild_preserves_unlisted_files_even_when_their_content_matches(string name)
    {
        using var project = new Project();
        project.Config.Output.Csv = "Csv";
        TableBuild.Commit(project.Config, project.Run());
        string manifest = Path.Combine(project.Data, project.Config.Output.ManifestFile);
        byte[] previousManifest = File.ReadAllBytes(manifest);
        byte[] previousItem = File.ReadAllBytes(Path.Combine(project.Data, "Item.bytes"));

        project.Write("Item", "Key<int>,int\nId,Price\n,\n1,11\n");
        project.Write("New", "Key<int>\nId\n\n42\n");
        File.Delete(Path.Combine(project.Sheets, "Monster.csv"));
        BuildResult next = project.Run();
        string existing;
        if (name.EndsWith(".csv", StringComparison.Ordinal))
        {
            existing = project.Combine("Csv", name);
            File.WriteAllText(existing, next.Output!.CsvFiles[name]);
        }
        else
        {
            existing = Path.Combine(project.Data, name);
            File.WriteAllBytes(existing, next.Output!.DataFiles[name]);
        }

        byte[] original = File.ReadAllBytes(existing);
        IOException conflict = Assert.Throws<IOException>(() => TableBuild.Commit(project.Config, next));
        Assert.Contains(existing, conflict.Message);
        Assert.Equal(original, File.ReadAllBytes(existing));
        Assert.Equal(previousManifest, File.ReadAllBytes(manifest));
        Assert.Equal(previousItem, File.ReadAllBytes(Path.Combine(project.Data, "Item.bytes")));
        Assert.True(File.Exists(Path.Combine(project.Code, "MonsterRow.g.cs")));
        Assert.False(File.Exists(Path.Combine(project.Code, "NewRow.g.cs")));

        File.Move(existing, existing + ".backup");
        TableBuild.Commit(project.Config, next);
        Assert.Equal(original, File.ReadAllBytes(existing + ".backup"));
        Assert.False(File.Exists(Path.Combine(project.Code, "MonsterRow.g.cs")));
        Assert.True(File.Exists(Path.Combine(project.Code, "NewRow.g.cs")));
    }

    [Fact]
    public void A_file_created_after_preflight_survives_commit_and_rollback()
    {
        using var temp = new TempDir();
        string existing = temp.Combine("existing");
        string created = temp.Combine("created");
        string late = temp.Combine("late");
        File.WriteAllText(existing, "previous build");
        var files = new FileChangeSet("file already exists");
        files.PlanWrite(existing, [1], "existing", canReplace: true);
        files.PlanWrite(created, [2], "created", canReplace: false);
        files.PlanWrite(late, [3], "late", canReplace: false);
        File.WriteAllText(late, "created by someone else");

        Assert.Throws<IOException>(() => files.Apply());
        Assert.Equal("previous build", File.ReadAllText(existing));
        Assert.False(File.Exists(created));
        Assert.Equal("created by someone else", File.ReadAllText(late));
    }

    [Fact]
    public void A_file_blocking_an_output_directory_is_reported_before_creating_other_outputs()
    {
        using var project = new Project();
        Directory.CreateDirectory(project.Combine("Generated"));
        File.WriteAllText(project.Data, "keep this file");

        IOException conflict = Assert.Throws<IOException>(() => TableBuild.Commit(project.Config, project.Run()));
        Assert.Contains(project.Data, conflict.Message);
        Assert.Equal("keep this file", File.ReadAllText(project.Data));
        Assert.False(Directory.Exists(project.Code));
    }

    [Fact]
    public void A_manifest_and_a_table_cannot_claim_the_same_new_path()
    {
        using var project = new Project();
        project.Config.Output.ManifestFile = "Item.bytes";

        IOException conflict = Assert.Throws<IOException>(() => TableBuild.Commit(project.Config, project.Run()));
        Assert.Contains("Item.bytes", conflict.Message);
        Assert.False(Directory.Exists(project.Data));
        Assert.False(Directory.Exists(project.Code));
    }

    [Fact]
    public void A_case_only_table_rename_can_update_its_own_files()
    {
        using var project = new Project();
        project.Config.Output.Csv = "Csv";
        TableBuild.Commit(project.Config, project.Run());
        File.Delete(Path.Combine(project.Sheets, "Item.csv"));
        project.Write("ITEM", "Key<int>,int\nId,Price\n,\n1,20\n");

        TableBuild.Commit(project.Config, project.Run());
        Assert.True(File.Exists(Path.Combine(project.Data, "ITEM.bytes")));
        Assert.Contains("1,20", File.ReadAllText(project.Combine("Csv", "ITEM.csv")));
        Assert.False(TableBuild.Commit(project.Config, project.Run()).HasChanges);
    }

    [Fact]
    public void Output_and_snapshot_folders_may_not_be_a_folder_of_sheets()
    {
        using var project = new Project();
        project.Config.Output.Csv = "Sheets";
        BuildResult sameAsInput = TableBuild.Run(project.Config);
        Assert.False(sameAsInput.Success);
        Assert.Contains("The CSV output folder 'Sheets' is also the source folder 'Sheets'", Sheets.Errors(sameAsInput));

        project.Config.Output.Csv = null;
        project.Config.Sources.Add(new SourceConfig { Type = SourceType.Google, SpreadsheetId = "id", Snapshot = "Sheets/" });
        BuildResult snapshotOnInput = TableBuild.Run(project.Config);
        Assert.False(snapshotOnInput.Success);
        Assert.Contains("The snapshot folder 'Sheets/' is also the source folder 'Sheets'", Sheets.Errors(snapshotOnInput));

        project.Config.Sources[1].Snapshot = "Snap";
        project.Config.Sources.Add(new SourceConfig { Type = SourceType.Google, SpreadsheetId = "other", Snapshot = "Snap" });
        BuildResult sharedSnapshot = TableBuild.Run(project.Config);
        Assert.Contains("Two sources save their snapshot to 'Snap'", Sheets.Errors(sharedSnapshot));
    }

    [Fact]
    public void A_snapshot_removes_only_the_tabs_it_wrote()
    {
        using var temp = new TempDir();
        string snapshot = temp.Combine("Snapshot");
        Directory.CreateDirectory(snapshot);
        File.WriteAllText(Path.Combine(snapshot, "Notes.csv"), "not a tab\n");

        GoogleSheetsSource.WriteSnapshot(snapshot, "Live Data", ["Item", "Monster"], ["a\n", "b\n"], complete: true);
        GoogleSheetsSource.WriteSnapshot(snapshot, "Live Data", ["Item"], ["a\n"], complete: true);

        Assert.True(File.Exists(Path.Combine(snapshot, "Item.csv")));
        Assert.False(File.Exists(Path.Combine(snapshot, "Monster.csv")));
        Assert.True(File.Exists(Path.Combine(snapshot, "Notes.csv")));
        Assert.Equal("Live Data", SnapshotIndex.Read(snapshot).Workbook);

        // A partial fetch keeps the tabs it did not fetch as the snapshot's own.
        GoogleSheetsSource.WriteSnapshot(snapshot, "Live Data", ["Monster"], ["b\n"], complete: false);
        Assert.Equal(["Item.csv", "Monster.csv"], SnapshotIndex.Read(snapshot).Files.OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_snapshot_keeps_local_csvs_and_preserves_tab_names_across_filename_collisions()
    {
        using var temp = new TempDir();
        File.WriteAllText(temp.Combine("Notes.csv"), "local notes");
        File.WriteAllText(temp.Combine("Item.csv"), "old");
        File.WriteAllText(temp.Combine(SnapshotIndex.FileName), "holysheet-snapshot 1\nworkbook\tLive\nfile\tItem.csv\n");

        GoogleSheetsSource.WriteSnapshot(temp.Path, "Live", ["Item", "Notes", "A/B", "A_B"],
            ["updated", "from Google", "one", "two"], complete: true);
        SnapshotIndex index = SnapshotIndex.Read(temp.Path);
        Assert.Equal("local notes", File.ReadAllText(temp.Combine("Notes.csv")));
        Assert.Equal("updated", File.ReadAllText(temp.Combine("Item.csv")));
        Assert.Equal("from Google", File.ReadAllText(temp.Combine(index.Tabs["Notes"])));
        Assert.Equal("one", File.ReadAllText(temp.Combine(index.Tabs["A/B"])));
        Assert.Equal("two", File.ReadAllText(temp.Combine(index.Tabs["A_B"])));

        var source = CsvDirectorySource.FromSnapshot(temp.Path, HolySheet.Core.Csv.BuiltinCsvParser.Instance, null, null, new SheetSelection().AddSheet("Live", "A/B"));
        var diagnostics = new DiagnosticBag();
        var offline = await source.LoadAsync(diagnostics, CancellationToken.None);
        Assert.False(diagnostics.HasErrors);
        Sheet selected = Assert.Single(Assert.Single(offline).Sheets);
        Assert.Equal("A/B", selected.Name);
        Assert.Equal("one", selected.GetCell(0, 0).Text);

        GoogleSheetsSource.WriteSnapshot(temp.Path, "Live", ["A_B"], ["changed"], complete: false);
        Assert.Equal(index.Tabs["A_B"], SnapshotIndex.Read(temp.Path).Tabs["A_B"]);
        Assert.Equal("changed", File.ReadAllText(temp.Combine(index.Tabs["A_B"])));
        GoogleSheetsSource.WriteSnapshot(temp.Path, "Live", ["Item"], ["updated"], complete: true);
        Assert.Equal("local notes", File.ReadAllText(temp.Combine("Notes.csv")));
        Assert.False(File.Exists(temp.Combine(index.Tabs["Notes"])));
    }

    [Fact]
    public void A_snapshot_never_writes_outside_its_folder()
    {
        Assert.Equal("_..", GoogleSheetsSource.SafeFileName(".."));
        Assert.Equal("_.", GoogleSheetsSource.SafeFileName("."));
        Assert.Equal("a_b", GoogleSheetsSource.SafeFileName("a/b"));
    }

    [Fact]
    public void A_build_that_started_before_another_build_wrote_the_output_is_refused()
    {
        using var project = new Project();
        TableBuild.Commit(project.Config, project.Run());

        // Two builds read the sheets at the same time; the first one to finish writes.
        BuildResult first = project.Run();
        BuildResult second = project.Run();
        project.Write("Item", "Key<int>,int\nId,Price\n,\n1,11\n");
        BuildResult third = project.Run();
        TableBuild.Commit(project.Config, third);
        byte[] item = File.ReadAllBytes(Path.Combine(project.Data, "Item.bytes"));

        IOException refused = Assert.Throws<IOException>(() => TableBuild.Commit(project.Config, first));
        Assert.Contains("Another build wrote the output", refused.Message);
        Assert.Throws<IOException>(() => TableBuild.Commit(project.Config, second));
        Assert.Equal(item, File.ReadAllBytes(Path.Combine(project.Data, "Item.bytes")));
        Assert.True(File.Exists(Path.Combine(project.Code, "GameTables.g.cs")));
    }

    [Fact]
    public void Two_partial_builds_of_different_sheets_cannot_undo_each_other()
    {
        using var project = new Project();
        TableBuild.Commit(project.Config, project.Run());

        project.Write("Item", "Key<int>,int\nId,Price\n,\n1,11\n");
        project.Write("Monster", "Key<int>,int\nId,Hp\n,\n1,200\n");
        BuildResult items = project.Run("Sheets/Item");
        BuildResult monsters = project.Run("Sheets/Monster");
        TableBuild.Commit(project.Config, items);
        byte[] item = File.ReadAllBytes(Path.Combine(project.Data, "Item.bytes"));

        // The monster build still carries the old item sheet from the cache; writing it would put the old price back.
        Assert.Throws<IOException>(() => TableBuild.Commit(project.Config, monsters));
        Assert.Equal(item, File.ReadAllBytes(Path.Combine(project.Data, "Item.bytes")));

        // Run again, it starts from the item build and keeps both changes.
        TableBuild.Commit(project.Config, project.Run("Sheets/Monster"));
        Assert.Equal(item, File.ReadAllBytes(Path.Combine(project.Data, "Item.bytes")));
    }

    [Fact]
    public void Commits_take_turns_and_a_lock_left_by_a_dead_process_is_taken_over()
    {
        using var temp = new TempDir();
        using (OutputLock.Acquire(temp.Path, TimeSpan.FromSeconds(1)))
        {
            IOException busy = Assert.Throws<IOException>(() => OutputLock.Acquire(temp.Path, TimeSpan.FromMilliseconds(100)));
            Assert.Contains("Another build is still writing", busy.Message);
        }

        Assert.False(File.Exists(temp.Combine(OutputLock.FileName)));

        // A process that crashed while holding the lock.
        File.WriteAllText(temp.Combine(OutputLock.FileName), "2147480000");
        using (OutputLock.Acquire(temp.Path, TimeSpan.FromSeconds(1)))
        {
            Assert.Equal(Environment.ProcessId.ToString(), File.ReadAllText(temp.Combine(OutputLock.FileName)));
        }
    }

    [Fact]
    public void A_partial_build_that_finds_no_workbook_fails_instead_of_dropping_its_tables()
    {
        using var project = new Project();
        TableBuild.Commit(project.Config, project.Run());

        // The cached workbook is "Sheets"; the source now reads under another name, as an offline snapshot used to.
        Directory.Move(project.Sheets, project.Combine("Renamed"));
        project.Config.Sources[0].Path = "Renamed";
        foreach (string only in new[] { "Sheets/Item", "Sheets" })
        {
            BuildResult result = TableBuild.Run(project.Config, new TableBuildOptions { Only = SheetSelection.Parse([only]) });
            Assert.False(result.Success);
            Assert.Contains("The workbook was not found in any source", Sheets.Errors(result));
        }

        Assert.True(File.Exists(Path.Combine(project.Data, "Item.bytes")));
    }

    [Fact]
    public void An_array_of_references_to_a_byte_keyed_table_reads_back_with_messagepack()
    {
        BuildResult result = Sheets.Build(Sheets.Config("messagepack"),
            Sheets.Of("Item", "Key<byte>,int\nId,Price\n,\n1,10\n2,20\n"),
            Sheets.Of("Monster", "Key<int>,Ref<Item>[],Ref<Item>\nId,Drops,Best\n,,\n1,\"1,2\",2\n2,,1\n"));
        Assert.True(result.Success, Sheets.Errors(result));

        ByteRefRow[] rows = MessagePackSerializer.Deserialize<ByteRefRow[]>(result.Output!.DataFiles["Monster.bytes"]);
        Assert.Equal([1, 2], rows[0].Drops);
        Assert.Equal(2, rows[0].Best);
        Assert.Empty(rows[1].Drops);
        // The bytes are exactly what MessagePack-CSharp writes for the generated byte[] field.
        Assert.Equal(MessagePackSerializer.Serialize(rows), result.Output.DataFiles["Monster.bytes"]);
    }
}

/// <summary>The shape HolySheet generates for Monster(Id, Drops: Ref&lt;Item&gt;[], Best: Ref&lt;Item&gt;) when Item has a byte key.</summary>
[MessagePackObject]
public sealed class ByteRefRow
{
    [Key(0)] public int Id { get; set; }

    [Key(1)] public byte[] Drops { get; set; } = [];

    [Key(2)] public byte Best { get; set; }
}
