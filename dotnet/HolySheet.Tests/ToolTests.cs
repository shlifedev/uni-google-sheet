using System.Net;
using System.Security.Cryptography;
using System.Text;
using HolySheet.Core;
using HolySheet.Core.Config;
using HolySheet.Core.Csv;
using HolySheet.Core.Generation;
using HolySheet.Core.Output;
using HolySheet.Core.Sources;

namespace HolySheet.Tests;

public class CommitTests
{
    [Fact]
    public void A_second_build_of_the_same_sheets_writes_nothing()
    {
        using var build = new SampleBuild("memorypack");
        Assert.True(build.Report.HasChanges);
        DateTime stamp = File.GetLastWriteTimeUtc(Path.Combine(build.DataDirectory, "Item.bytes"));

        CommitReport second = build.Rebuild();
        Assert.False(second.HasChanges);
        Assert.Equal(build.Report.Written.Count, second.Unchanged);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(Path.Combine(build.DataDirectory, "Item.bytes")));
    }

    [Fact]
    public void A_released_result_keeps_what_the_window_shows_and_nothing_else()
    {
        using var build = new SampleBuild("memorypack");
        BuildResult result = build.Result;
        int tables = result.TableCount;
        int rows = result.RowCount;
        int rawTables = result.RawTables.Count;
        result.ReleaseBuildData();

        Assert.True(result.IsReleased);
        Assert.True(result.Success);
        Assert.Equal(tables, result.TableCount);
        Assert.Equal(rows, result.RowCount);
        Assert.Equal(rawTables, result.RawTables.Count);
        Assert.Empty(result.Tables);
        Assert.Empty(result.Workbooks);
        Assert.Null(result.Output);
        Assert.Contains("released", Assert.Throws<InvalidOperationException>(() => TableBuild.Commit(build.Config, result)).Message);
    }

    [Fact]
    public void The_manifest_is_written_last()
    {
        using var build = new SampleBuild("memorypack");
        Assert.Equal("tables.manifest.json", build.Report.Written[^1]);
    }

    [Fact]
    public void Files_of_a_removed_table_are_deleted_and_files_the_tool_did_not_make_are_kept()
    {
        using var temp = new TempDir();
        string sheets = temp.Combine("Sheets");
        Directory.CreateDirectory(sheets);
        File.WriteAllText(Path.Combine(sheets, "Item.csv"), "Key<int>\nId\n\n1\n");
        File.WriteAllText(Path.Combine(sheets, "Old.csv"), "Key<int>\nId\n\n1\n");
        var config = new HolySheetConfig { BaseDirectory = temp.Path, Sources = { new SourceConfig { Type = SourceType.Csv, Path = "Sheets" } } };
        LoaderTests.Build(config);

        string code = config.Resolve(config.Output.Code);
        string data = config.Resolve(config.Output.Data);
        // Things a person or Unity put next to the generated files.
        File.WriteAllText(Path.Combine(code, "ItemTable.Custom.cs"), "// hand written partial class");
        File.WriteAllText(Path.Combine(code, "Mine.g.cs"), "// a .g.cs file from some other generator");
        File.WriteAllText(Path.Combine(code, "OldRow.g.cs.meta"), "guid: 1");
        File.WriteAllText(Path.Combine(data, "Old.bytes.meta"), "guid: 2");
        File.WriteAllText(Path.Combine(data, "notes.bytes"), "not a table");

        File.Delete(Path.Combine(sheets, "Old.csv"));
        CommitReport report = LoaderTests.Build(config);

        Assert.Equal(["OldRow.g.cs", "OldTable.g.cs", "Old.bytes"], report.Deleted);
        Assert.False(File.Exists(Path.Combine(code, "OldRow.g.cs")));
        Assert.False(File.Exists(Path.Combine(code, "OldRow.g.cs.meta")));   // Unity's .meta goes with its asset
        Assert.False(File.Exists(Path.Combine(data, "Old.bytes")));
        Assert.False(File.Exists(Path.Combine(data, "Old.bytes.meta")));
        Assert.True(File.Exists(Path.Combine(code, "ItemTable.Custom.cs")));
        Assert.True(File.Exists(Path.Combine(code, "Mine.g.cs")));
        Assert.True(File.Exists(Path.Combine(data, "notes.bytes")));
        Assert.True(File.Exists(Path.Combine(code, "ItemRow.g.cs")));
    }

    [Fact]
    public void A_build_with_errors_writes_nothing_and_leaves_the_last_good_output_in_place()
    {
        using var temp = new TempDir();
        string sheets = temp.Combine("Sheets");
        Directory.CreateDirectory(sheets);
        File.WriteAllText(Path.Combine(sheets, "Item.csv"), "Key<int>,int\nId,Price\n,\n1,10\n");
        var config = new HolySheetConfig { BaseDirectory = temp.Path, Sources = { new SourceConfig { Type = SourceType.Csv, Path = "Sheets" } } };
        LoaderTests.Build(config);
        string manifest = File.ReadAllText(Path.Combine(config.Resolve(config.Output.Data), "tables.manifest.json"));

        File.WriteAllText(Path.Combine(sheets, "Item.csv"), "Key<int>,int\nId,Price\n,\n1,ten\n");
        BuildResult broken = TableBuild.Run(config);
        Assert.False(broken.Success);
        Assert.Null(broken.Output);
        Assert.Throws<InvalidOperationException>(() => TableBuild.Commit(config, broken));
        Assert.Equal(manifest, File.ReadAllText(Path.Combine(config.Resolve(config.Output.Data), "tables.manifest.json")));
    }

    [Fact]
    public void A_manifest_cannot_make_the_tool_delete_outside_its_folder()
    {
        using var temp = new TempDir();
        string victim = temp.Combine("victim.txt");
        File.WriteAllText(victim, "keep me");
        string data = temp.Combine("data");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "tables.manifest.json"),
            "{ \"holysheet\": 1, \"serializer\": \"memorypack\", \"schema\": \"s\", \"tables\": [ { \"name\": \"Evil\", \"file\": \"../victim.txt\", \"rows\": 0, \"hash\": \"h\" } ] }");
        // The old text manifest is read once to clean up after it, with the same rule.
        File.WriteAllText(Path.Combine(data, "tables.manifest.txt"), "holysheet-manifest 1\nserializer memorypack\nschema s\ntable Evil file=../victim.txt rows=0 bytes=0 hash=h schema=s\n");

        var output = new GeneratedOutput { ManifestText = "{ \"holysheet\": 1, \"serializer\": \"memorypack\", \"schema\": \"s\", \"tables\": [] }\n" };
        OutputCommitter.Commit(output, temp.Combine("code"), data);
        Assert.True(File.Exists(victim));
    }

    [Fact]
    public void A_build_removes_the_old_text_manifest_and_the_files_only_it_listed()
    {
        using var temp = new TempDir();
        string data = temp.Combine("data");
        Directory.CreateDirectory(data);
        File.WriteAllBytes(Path.Combine(data, "Old.bytes"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(data, "Kept.bytes"), [1]);
        File.WriteAllText(Path.Combine(data, "tables.manifest.txt"),
            "holysheet-manifest 1\nserializer memorypack\nschema s\ntable Kept file=Kept.bytes rows=0 bytes=1 hash=h schema=s\ntable Old file=Old.bytes rows=0 bytes=3 hash=h schema=s\n");
        // A file of the same name that this tool did not write is someone else's.
        string other = temp.Combine("other");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "tables.manifest.txt"), "my notes\n");

        var output = new GeneratedOutput { ManifestText = "{ \"holysheet\": 1, \"serializer\": \"memorypack\", \"schema\": \"s\", \"tables\": [] }\n" };
        output.DataFiles["Kept.bytes"] = [1];
        OutputCommitter.Commit(output, temp.Combine("code"), data);
        Assert.False(File.Exists(Path.Combine(data, "tables.manifest.txt")));
        Assert.False(File.Exists(Path.Combine(data, "Old.bytes")));
        Assert.True(File.Exists(Path.Combine(data, "Kept.bytes")));
        Assert.True(File.Exists(Path.Combine(data, "tables.manifest.json")));

        OutputCommitter.Commit(output, temp.Combine("code"), other);
        Assert.Equal("my notes\n", File.ReadAllText(Path.Combine(other, "tables.manifest.txt")));
    }

    [Fact]
    public void Transformed_csv_is_written_when_asked_for()
    {
        using var temp = new TempDir();
        using var build = new SampleBuild("memorypack", config => config.Output.Csv = temp.Combine("csv"));
        string[] files = Directory.GetFiles(temp.Combine("csv")).Select(path => Path.GetFileName(path)!).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        // Beside the tables, the list of what the tool wrote: the only files a later build may remove.
        Assert.Equal([OutputCommitter.CsvListFileName, "Game.csv", "Item.csv", "Limits.csv", "Monster.csv", "Text.csv"], files);
        // What was written reads back as the same table.
        List<string[]> rows = BuiltinCsvParser.Instance.Parse(File.ReadAllText(temp.Combine("csv", "Item.csv")));
        Assert.Equal(9, rows.Count);
        Assert.Equal("Ore \"raw\", heavy\nsecond line", rows[6][2]);
        Assert.DoesNotContain("Memo", rows[1]);
    }

    [Fact]
    public void The_schema_description_lists_every_table_column_and_enum()
    {
        using var build = new SampleBuild("memorypack");
        JsonObject schema = JsonValue.Parse(File.ReadAllText(Path.Combine(build.DataDirectory, SchemaJson.FileName))).AsObject!;
        Assert.Equal(build.Result.Output!.SchemaHash, schema.GetString("schema"));
        JsonObject monster = schema.GetArray("tables")!.Select(item => item.AsObject!).Single(table => table.GetString("name") == "Monster");
        Assert.Equal("Id", monster.GetString("primaryKey"));
        JsonObject rare = monster.GetArray("columns")!.Select(item => item.AsObject!).Single(column => column.GetString("name") == "Rare");
        Assert.Equal("Ref<Item>?", rare.GetString("type"));
        Assert.Equal("Item", rare.GetString("references"));
        Assert.Equal("int", rare.GetString("stored"));
        Assert.Equal(2, rare.GetNumber("members"));
        Assert.Contains(schema.GetArray("enums")!, item => item.AsObject!.GetString("name") == "EMonsterId");
    }

    [Fact]
    public void Generated_files_carry_the_marker_the_committer_looks_for()
    {
        using var build = new SampleBuild("memorypack");
        Assert.All(build.Result.Output!.CodeFiles.Values, code => Assert.Contains("Generated by HolySheet", code.Split('\n')[1]));
    }
}

public class ConfigTests
{
    [Fact]
    public void A_config_survives_a_round_trip_through_json()
    {
        var config = new HolySheetConfig
        {
            Sources =
            {
                new SourceConfig { Type = SourceType.Xlsx, Path = "Sheets" },
                new SourceConfig
                {
                    Type = SourceType.Google, SpreadsheetId = "abc123", Name = "Live", Snapshot = "Sheets/Snapshot",
                    Auth = { Type = GoogleAuthType.ServiceAccount, KeyFile = "secrets/sa.json" },
                    Sheets = { new GoogleSheetRef("Item", 0), new GoogleSheetRef("Monster", 123456) }
                }
            },
            Output = { Code = "Assets/Gen", Data = "Assets/StreamingAssets/Tables", Csv = "Csv", Schema = false },
            Codegen = { Namespace = "My.Game", TablesClass = "Db", Serializer = "messagepack" },
            Sheet = { Header = ["comment", "name", "type"], ArraySeparator = ';' },
            CsvBackend = "builtin",
            Signing = { PublicKey = "rsa:AAAA:AQAB", PrivateKeyEnv = "HOLYSHEET_KEY" },
            BuildLabel = "1.2.3",
            Parallel = false
        };
        HolySheetConfig parsed = HolySheetConfig.Parse(config.ToJson(), config.BaseDirectory);
        Assert.Equal(config.ToJson(), parsed.ToJson());
        Assert.Equal(GoogleAuthType.ServiceAccount, parsed.Sources[1].Auth.Type);
        Assert.Equal(123456, parsed.Sources[1].Sheets[1].Gid);
        Assert.Equal(2, parsed.Sheet.TypeRow);
        Assert.Equal(';', parsed.Sheet.ArraySeparator);
    }

    [Fact]
    public void A_hand_written_config_may_have_comments_and_trailing_commas()
    {
        HolySheetConfig config = HolySheetConfig.Parse("""
            {
              // where the sheets are
              "sources": [ { "type": "csv", "path": "Sheets", }, ],
              /* everything else keeps its default */
              "codegen": { "serializer": "MessagePack" },
            }
            """, "/tmp");
        Assert.Equal(SourceType.Csv, Assert.Single(config.Sources).Type);
        Assert.Equal("messagepack", config.Codegen.Serializer);
        Assert.Equal("GameTables", config.Codegen.TablesClass);
    }

    [Theory]
    [InlineData("{ \"sources\": [ { \"type\": \"ods\" } ] }", "source type \"ods\"")]
    [InlineData("{ \"sources\": [ 1 ] }", "must be an object")]
    [InlineData("[]", "must be a JSON object")]
    [InlineData("{ \"sources\": ", "line 1, column 14: unexpected end of text")]
    [InlineData("{ \"a\": tru }", "expected 'true'")]
    public void A_broken_config_says_where(string json, string expected)
    {
        Assert.Contains(expected, Assert.Throws<JsonFormatException>(() => HolySheetConfig.Parse(json, "/tmp")).Message);
    }

    [Fact]
    public void Settings_that_cannot_work_are_reported_before_any_sheet_is_read()
    {
        var config = new HolySheetConfig
        {
            Codegen = { Namespace = "1Bad", TablesClass = "class", Serializer = "protobuf" },
            Sheet = { Header = ["name", "comment"] },
            Sources = { new SourceConfig { Type = SourceType.Google }, new SourceConfig { Type = SourceType.Xlsx } }
        };
        BuildResult result = TableBuild.Run(config);
        string errors = Sheets.Errors(result);
        Assert.Contains("A google source has no spreadsheetId", errors);
        Assert.Contains("A xlsx source has no path", errors);
        Assert.Contains("is not a valid C# namespace", errors);
        Assert.Contains("is not a valid C# identifier", errors);
        Assert.Contains("The serializer 'protobuf' is not known", errors);
        Assert.Contains("must contain both \"type\" and \"name\"", errors);
        Assert.Empty(result.Workbooks);
    }

    [Fact]
    public void A_source_folder_cannot_also_be_an_output_folder()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(temp.Combine("Data"));
        var config = new HolySheetConfig { BaseDirectory = temp.Path, Sources = { new SourceConfig { Type = SourceType.Csv, Path = "Data" } }, Output = { Data = "Data" } };
        Assert.Contains("is also an output folder", Sheets.Errors(TableBuild.Run(config)));
    }

    [Fact]
    public void Asking_for_an_unknown_csv_backend_is_a_config_error()
    {
        var config = Sheets.Config();
        config.CsvBackend = "turbo";
        Assert.Contains("Unknown CSV backend 'turbo'", Sheets.Errors(Sheets.Build(config, Sheets.Of("T", "Key<int>\nId\n\n1\n"))));
    }
}

/// <summary>The Google source against a stand-in for Google's servers. No network is used.</summary>
public class GoogleSourceTests
{
    private const string ItemCsv = "Key<int>,string\r\nId,Name\r\n,\r\n1,\"Sword, long\"\r\n2,포션\r\n";
    private const string MonsterCsv = "Key<int>,Ref<Item>\r\nId,Weapon\r\n,\r\n1,2\r\n";

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add(request.Method + " " + request.RequestUri);
            }

            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Text(string body, string mediaType = "text/csv") =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, mediaType) };

    private static readonly GoogleEndpoints Endpoints = new() { SheetsApi = "https://sheets.test", Docs = "https://docs.test", DriveApi = "https://drive.test", Wait = (_, _) => Task.CompletedTask };

    private static (BuildResult Result, StubHandler Handler) Build(SourceConfig source, Func<HttpRequestMessage, HttpResponseMessage> respond, string? baseDirectory = null, bool offline = false)
    {
        var handler = new StubHandler(respond);
        var config = new HolySheetConfig { BaseDirectory = baseDirectory ?? Path.GetTempPath(), Sources = { source }, CsvBackend = "builtin" };
        return (TableBuild.Run(config, new TableBuildOptions { HttpHandler = handler, GoogleEndpoints = Endpoints, Offline = offline }), handler);
    }

    private static HttpResponseMessage Serve(HttpRequestMessage request)
    {
        string url = request.RequestUri!.ToString();
        if (url.StartsWith("https://sheets.test/v4/spreadsheets/sheet-id?"))
        {
            return Text("""{ "properties": { "title": "Live Data" }, "sheets": [ { "properties": { "sheetId": 0, "title": "Item" } }, { "properties": { "sheetId": 777, "title": "Monster" } } ] }""", "application/json");
        }

        if (url.StartsWith("https://sheets.test/v4/spreadsheets/sheet-id/values:batchGet?"))
        {
            // The same tabs as the CSV exports below, as the Sheets API returns them: trailing blanks left out.
            return Text("""
                { "valueRanges": [
                  { "range": "Item!A1:B5", "values": [ ["Key<int>", "string"], ["Id", "Name"], [], ["1", "Sword, long"], ["2", "포션"] ] },
                  { "range": "Monster!A1:B4", "values": [ ["Key<int>", "Ref<Item>"], ["Id", "Weapon"], [], ["1", "2"] ] } ] }
                """, "application/json");
        }

        if (url == "https://docs.test/spreadsheets/d/sheet-id/export?format=csv&gid=0")
        {
            return Text(ItemCsv);
        }

        if (url == "https://docs.test/spreadsheets/d/sheet-id/export?format=csv&gid=777")
        {
            return Text(MonsterCsv);
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    [Fact]
    public void An_api_key_lists_the_tabs_and_fetches_them_all_in_one_batch_request()
    {
        var source = new SourceConfig { Type = SourceType.Google, SpreadsheetId = "sheet-id", Auth = { Type = GoogleAuthType.ApiKey, ApiKey = "KEY" } };
        (BuildResult result, StubHandler handler) = Build(source, Serve);
        Assert.True(result.Success, Sheets.Errors(result));
        Assert.Equal("Live Data", Assert.Single(result.Workbooks).Name);
        Assert.Equal(["Item", "Monster"], result.Tables.Select(table => table.Schema.Name));
        Assert.Equal("Sword, long", result.Tables[0].Rows[0].Values[1]);
        Assert.Equal("포션", result.Tables[0].Rows[1].Values[1]);
        Assert.Contains(handler.Requests, request => request.Contains("key=KEY"));
        // The Drive version check (unknown here), the tab list, and one values:batchGet for both tabs. No per-tab exports.
        Assert.Equal(3, handler.Requests.Count);
        Assert.Single(handler.Requests, request => request.Contains("values:batchGet") && request.Contains("ranges=%27Item%27") && request.Contains("ranges=%27Monster%27"));
        Assert.DoesNotContain(handler.Requests, request => request.Contains("/export?"));
    }

    [Fact]
    public void Tabs_named_in_the_config_need_no_listing_call()
    {
        var source = new SourceConfig { Type = SourceType.Google, SpreadsheetId = "sheet-id", Sheets = { new GoogleSheetRef("Item", 0) } };
        (BuildResult result, StubHandler handler) = Build(source, Serve);
        Assert.True(result.Success, Sheets.Errors(result));
        Assert.Equal(["GET https://docs.test/spreadsheets/d/sheet-id/export?format=csv&gid=0"], handler.Requests);
    }

    [Fact]
    public void A_public_spreadsheet_without_a_tab_list_is_fetched_as_one_workbook()
    {
        byte[] workbook = File.ReadAllBytes(Path.Combine(Repo.SampleData, "Sheets", "Game.xlsx"));
        var source = new SourceConfig { Type = SourceType.Google, SpreadsheetId = "sheet-id", Name = "Game" };
        (BuildResult result, StubHandler handler) = Build(source, request => request.RequestUri!.ToString().EndsWith("export?format=xlsx")
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(workbook) }
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        Assert.True(result.Success, Sheets.Errors(result));
        Assert.Equal(["Game", "Item", "Monster", "Text"], result.Tables.Select(table => table.Schema.Name));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public void A_service_account_signs_a_token_request_and_sends_the_token_with_every_call()
    {
        using var temp = new TempDir();
        using RSA rsa = RSA.Create(2048);
        string keyFile = temp.Combine("sa.json");
        File.WriteAllText(keyFile, JsonValue.Object()
            .Set("client_email", "bot@project.iam.gserviceaccount.com")
            .Set("token_uri", "https://oauth.test/token")
            .Set("private_key", rsa.ExportPkcs8PrivateKeyPem())
            .ToValue().ToJson());
        var source = new SourceConfig { Type = SourceType.Google, SpreadsheetId = "sheet-id", Auth = { Type = GoogleAuthType.ServiceAccount, KeyFile = "sa.json" } };

        string? assertion = null;
        var authorized = new List<bool>();
        (BuildResult result, _) = Build(source, request =>
        {
            if (request.RequestUri!.ToString() == "https://oauth.test/token")
            {
                string form = request.Content!.ReadAsStringAsync().Result;
                assertion = Uri.UnescapeDataString(form.Split('&').Single(part => part.StartsWith("assertion=")).Substring("assertion=".Length));
                return Text("""{ "access_token": "TOKEN-123", "expires_in": 3600 }""", "application/json");
            }

            lock (authorized)
            {
                authorized.Add(request.Headers.Authorization?.ToString() == "Bearer TOKEN-123");
            }

            return Serve(request);
        }, temp.Path);

        Assert.True(result.Success, Sheets.Errors(result));
        Assert.Equal(3, authorized.Count);
        Assert.All(authorized, Assert.True);

        // The assertion is a JWT that the account's public key verifies, with the claims Google requires.
        string[] parts = assertion!.Split('.');
        Assert.Equal(3, parts.Length);
        static byte[] FromBase64Url(string text) => Convert.FromBase64String(text.Replace('-', '+').Replace('_', '/').PadRight(text.Length + (4 - text.Length % 4) % 4, '='));
        Assert.True(rsa.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), FromBase64Url(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        JsonObject claims = JsonValue.Parse(Encoding.UTF8.GetString(FromBase64Url(parts[1]))).AsObject!;
        Assert.Equal("bot@project.iam.gserviceaccount.com", claims.GetString("iss"));
        Assert.Equal("https://oauth.test/token", claims.GetString("aud"));
        Assert.Contains("drive.readonly", claims.GetString("scope"));
        Assert.Equal(3600, claims.GetNumber("exp") - claims.GetNumber("iat"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "Google refused access", "Share the spreadsheet as 'Anyone with the link can view'")]
    [InlineData(HttpStatusCode.NotFound, "Google has no spreadsheet 'sheet-id'", "Copy the id from the URL")]
    [InlineData((HttpStatusCode)429, "rate limiting", "Wait a minute")]
    [InlineData(HttpStatusCode.InternalServerError, "status 500", "Try again later")]
    public void Failures_are_explained_with_what_to_do(HttpStatusCode status, string message, string hint)
    {
        var source = new SourceConfig { Type = SourceType.Google, SpreadsheetId = "sheet-id", Sheets = { new GoogleSheetRef("Item", 0) } };
        (BuildResult result, _) = Build(source, _ => new HttpResponseMessage(status));
        Diagnostic error = Assert.Single(result.Diagnostics.Items, item => item.Severity == Severity.Error && item.Location.StartsWith("google:"));
        Assert.Contains(message, error.Message);
        Assert.Contains(hint, error.Hint);
    }

    [Fact]
    public void A_sign_in_page_instead_of_data_is_reported_as_no_access()
    {
        var source = new SourceConfig { Type = SourceType.Google, SpreadsheetId = "sheet-id", Sheets = { new GoogleSheetRef("Item", 0) } };
        (BuildResult result, _) = Build(source, _ => Text("<html>Sign in</html>", "text/html"));
        Assert.Contains("Google refused access", Sheets.Errors(result));
    }

    [Fact]
    public void A_missing_api_key_is_explained_without_calling_google()
    {
        var source = new SourceConfig { Type = SourceType.Google, SpreadsheetId = "sheet-id", Auth = { Type = GoogleAuthType.ApiKey, ApiKeyEnv = "HOLYSHEET_TEST_NO_SUCH_VARIABLE" } };
        (BuildResult result, StubHandler handler) = Build(source, Serve);
        Assert.Contains("No API key is configured", Sheets.Errors(result));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void A_snapshot_is_saved_and_an_offline_build_reads_it_instead_of_the_network()
    {
        using var temp = new TempDir();
        var source = new SourceConfig { Type = SourceType.Google, SpreadsheetId = "sheet-id", Snapshot = "Snapshot", Auth = { Type = GoogleAuthType.ApiKey, ApiKey = "KEY" } };
        (BuildResult online, _) = Build(source, Serve, temp.Path);
        Assert.True(online.Success, Sheets.Errors(online));
        Assert.Equal(["Item.csv", "Monster.csv"], Directory.GetFiles(temp.Combine("Snapshot"), "*.csv").Select(Path.GetFileName).OrderBy(name => name));
        Assert.True(File.Exists(temp.Combine("Snapshot", SnapshotIndex.FileName)));
        // The snapshot uses LF line endings so it diffs the same on every machine.
        Assert.DoesNotContain("\r", File.ReadAllText(temp.Combine("Snapshot", "Item.csv")));

        (BuildResult offline, StubHandler handler) = Build(source, _ => throw new InvalidOperationException("offline builds must not use the network"), temp.Path, offline: true);
        Assert.True(offline.Success, Sheets.Errors(offline));
        Assert.Empty(handler.Requests);
        Assert.Equal(online.Output!.ContentHash, offline.Output!.ContentHash);
        Assert.Equal(online.Output.SchemaHash, offline.Output.SchemaHash);
    }

    [Fact]
    public void An_offline_partial_build_names_the_workbook_as_the_online_build_did()
    {
        using var temp = new TempDir();
        var source = new SourceConfig { Type = SourceType.Google, SpreadsheetId = "sheet-id", Snapshot = "Snapshot", Auth = { Type = GoogleAuthType.ApiKey, ApiKey = "KEY" } };
        var config = new HolySheetConfig { BaseDirectory = temp.Path, Sources = { source }, CsvBackend = "builtin" };
        BuildResult online = TableBuild.Run(config, new TableBuildOptions { HttpHandler = new StubHandler(Serve), GoogleEndpoints = Endpoints });
        Assert.True(online.Success, Sheets.Errors(online));
        TableBuild.Commit(config, online);
        string item = Path.Combine(config.Resolve(config.Output.Data), "Item.bytes");

        // Without a configured name the workbook is the spreadsheet's title, and the offline build must use the same one.
        var offline = new StubHandler(_ => throw new InvalidOperationException("offline builds must not use the network"));
        BuildResult partial = TableBuild.Run(config, new TableBuildOptions { HttpHandler = offline, GoogleEndpoints = Endpoints, Offline = true, Only = SheetSelection.Parse(["Live Data/Item"]) });
        Assert.True(partial.Success, Sheets.Errors(partial));
        Assert.Equal(["Live Data / Item"], partial.RefreshedSheets);
        TableBuild.Commit(config, partial);
        Assert.True(File.Exists(item));

        // A snapshot from before the name was recorded cannot be matched: the build stops instead of deleting the table.
        File.Delete(temp.Combine("Snapshot", SnapshotIndex.FileName));
        BuildResult unmatched = TableBuild.Run(config, new TableBuildOptions { HttpHandler = offline, GoogleEndpoints = Endpoints, Offline = true, Only = SheetSelection.Parse(["Live Data/Item"]) });
        Assert.False(unmatched.Success);
        Assert.Contains("The workbook was not found in any source", Sheets.Errors(unmatched));
        Assert.True(File.Exists(item));
    }
}

/// <summary>The command line, run in process. Console output is captured, so these tests must not run in parallel with each other.</summary>
[Collection("console")]
public class CliTests
{
    private static (int ExitCode, string Output, string Error) Run(string directory, params string[] args)
    {
        TextWriter previousOut = Console.Out;
        TextWriter previousError = Console.Error;
        string previousDirectory = Directory.GetCurrentDirectory();
        var output = new StringWriter();
        var error = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            Directory.SetCurrentDirectory(directory);
            int exitCode = HolySheet.Cli.Program.Main(args).GetAwaiter().GetResult();
            return (exitCode, output.ToString(), error.ToString());
        }
        finally
        {
            Directory.SetCurrentDirectory(previousDirectory);
            Console.SetOut(previousOut);
            Console.SetError(previousError);
        }
    }

    [Fact]
    public void Init_build_check_verify_and_diff_work_together()
    {
        using var temp = new TempDir();
        (int exitCode, string output, _) = Run(temp.Path, "init", "--sample");
        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(temp.Combine("holysheet.json")));

        (exitCode, output, _) = Run(temp.Path, "build");
        Assert.Equal(0, exitCode);
        Assert.Contains("built: 3 tables, 6 rows, 3 enums", output);
        Assert.True(File.Exists(temp.Combine("Generated", "Data", "tables.manifest.json")));

        (exitCode, output, _) = Run(temp.Path, "build");
        Assert.Equal(0, exitCode);
        Assert.Contains("up to date", output);

        (exitCode, output, _) = Run(temp.Path, "check");
        Assert.Equal(0, exitCode);
        Assert.Contains("csv builtin", output);
        (exitCode, output, _) = Run(temp.Path, "check", "--csv", "sep");
        Assert.Equal(0, exitCode);
        Assert.Contains("csv sep", output);

        (exitCode, output, _) = Run(temp.Path, "verify", "Generated/Data");
        Assert.Equal(0, exitCode);
        Assert.Contains("ok: 3 tables match the manifest", output);

        // Keep the first build, change a price, build again and compare the two.
        Directory.CreateDirectory(temp.Combine("v1"));
        File.Copy(temp.Combine("Generated", "Data", "tables.manifest.json"), temp.Combine("v1", "tables.manifest.json"));
        string itemPath = temp.Combine("Sheets", "Item.csv");
        File.WriteAllText(itemPath, File.ReadAllText(itemPath).Replace("Tradable,100,", "Tradable,120,"));
        Assert.Equal(0, Run(temp.Path, "build", "--label", "v2").ExitCode);

        (exitCode, output, _) = Run(temp.Path, "diff", "v1", "Generated/Data");
        Assert.Equal(0, exitCode);
        Assert.Contains("~ Item", output);
        Assert.Contains("0 added, 1 changed, 0 removed, 2 unchanged", output);
        Assert.Contains("clients can load the new data with the code they have", output);

        (exitCode, output, _) = Run(temp.Path, "diff", "v1", "Generated/Data", "--json");
        JsonObject plan = JsonValue.Parse(output).AsObject!;
        Assert.Equal(false, plan.GetBoolean("requiresNewCode"));
        Assert.Equal("Item.bytes", Assert.Single(plan.GetArray("download")!).AsObject!.GetString("file"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Diff_reports_a_code_only_release_with_no_table_downloads(bool json)
    {
        using var temp = new TempDir();
        Assert.Equal(0, Run(temp.Path, "init", "--sample").ExitCode);
        string path = temp.Combine("Sheets", "Release.Enums.csv");
        File.WriteAllText(path, "int\nERelease\n\nAlpha\n");
        Assert.Equal(0, Run(temp.Path, "build").ExitCode);
        string manifest = "Generated/Data/tables.manifest.json";
        File.Copy(temp.Combine(manifest), temp.Combine("old.json"));
        File.AppendAllText(path, "Beta\n");
        Assert.Equal(0, Run(temp.Path, "build").ExitCode);

        var oldRelease = TableManifest.Parse(File.ReadAllText(temp.Combine("old.json")));
        var newRelease = TableManifest.Parse(File.ReadAllText(temp.Combine(manifest)));
        Assert.Equal(oldRelease.Content, newRelease.Content);
        Assert.NotEqual(oldRelease.Schema, newRelease.Schema);

        var result = json ? Run(temp.Path, "diff", "old.json", manifest, "--json") : Run(temp.Path, "diff", "old.json", manifest);
        Assert.Equal(0, result.ExitCode);
        if (json)
        {
            JsonObject plan = JsonValue.Parse(result.Output).AsObject!;
            Assert.Equal(true, plan.GetBoolean("hasChanges"));
            Assert.Equal(true, plan.GetBoolean("requiresNewCode"));
            Assert.Empty(plan.GetArray("download")!);
        }
        else
        {
            Assert.Contains("clients need the new generated code", result.Output);
            Assert.Contains("0 bytes to fetch", result.Output);
        }
    }

    [Fact]
    public void Sheet_errors_exit_with_one_and_write_nothing()
    {
        using var temp = new TempDir();
        Run(temp.Path, "init", "--sample");
        File.AppendAllText(temp.Combine("Sheets", "Item.csv"), "3001,Broken,Sword,,cheap,,\n");
        (int exitCode, string output, _) = Run(temp.Path, "build");
        Assert.Equal(1, exitCode);
        Assert.Contains("error: Sheets / Item!C8", output);
        Assert.Contains("'Sword' is not a member of EItemKind", output);
        Assert.Contains("error: Sheets / Item!E8", output);
        Assert.Contains("Nothing was written", output);
        Assert.False(Directory.Exists(temp.Combine("Generated", "Data")));

        (exitCode, output, _) = Run(temp.Path, "build", "--json");
        Assert.Equal(1, exitCode);
        JsonObject document = JsonValue.Parse(output).AsObject!;
        Assert.Equal(false, document.GetBoolean("success"));
        JsonObject first = document.GetArray("diagnostics")![0].AsObject!;
        Assert.Equal("Item", first.GetString("sheet"));
        Assert.Equal("C8", first.GetString("cell"));
    }

    [Fact]
    public void Keygen_signs_later_builds_and_verify_notices_tampering()
    {
        using var temp = new TempDir();
        Run(temp.Path, "init", "--sample");
        (int exitCode, string output, _) = Run(temp.Path, "keygen", "--write-config");
        Assert.Equal(0, exitCode);
        Assert.Contains("public key:  rsa:", output);
        Assert.True(File.Exists(temp.Combine("holysheet.private.key")));
        Assert.Equal(3, Run(temp.Path, "keygen").ExitCode);      // refuses to replace a key without --force

        Assert.Equal(0, Run(temp.Path, "build").ExitCode);
        Assert.Contains("\"signature\": { \"algorithm\": \"rsa-sha256\", \"value\": ", File.ReadAllText(temp.Combine("Generated", "Data", "tables.manifest.json")));
        (exitCode, output, _) = Run(temp.Path, "verify", "Generated/Data");
        Assert.Equal(0, exitCode);
        Assert.Contains("signature: valid", output);

        string dataPath = temp.Combine("Generated", "Data", "Item.bytes");
        byte[] bytes = File.ReadAllBytes(dataPath);
        bytes[10] ^= 0xFF;
        File.WriteAllBytes(dataPath, bytes);
        (exitCode, output, _) = Run(temp.Path, "verify", "Generated/Data");
        Assert.Equal(1, exitCode);
        Assert.Contains("FAILED Item.bytes", output);
    }

    [Theory]
    [InlineData(2, "frobnicate")]
    [InlineData(2, "build", "--no-such-option")]
    [InlineData(2, "build", "--config")]
    [InlineData(2, "diff", "only-one")]
    [InlineData(3, "build", "--config", "missing.json")]
    [InlineData(0, "help")]
    [InlineData(0, "--help")]
    [InlineData(0, "version")]
    [InlineData(0, "info")]
    [InlineData(0, "info", "--json")]
    public void Exit_codes_tell_usage_errors_from_tool_failures(int expected, params string[] args)
    {
        using var temp = new TempDir();
        Assert.Equal(expected, Run(temp.Path, args).ExitCode);
    }

    [Fact]
    public void Init_recognizes_a_unity_project_and_does_not_overwrite_a_config()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(temp.Combine("Assets"));
        Directory.CreateDirectory(temp.Combine("ProjectSettings"));
        Assert.Equal(0, Run(temp.Path, "init").ExitCode);
        HolySheetConfig config = HolySheetConfig.Load(temp.Combine("holysheet.json"));
        Assert.Equal("Assets/StreamingAssets/Tables", config.Output.Data);
        Assert.Equal(3, Run(temp.Path, "init").ExitCode);
        Assert.Equal(0, Run(temp.Path, "init", "--force").ExitCode);
    }

    [Fact]
    public void Init_sample_checks_every_destination_before_writing()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(temp.Combine("Sheets"));
        string item = temp.Combine("Sheets", "Item.csv");
        byte[] original = Encoding.UTF8.GetBytes("Id,Name\r\n42,My Item\r\n");
        File.WriteAllBytes(item, original);

        var result = Run(temp.Path, "init", "--sample");

        Assert.Equal(3, result.ExitCode);
        Assert.Contains("Item.csv", result.Error);
        Assert.Equal(original, File.ReadAllBytes(item));
        Assert.Equal([item], Directory.GetFiles(temp.Path, "*", SearchOption.AllDirectories));

        Assert.Equal(0, Run(temp.Path, "init").ExitCode);
        Assert.Equal(original, File.ReadAllBytes(item));
        Assert.Equal(0, Run(temp.Path, "init", "--sample", "--force").ExitCode);
        Assert.Contains("Wooden Sword", File.ReadAllText(item));
    }

    [Theory]
    [InlineData("Data", "Item.bytes")]
    [InlineData("Code", "ItemRow.g.cs")]
    [InlineData("Data", "tables.manifest.json")]
    [InlineData("Data", "tables.schema.json")]
    [InlineData("Csv", "Item.csv")]
    [InlineData("Csv", OutputCommitter.CsvListFileName)]
    public void First_build_checks_every_destination_before_writing(string folder, string name)
    {
        using var temp = new TempDir();
        Assert.Equal(0, Run(temp.Path, "init", "--sample").ExitCode);
        string path = temp.Combine("holysheet.json");
        HolySheetConfig config = HolySheetConfig.Load(path);
        config.Output.Csv = "Generated/Csv";
        config.Save(path);
        Directory.CreateDirectory(temp.Combine("Generated", folder));
        string existing = temp.Combine("Generated", folder, name);
        byte[] original = Encoding.UTF8.GetBytes("""{"tables":[{"file":"Notes.bytes"}]}""");
        File.WriteAllBytes(existing, original);

        var result = Run(temp.Path, "build");

        Assert.Equal(3, result.ExitCode);
        Assert.Contains(name, result.Error);
        Assert.Equal(original, File.ReadAllBytes(existing));
        Assert.Equal([existing], Directory.GetFiles(temp.Combine("Generated"), "*", SearchOption.AllDirectories));
    }
}

[CollectionDefinition("console", DisableParallelization = true)]
public class ConsoleCollection;
