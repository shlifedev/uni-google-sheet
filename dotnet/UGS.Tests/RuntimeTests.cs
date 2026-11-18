using System.Text;
using UGS.Core.Output;
using Mem = Sample.MemoryPackData;

namespace UGS.Tests;

public class ManifestTests
{
    [Fact]
    public void The_runtime_reads_what_the_tool_writes_and_writes_it_back_identically()
    {
        using var build = new SampleBuild("memorypack", config => config.BuildLabel = "1.4.0 (a1b2c3d)");
        string text = build.Result.Output!.ManifestText;
        TableManifest manifest = TableManifest.Parse(text);

        Assert.Equal("memorypack", manifest.Serializer);
        Assert.Equal(build.Result.Output.SchemaHash, manifest.Schema);
        Assert.Equal(build.Result.Output.ContentHash, manifest.Content);
        Assert.Equal(Mem.GameTables.SchemaHash, manifest.Schema);
        Assert.Equal("1.4.0 (a1b2c3d)", manifest.Properties["build"]);
        Assert.Equal(UGS.Core.TableBuild.ToolName, manifest.Properties["tool"]);
        Assert.Equal(["Game", "Item", "Limits", "Monster", "Text"], manifest.Tables.Select(table => table.Name));

        TableManifestEntry item = manifest.FindTable("Item")!;
        byte[] bytes = File.ReadAllBytes(Path.Combine(build.DataDirectory, item.File));
        Assert.Equal("Item.bytes", item.File);
        Assert.Equal(6, item.Rows);
        Assert.Equal(bytes.Length, item.Bytes);
        Assert.Equal(TableHash.Compute(bytes), item.Hash);
        Assert.Equal(Mem.ItemTable.SchemaHash, item.Schema);

        // Two independent implementations of the format agree byte for byte.
        Assert.Equal(text, manifest.ToText());
    }

    [Fact]
    public void Unknown_lines_and_attributes_are_kept_for_newer_tools()
    {
        const string hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        string text = $"ugs-manifest 1\nserializer memorypack\nschema s1\nfuture-thing some value\ntable Item file=Item.bytes rows=1 bytes=2 hash={hash} schema=x compression=zstd\n";
        TableManifest manifest = TableManifest.Parse(text);
        Assert.Equal("some value", manifest.Properties["future-thing"]);
        Assert.Equal("zstd", manifest.FindTable("Item")!.Extra["compression"]);
        Assert.Contains("compression=zstd", manifest.ToText());
        Assert.Contains("future-thing some value", manifest.ToText());
    }

    [Theory]
    [InlineData("", "it is empty")]
    [InlineData("not a manifest\n", "the first line is")]
    [InlineData("ugs-manifest 2\nserializer m\nschema s\n", "format version 2")]
    [InlineData("ugs-manifest 1\nschema s\n", "line is missing")]
    [InlineData("ugs-manifest 1\nserializer m\nschema s\ntable Item rows=1\n", "missing hash= or rows=")]
    [InlineData("ugs-manifest 1\nserializer m\nschema s\ntable Item rows=x hash=h\n", "expected a number")]
    [InlineData("ugs-manifest 1\nserializer m\nschema s\nsign rsa-sha256 abc\ntable Item rows=1 hash=h\n", "comes after the signature")]
    [InlineData("ugs-manifest 1\nserializer m\nschema s\nsign md5 abc\n", "signature algorithm")]
    public void A_broken_manifest_says_what_is_wrong(string text, string expected)
    {
        var exception = Assert.Throws<TableLoadException>(() => TableManifest.Parse(text));
        Assert.Equal(TableLoadError.ManifestInvalid, exception.Error);
        Assert.Contains(expected, exception.Message);
    }

    [Fact]
    public void Diff_lists_what_a_patch_must_fetch_and_drop()
    {
        static TableManifestEntry Entry(string name, string hash, string schema = "s") => new(name, name + ".bytes", 1, 10, hash, schema);
        var older = new TableManifest("memorypack", "schema-1", [Entry("Item", "h1"), Entry("Monster", "h2"), Entry("Old", "h3"), Entry("Text", "h4")]);
        var newer = new TableManifest("memorypack", "schema-2", [Entry("Item", "h1"), Entry("Monster", "h2b"), Entry("New", "h5"), Entry("Text", "h4b", "s2")]);

        TableManifestDiff diff = TableManifest.Diff(older, newer);
        Assert.Equal(["New"], diff.Added);
        Assert.Equal(["Old"], diff.Removed);
        Assert.Equal(["Monster", "Text"], diff.Changed);
        Assert.Equal(["Text"], diff.SchemaChanged);
        Assert.Equal(["Item"], diff.Unchanged);
        Assert.True(diff.RequiresNewCode);
        Assert.True(diff.HasChanges);

        Assert.False(TableManifest.Diff(older, older).HasChanges);
        // Nothing installed yet: everything is new.
        Assert.Equal(4, TableManifest.Diff(null, newer).Added.Count);
    }
}

/// <summary>Tamper and corruption detection: every way the data on disk can disagree with what was built.</summary>
public class IntegrityTests
{
    private static TableLoadException LoadFails(string directory, TableLoadOptions? options = null) =>
        Assert.Throws<TableLoadException>(() => Mem.GameTables.Load(directory, options));

    [Fact]
    public void A_changed_byte_in_a_data_file_is_detected()
    {
        using var build = new SampleBuild("memorypack");
        string path = Path.Combine(build.DataDirectory, "Item.bytes");
        byte[] bytes = File.ReadAllBytes(path);
        bytes[bytes.Length / 2] ^= 0x01;
        File.WriteAllBytes(path, bytes);

        TableLoadException exception = LoadFails(build.DataDirectory);
        Assert.Equal(TableLoadError.HashMismatch, exception.Error);
        Assert.Equal("Item", exception.TableName);
        Assert.Contains("does not match the manifest", exception.Message);
    }

    [Fact]
    public void A_truncated_or_padded_data_file_is_detected()
    {
        using var build = new SampleBuild("memorypack");
        string path = Path.Combine(build.DataDirectory, "Monster.bytes");
        byte[] bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, bytes[..^3]);
        Assert.Equal(TableLoadError.HashMismatch, LoadFails(build.DataDirectory).Error);
        File.WriteAllBytes(path, [.. bytes, 0]);
        Assert.Equal(TableLoadError.HashMismatch, LoadFails(build.DataDirectory).Error);
    }

    [Fact]
    public void A_data_file_swapped_with_one_from_another_build_is_detected()
    {
        using var first = new SampleBuild("memorypack");
        using var second = new SampleBuild("memorypack");
        // A valid, well-formed file, just not the one this manifest describes.
        File.Copy(Path.Combine(first.DataDirectory, "Game.bytes"), Path.Combine(second.DataDirectory, "Text.bytes"), overwrite: true);
        Assert.Equal(TableLoadError.HashMismatch, LoadFails(second.DataDirectory).Error);
    }

    [Fact]
    public void A_missing_data_file_or_manifest_is_reported_as_missing()
    {
        using var build = new SampleBuild("memorypack");
        File.Delete(Path.Combine(build.DataDirectory, "Text.bytes"));
        TableLoadException exception = LoadFails(build.DataDirectory);
        Assert.Equal(TableLoadError.FileMissing, exception.Error);
        Assert.Equal("Text", exception.TableName);

        File.Delete(Path.Combine(build.DataDirectory, TableManifest.DefaultFileName));
        Assert.Equal(TableLoadError.FileMissing, LoadFails(build.DataDirectory).Error);
    }

    [Fact]
    public void A_hash_edited_in_the_manifest_to_match_modified_data_is_detected()
    {
        using var build = new SampleBuild("memorypack");
        string dataPath = Path.Combine(build.DataDirectory, "Item.bytes");
        string manifestPath = Path.Combine(build.DataDirectory, TableManifest.DefaultFileName);
        string oldHash = TableHash.Compute(File.ReadAllBytes(dataPath));
        byte[] bytes = File.ReadAllBytes(dataPath);
        bytes[^1] ^= 0x7F;
        File.WriteAllBytes(dataPath, bytes);
        // The attacker also fixes the table line. The content line no longer agrees with it.
        File.WriteAllText(manifestPath, File.ReadAllText(manifestPath).Replace(oldHash, TableHash.Compute(bytes)));

        TableLoadException exception = LoadFails(build.DataDirectory);
        Assert.Equal(TableLoadError.HashMismatch, exception.Error);
        Assert.Contains("content hash does not match", exception.Message);
    }

    [Fact]
    public void Data_built_for_other_code_is_refused_and_names_the_table()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(temp.Combine("Sheets", "Csv"));
        File.Copy(Path.Combine(Repo.SampleData, "Sheets", "Game.xlsx"), temp.Combine("Sheets", "Game.xlsx"));
        // One column of Limits changes type: the layout of that table differs from the compiled sample code.
        string limits = File.ReadAllText(Path.Combine(Repo.SampleData, "Sheets", "Csv", "Limits.csv"));
        File.WriteAllText(temp.Combine("Sheets", "Csv", "Limits.csv"), limits.Replace("Key<int>,sbyte,", "Key<int>,int,"));
        var config = SampleBuild.Load("memorypack", temp.Combine("out"));
        config.BaseDirectory = temp.Path;
        var result = UGS.Core.TableBuild.Run(config);
        Assert.True(result.Success, Sheets.Errors(result));
        UGS.Core.TableBuild.Commit(config, result);

        TableLoadException exception = LoadFails(config.Resolve(config.Output.Data));
        Assert.Equal(TableLoadError.SchemaMismatch, exception.Error);
        Assert.Contains("'Limits' has a different layout", exception.Message);
        Assert.DoesNotContain("'Item'", exception.Message);
    }

    [Fact]
    public void Data_for_another_serializer_is_refused()
    {
        using var build = new SampleBuild("messagepack");
        TableLoadException exception = LoadFails(build.DataDirectory);
        Assert.Equal(TableLoadError.SchemaMismatch, exception.Error);
        Assert.Contains("'messagepack' serializer", exception.Message);
    }

    [Fact]
    public void Hash_checks_can_be_turned_off_and_then_corruption_surfaces_only_if_it_breaks_decoding()
    {
        using var build = new SampleBuild("memorypack");
        string path = Path.Combine(build.DataDirectory, "Game.bytes");
        byte[] bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, bytes[..5]);
        var exception = Assert.Throws<TableLoadException>(() => Mem.GameTables.Load(build.DataDirectory, new TableLoadOptions { VerifyHashes = false }));
        Assert.Equal(TableLoadError.DecodeFailed, exception.Error);
        Assert.Equal("Game", exception.TableName);
    }
}

public class SignatureTests
{
    private static (SampleBuild Build, string PublicKey) SignedBuild(TempDir temp)
    {
        (string privateKey, string publicKey) = SigningKeys.Generate();
        string keyPath = temp.Combine("ugs.private.key");
        File.WriteAllText(keyPath, privateKey);
        var build = new SampleBuild("memorypack", config =>
        {
            config.Signing.PrivateKeyFile = keyPath;
            config.Signing.PublicKey = publicKey;
        });
        return (build, publicKey);
    }

    [Fact]
    public void A_signed_manifest_verifies_with_its_public_key_and_no_other()
    {
        using var temp = new TempDir();
        (SampleBuild build, string publicKey) = SignedBuild(temp);
        using (build)
        {
            TableManifest manifest = TableManifest.Parse(File.ReadAllText(Path.Combine(build.DataDirectory, TableManifest.DefaultFileName)));
            Assert.True(manifest.IsSigned);
            Assert.True(TableSignature.Verify(manifest, publicKey));
            Assert.False(TableSignature.Verify(manifest, SigningKeys.Generate().PublicKey));

            // The sample code was generated without a key, so the key is supplied at load time here.
            Mem.GameTables tables = Mem.GameTables.Load(build.DataDirectory, new TableLoadOptions { PublicKey = publicKey });
            Assert.Equal(6, tables.Item.Count);
            // The public key is baked into the code generated by a signed build.
            Assert.Contains($"public const string? PublicKey = \"{publicKey}\";", build.Result.Output!.CodeFiles["GameTables.g.cs"]);
        }
    }

    [Fact]
    public void Replacing_data_and_recomputing_every_hash_still_fails_without_the_private_key()
    {
        using var temp = new TempDir();
        (SampleBuild build, string publicKey) = SignedBuild(temp);
        using (build)
        {
            // An attacker who knows the format rebuilds a fully consistent manifest for modified data. Only the signature is out of reach.
            string manifestPath = Path.Combine(build.DataDirectory, TableManifest.DefaultFileName);
            TableManifest original = TableManifest.Parse(File.ReadAllText(manifestPath));
            string dataPath = Path.Combine(build.DataDirectory, "Game.bytes");
            byte[] bytes = File.ReadAllBytes(dataPath);
            bytes[^1] ^= 0x01;
            File.WriteAllBytes(dataPath, bytes);
            var forged = new TableManifest(original.Serializer, original.Schema,
                original.Tables.Select(table => table.Name == "Game" ? new TableManifestEntry(table.Name, table.File, table.Rows, bytes.Length, TableHash.Compute(bytes), table.Schema) : table),
                original.Properties, original.Signature);
            File.WriteAllText(manifestPath, forged.ToText());

            var options = new TableLoadOptions { PublicKey = publicKey };
            var exception = Assert.Throws<TableLoadException>(() => Mem.GameTables.Load(build.DataDirectory, options));
            Assert.Equal(TableLoadError.SignatureInvalid, exception.Error);

            // Without a key the forged set loads: hashes alone detect corruption, not a deliberate, consistent replacement.
            Assert.Equal(6, Mem.GameTables.Load(build.DataDirectory).Item.Count);
        }
    }

    [Fact]
    public void An_unsigned_manifest_is_refused_when_a_key_is_required()
    {
        using var build = new SampleBuild("memorypack");
        var options = new TableLoadOptions { PublicKey = SigningKeys.Generate().PublicKey };
        var exception = Assert.Throws<TableLoadException>(() => Mem.GameTables.Load(build.DataDirectory, options));
        Assert.Equal(TableLoadError.SignatureInvalid, exception.Error);
        Assert.Contains("is not signed", exception.Message);

        options.SkipSignature = true;
        Assert.Equal(6, Mem.GameTables.Load(build.DataDirectory, options).Item.Count);
    }

    [Fact]
    public void A_stripped_signature_line_is_refused()
    {
        using var temp = new TempDir();
        (SampleBuild build, string publicKey) = SignedBuild(temp);
        using (build)
        {
            string manifestPath = Path.Combine(build.DataDirectory, TableManifest.DefaultFileName);
            string[] lines = File.ReadAllLines(manifestPath);
            File.WriteAllText(manifestPath, string.Join("\n", lines.Where(line => !line.StartsWith("sign "))) + "\n");
            var exception = Assert.Throws<TableLoadException>(() => Mem.GameTables.Load(build.DataDirectory, new TableLoadOptions { PublicKey = publicKey }));
            Assert.Equal(TableLoadError.SignatureInvalid, exception.Error);
        }
    }

    [Fact]
    public void A_public_key_without_a_private_key_stops_the_build()
    {
        using var temp = new TempDir();
        var config = SampleBuild.Load("memorypack", temp.Path);
        config.Signing.PublicKey = SigningKeys.Generate().PublicKey;
        var result = UGS.Core.TableBuild.Run(config);
        Assert.False(result.Success);
        Assert.Contains("no private key is available to sign with", Sheets.Errors(result));
    }

    [Fact]
    public void A_private_key_that_does_not_match_the_public_key_stops_the_build()
    {
        using var temp = new TempDir();
        File.WriteAllText(temp.Combine("key"), SigningKeys.Generate().PrivateKey);
        var config = SampleBuild.Load("memorypack", temp.Path);
        config.Signing.PrivateKeyFile = temp.Combine("key");
        config.Signing.PublicKey = SigningKeys.Generate().PublicKey;
        Assert.Contains("does not belong to the public key", Sheets.Errors(UGS.Core.TableBuild.Run(config)));
    }

    [Fact]
    public void The_private_key_can_come_from_the_environment()
    {
        using var temp = new TempDir();
        (string privateKey, string publicKey) = SigningKeys.Generate();
        string variable = "UGS_TEST_KEY_" + Guid.NewGuid().ToString("N");
        // CI secret stores often flatten line breaks.
        Environment.SetEnvironmentVariable(variable, privateKey.Replace("\n", "\\n"));
        try
        {
            using var build = new SampleBuild("memorypack", config => config.Signing.PrivateKeyEnv = variable);
            TableManifest manifest = TableManifest.Parse(build.Result.Output!.ManifestText);
            Assert.True(TableSignature.Verify(manifest, publicKey));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }
}

public class LoaderTests
{
    [Fact]
    public async Task Parallel_and_async_loads_give_the_same_tables_as_a_sequential_load()
    {
        using var build = new SampleBuild("memorypack");
        Mem.GameTables sequential = Mem.GameTables.Load(build.DataDirectory);
        Mem.GameTables parallel = Mem.GameTables.Load(build.DataDirectory, new TableLoadOptions { Parallel = true });
        Mem.GameTables limited = Mem.GameTables.Load(build.DataDirectory, new TableLoadOptions { Parallel = true, MaxDegreeOfParallelism = 2 });
        Mem.GameTables asynchronous = await Mem.GameTables.LoadAsync(TableSource.FromDirectory(build.DataDirectory));
        Mem.GameTables asyncParallel = await Mem.GameTables.LoadAsync(TableSource.FromDirectory(build.DataDirectory), new TableLoadOptions { Parallel = true });

        foreach (Mem.GameTables tables in new[] { parallel, limited, asynchronous, asyncParallel })
        {
            Assert.Equal(sequential.Manifest.Content, tables.Manifest.Content);
            Assert.Equal(sequential.Item.Rows.Select(row => row.Name), tables.Item.Rows.Select(row => row.Name));
            Assert.Equal(sequential.Limits.Get(2).U64, tables.Limits.Get(2).U64);
            Assert.Equal(["Game", "Item", "Limits", "Monster", "Text"], tables.LoadedTables);
        }
    }

    [Fact]
    public void A_parallel_load_reports_the_failing_table_as_a_load_exception()
    {
        using var build = new SampleBuild("memorypack");
        File.Delete(Path.Combine(build.DataDirectory, "Monster.bytes"));
        var exception = Assert.Throws<TableLoadException>(() => Mem.GameTables.Load(build.DataDirectory, new TableLoadOptions { Parallel = true }));
        Assert.Equal("Monster", exception.TableName);
    }

    [Fact]
    public void Tables_load_from_memory_and_from_a_callback()
    {
        using var build = new SampleBuild("memorypack");
        Dictionary<string, byte[]> files = Directory.GetFiles(build.DataDirectory).ToDictionary(path => Path.GetFileName(path)!, File.ReadAllBytes);
        Assert.Equal(6, Mem.GameTables.Load(TableSource.FromMemory(files)).Item.Count);

        var requested = new List<string>();
        Mem.GameTables tables = Mem.GameTables.Load(TableSource.FromDelegate(name =>
        {
            requested.Add(name);
            return files.GetValueOrDefault(name);
        }));
        Assert.Equal(3, tables.Monster.Count);
        // The manifest is read first; the schema description is tooling metadata and is never requested at run time.
        Assert.Equal(TableManifest.DefaultFileName, requested[0]);
        Assert.DoesNotContain("tables.schema.json", requested);
    }

    [Fact]
    public void Reload_decodes_only_the_tables_whose_data_changed_and_leaves_the_old_snapshot_alone()
    {
        using var temp = new TempDir();
        string sheets = CopySampleSheets(temp);
        var config = SampleBuild.Load("memorypack", temp.Combine("out"));
        config.BaseDirectory = temp.Path;
        Build(config);
        string data = config.Resolve(config.Output.Data);
        Mem.GameTables before = Mem.GameTables.Load(data);

        // Edit one value of one table, the way a designer would, and build again.
        string limitsPath = Path.Combine(sheets, "Csv", "Limits.csv");
        File.WriteAllText(limitsPath, File.ReadAllText(limitsPath).Replace("\n3,-33,128,", "\n3,-34,129,"));
        Build(config);
        Mem.GameTables after = before.Reload(TableSource.FromDirectory(data));

        Assert.Equal(["Limits"], after.LoadedTables);
        Assert.NotSame(before, after);
        Assert.NotSame(before.Limits, after.Limits);
        Assert.Same(before.Item, after.Item);
        Assert.Same(before.Monster, after.Monster);
        Assert.Same(before.Text, after.Text);
        Assert.Same(before.Game, after.Game);
        Assert.Equal(-34, after.Limits.Get(3).I8);
        // The snapshot taken earlier still holds the old value: nothing it refers to was touched.
        Assert.Equal(-33, before.Limits.Get(3).I8);
        Assert.NotEqual(before.Manifest.Content, after.Manifest.Content);

        // Nothing changed since: a reload carries every table over.
        Assert.Empty(after.Reload(TableSource.FromDirectory(data)).LoadedTables);
    }

    internal static string CopySampleSheets(TempDir temp)
    {
        string sheets = temp.Combine("Sheets");
        Directory.CreateDirectory(Path.Combine(sheets, "Csv"));
        File.Copy(Path.Combine(Repo.SampleData, "Sheets", "Game.xlsx"), Path.Combine(sheets, "Game.xlsx"));
        File.Copy(Path.Combine(Repo.SampleData, "Sheets", "Csv", "Limits.csv"), Path.Combine(sheets, "Csv", "Limits.csv"));
        return sheets;
    }

    internal static CommitReport Build(UGS.Core.Config.UgsConfig config)
    {
        UGS.Core.BuildResult result = UGS.Core.TableBuild.Run(config);
        Assert.True(result.Success, Sheets.Errors(result));
        return UGS.Core.TableBuild.Commit(config, result);
    }
}

public class WatcherTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private static void EditLimits(string sheets, string from, string to)
    {
        string path = Path.Combine(sheets, "Csv", "Limits.csv");
        string text = File.ReadAllText(path);
        Assert.Contains(from, text);
        File.WriteAllText(path, text.Replace(from, to));
    }

    [Theory]
    [InlineData(TableWatchMode.Auto)]
    [InlineData(TableWatchMode.Polling)]
    public async Task A_rebuild_is_picked_up_and_only_changed_tables_are_replaced(TableWatchMode mode)
    {
        using var temp = new TempDir();
        string sheets = LoaderTests.CopySampleSheets(temp);
        var config = SampleBuild.Load("memorypack", temp.Combine("out"));
        config.BaseDirectory = temp.Path;
        LoaderTests.Build(config);

        var reloaded = new TaskCompletionSource<TableReloadedEventArgs<Mem.GameTables>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new TableWatchOptions { Mode = mode, PollInterval = TimeSpan.FromMilliseconds(100), Debounce = TimeSpan.FromMilliseconds(50), RaiseEventsInline = true };
        using TableWatcher<Mem.GameTables> watcher = Mem.GameTables.Watch(config.Resolve(config.Output.Data), options);
        watcher.Reloaded += args => reloaded.TrySetResult(args);
        watcher.ReloadFailed += exception => reloaded.TrySetException(exception);
        Mem.GameTables first = watcher.Current;
        Assert.Equal(-33, first.Limits.Get(3).I8);

        EditLimits(sheets, "\n3,-33,128,", "\n3,-34,129,");
        LoaderTests.Build(config);

        TableReloadedEventArgs<Mem.GameTables> args = await reloaded.Task.WaitAsync(Timeout);
        Assert.Same(first, args.Previous);
        Assert.Same(watcher.Current, args.Current);
        Assert.Equal(["Limits"], args.ChangedTables);
        Assert.Equal(["Limits"], args.Diff.Changed);
        Assert.False(args.Diff.RequiresNewCode);
        Assert.Equal(-34, watcher.Current.Limits.Get(3).I8);
        Assert.Same(first.Item, watcher.Current.Item);
        Assert.Equal(-33, first.Limits.Get(3).I8);
    }

    [Fact]
    public async Task A_broken_update_keeps_the_current_tables_and_reports_the_failure()
    {
        using var build = new SampleBuild("memorypack");
        var failed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new TableWatchOptions { Mode = TableWatchMode.Polling, PollInterval = TimeSpan.FromMilliseconds(50), Debounce = TimeSpan.FromMilliseconds(20), RaiseEventsInline = true };
        using TableWatcher<Mem.GameTables> watcher = Mem.GameTables.Watch(build.DataDirectory, options);
        watcher.ReloadFailed += exception => failed.TrySetResult(exception);
        Mem.GameTables current = watcher.Current;

        // A manifest for a layout this code was not generated for, as when data is deployed ahead of the code.
        string manifestPath = Path.Combine(build.DataDirectory, TableManifest.DefaultFileName);
        File.WriteAllText(manifestPath, File.ReadAllText(manifestPath).Replace("schema " + Mem.GameTables.SchemaHash, "schema " + new string('0', 64)));

        Exception exception = await failed.Task.WaitAsync(Timeout);
        Assert.Equal(TableLoadError.SchemaMismatch, Assert.IsType<TableLoadException>(exception).Error);
        Assert.Same(current, watcher.Current);
        Assert.Equal(6, watcher.Current.Item.Count);
    }

    [Fact]
    public void Manual_mode_checks_only_when_asked()
    {
        using var temp = new TempDir();
        string sheets = LoaderTests.CopySampleSheets(temp);
        var config = SampleBuild.Load("memorypack", temp.Combine("out"));
        config.BaseDirectory = temp.Path;
        LoaderTests.Build(config);

        int events = 0;
        using TableWatcher<Mem.GameTables> watcher = Mem.GameTables.Watch(config.Resolve(config.Output.Data), new TableWatchOptions { Mode = TableWatchMode.Manual });
        watcher.Reloaded += _ => events++;
        Assert.False(watcher.CheckNow());

        EditLimits(sheets, "\n3,-33,128,", "\n3,-35,130,");
        LoaderTests.Build(config);
        Assert.Equal(-33, watcher.Current.Limits.Get(3).I8);     // not picked up until asked
        Assert.True(watcher.CheckNow());
        Assert.Equal(-35, watcher.Current.Limits.Get(3).I8);
        Assert.False(watcher.CheckNow());
        Assert.Equal(1, events);
    }

    [Fact]
    public void Rewriting_the_same_data_raises_no_event()
    {
        using var build = new SampleBuild("memorypack");
        int events = 0;
        using TableWatcher<Mem.GameTables> watcher = Mem.GameTables.Watch(build.DataDirectory, new TableWatchOptions { Mode = TableWatchMode.Manual });
        watcher.Reloaded += _ => events++;
        string manifestPath = Path.Combine(build.DataDirectory, TableManifest.DefaultFileName);
        File.WriteAllText(manifestPath, File.ReadAllText(manifestPath) + "\n");
        File.SetLastWriteTimeUtc(manifestPath, DateTime.UtcNow.AddSeconds(5));
        Assert.False(watcher.CheckNow());
        Assert.Equal(0, events);
    }

    [Fact]
    public void A_watcher_that_cannot_load_at_all_throws_from_its_constructor()
    {
        using var temp = new TempDir();
        var exception = Assert.Throws<TableLoadException>(() => Mem.GameTables.Watch(temp.Path));
        Assert.Equal(TableLoadError.FileMissing, exception.Error);
    }
}

public class ReadOnlyArrayTests
{
    [Fact]
    public void Behaves_like_a_list_that_cannot_be_written()
    {
        int[] source = [3, 1, 2];
        var array = new ReadOnlyArray<int>(source);
        Assert.Equal(3, array.Length);
        Assert.False(array.IsEmpty);
        Assert.Equal(1, array[1]);
        Assert.Equal(2, array.ItemRef(2));
        Assert.Equal(1, array.IndexOf(1));
        Assert.True(array.Contains(2));
        Assert.False(array.Contains(9));
        Assert.Equal([3, 1, 2], array.ToArray());
        Assert.NotSame(source, array.ToArray());
        Assert.Equal(6, array.Sum());

        int total = 0;
        foreach (int value in array)
        {
            total += value;
        }

        Assert.Equal(6, total);
        ReadOnlySpan<int> span = array;
        Assert.Equal(3, span.Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => array[3]);
        Assert.Throws<ArgumentOutOfRangeException>(() => array[-1]);
    }

    [Fact]
    public void The_default_value_is_an_empty_array_not_a_crash()
    {
        ReadOnlyArray<string> array = default;
        Assert.Equal(0, array.Length);
        Assert.True(array.IsEmpty);
        Assert.Empty(array);
        Assert.Empty(array.ToArray());
        Assert.Equal(0, array.AsSpan().Length);
        Assert.Equal(-1, array.IndexOf("x"));
        Assert.True(ReadOnlyArray<string>.Empty.IsEmpty);
    }
}
