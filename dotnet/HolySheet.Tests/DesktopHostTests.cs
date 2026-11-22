using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using HolySheet.Desktop.Host;

namespace HolySheet.Tests;

/// <summary>The desktop app's engine process, driven in memory the way the Tauri shell drives it over stdio.</summary>
public sealed class DesktopHostTests : IDisposable
{
    private readonly TempDir _temp = new();
    private readonly StringWriter _wire = new();
    private readonly RpcServer _server;
    private readonly ConcurrentQueue<JsonObject> _notifications = new();

    public DesktopHostTests()
    {
        _server = new RpcServer(new StringReader(""), new NotificationWriter(_wire, _notifications));
    }

    public void Dispose()
    {
        _temp.Dispose();
    }

    private async Task<JsonObject> Call(string method, JsonObject? parameters = null) =>
        (JsonObject)(await _server.DispatchAsync(method, parameters ?? new JsonObject()))!;

    private Task<JsonObject> Run(bool commit, params string[] only) =>
        Call("run", new JsonObject { ["commit"] = commit, ["only"] = new JsonArray(only.Select(item => (JsonNode?)item).ToArray()) });

    private async Task<JsonObject> InitSample()
    {
        JsonObject state = await Call("project.init", new JsonObject { ["dir"] = _temp.Path, ["sample"] = true });
        Assert.True(state["hasConfig"]!.GetValue<bool>());
        return state;
    }

    // The sample workbook copied to a temp folder. Its config writes code outside its folder, so it is only checked here.
    private async Task OpenGameData()
    {
        CopyDirectory(Repo.SampleData, _temp.Path);
        await Call("project.open", new JsonObject { ["path"] = _temp.Combine("holysheet.memorypack.json") });
        JsonObject result = await Run(commit: false);
        Assert.Equal("checked", result["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_new_project_checks_builds_and_is_then_up_to_date()
    {
        await InitSample();

        JsonObject check = await Run(commit: false);
        Assert.Equal("checked", check["status"]!.GetValue<string>());
        Assert.Equal(3, check["tables"]!.GetValue<int>());
        Assert.False(Directory.Exists(_temp.Combine("Generated")));

        JsonObject build = await Run(commit: true);
        Assert.Equal("built", build["status"]!.GetValue<string>());
        Assert.NotEmpty(build["written"]!.AsArray());
        Assert.StartsWith("Built 3 tables", build["summary"]!.GetValue<string>());

        JsonObject again = await Run(commit: true);
        Assert.Equal("upToDate", again["status"]!.GetValue<string>());

        // The toolbar follows each run from start to finish.
        string[] methods = _notifications.Select(message => message["method"]!.GetValue<string>()).ToArray();
        Assert.Equal(["run.started", "run.finished", "run.started", "run.finished", "run.started", "run.finished"], methods);
    }

    [Fact]
    public async Task Opening_a_project_can_preview_offline_only_when_every_google_source_has_a_local_snapshot()
    {
        JsonObject state = await InitSample();
        Assert.True(state["canPreviewOffline"]!.GetValue<bool>());
        var config = state["config"]!.AsObject();
        config["sources"]!.AsArray().Add(new JsonObject { ["type"] = "google", ["spreadsheetId"] = "example", ["snapshot"] = "local-copy" });
        await Call("config.save", new JsonObject { ["config"] = config.DeepClone() });
        Assert.False((await Call("project.state"))["canPreviewOffline"]!.GetValue<bool>());
        Directory.CreateDirectory(_temp.Combine("local-copy"));
        Assert.True((await Call("project.state"))["canPreviewOffline"]!.GetValue<bool>());
        config["sources"]!.AsArray().Add(new JsonObject { ["type"] = "googleFolder", ["folderId"] = "example" });
        await Call("config.save", new JsonObject { ["config"] = config.DeepClone() });
        Assert.False((await Call("project.state"))["canPreviewOffline"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Sheet_errors_are_reported_with_their_cell_and_nothing_is_written()
    {
        await InitSample();
        File.AppendAllText(_temp.Combine("Sheets", "Item.csv"), "3001,Broken,Weapon,,not a number,,\n");

        JsonObject result = await Run(commit: true);

        Assert.Equal("errors", result["status"]!.GetValue<string>());
        JsonObject problem = result["diagnostics"]!.AsArray().Select(item => item!.AsObject()).First(item => item["severity"]!.GetValue<string>() == "error");
        Assert.Equal("Item", problem["sheet"]!.GetValue<string>());
        Assert.Equal("E8", problem["cell"]!.GetValue<string>());
        Assert.Empty(result["written"]!.AsArray());
        Assert.False(Directory.Exists(_temp.Combine("Generated")));
    }

    [Fact]
    public async Task A_second_run_is_refused_while_one_is_going()
    {
        await InitSample();
        Task<JsonObject> first = Run(commit: false);

        var refused = await Assert.ThrowsAsync<RpcException>(() => Run(commit: false));

        Assert.Equal(RpcException.Busy, refused.Code);
        Assert.Equal("checked", (await first)["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_model_lists_tables_with_their_sheets_and_enums_by_where_they_are_defined()
    {
        await OpenGameData();

        JsonObject model = await Call("tables.model");

        JsonObject item = model["tables"]!.AsArray().Select(node => node!.AsObject()).Single(table => table["name"]!.GetValue<string>() == "Item");
        var roles = item["sheets"]!.AsArray().Select(node => node!["sheet"]!.GetValue<string>() + ":" + node["role"]!.GetValue<string>()).ToArray();
        Assert.Equal(["Item:defines", "Item.Col.Balance:columns", "Item.Rare:rows"], roles);
        Assert.Contains(model["workbooks"]!.AsArray(), node => node!["name"]!.GetValue<string>() == "Game.xlsx" && node["type"]!.GetValue<string>() == "xlsx");
        JsonObject monsterIds = model["enums"]!.AsArray().Select(node => node!.AsObject()).Single(definition => definition["name"]!.GetValue<string>() == "EMonsterId");
        Assert.True(monsterIds["mapping"]!.GetValue<bool>());
        JsonObject tags = model["enums"]!.AsArray().Select(node => node!.AsObject()).Single(definition => definition["name"]!.GetValue<string>() == "EItemTag");
        Assert.False(tags["mapping"]!.GetValue<bool>());
        Assert.Contains(tags["members"]!.AsArray(), member => member!["display"]!.GetValue<string>().EndsWith("(1 << 1)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_preview_shows_a_table_or_the_part_one_sheet_added()
    {
        await OpenGameData();

        JsonObject whole = await Call("tables.preview", new JsonObject { ["table"] = "Item" });
        JsonArray rows = whole["rows"]!.AsArray();
        // Rows from Item.Rare carry the sheet's short name, so "Rare 4" is not read as row 4 of Item.
        Assert.Contains(rows, row => row!["label"]!.GetValue<string>().StartsWith("Rare ", StringComparison.Ordinal));
        Assert.Equal(0, whole["keyColumn"]!.GetValue<int>());
        JsonObject id = whole["columns"]![0]!.AsObject();
        Assert.Equal(("Id", "int", "number", "A"), (id["name"]!.GetValue<string>(), id["typeClass"]!.GetValue<string>(), id["sortKind"]!.GetValue<string>(), id["letter"]!.GetValue<string>()));

        JsonObject rare = await Call("tables.preview", new JsonObject { ["table"] = "Item", ["sheet"] = "Item.Rare" });
        Assert.All(rare["rows"]!.AsArray(), row => Assert.Equal("Item.Rare", row!["sheet"]!.GetValue<string>()));
        Assert.All(rare["rows"]!.AsArray(), row => Assert.True(int.TryParse(row!["label"]!.GetValue<string>(), out _)));

        // A column extension shows its own columns, plus the key they are matched by.
        JsonObject balance = await Call("tables.preview", new JsonObject { ["table"] = "Item", ["sheet"] = "Item.Col.Balance" });
        JsonArray columns = balance["columns"]!.AsArray();
        Assert.Equal("Id", columns[0]!["name"]!.GetValue<string>());
        Assert.True(columns.Count < whole["columns"]!.AsArray().Count);
        Assert.All(columns.Skip(1), column => Assert.Equal("Item.Col.Balance", column!["sheet"]!.GetValue<string>()));
    }

    [Fact]
    public async Task A_partial_run_reads_only_the_selected_sheet_again()
    {
        await InitSample();
        await Run(commit: true);

        JsonObject partial = await Run(commit: true, "Sheets/Item");

        Assert.True(partial["partial"]!.GetValue<bool>());
        Assert.Equal(["Sheets / Item"], partial["refreshed"]!.AsArray().Select(node => node!.GetValue<string>()));
    }

    [Fact]
    public async Task Saving_the_config_goes_through_the_engine_parser()
    {
        JsonObject state = await InitSample();
        JsonObject config = state["config"]!.DeepClone().AsObject();
        config["codegen"]!["namespace"] = "My.Game";
        config["build"] = new JsonObject { ["label"] = null, ["parallel"] = false };

        JsonObject saved = await Call("config.save", new JsonObject { ["config"] = config });

        Assert.Equal("My.Game", saved["config"]!["codegen"]!["namespace"]!.GetValue<string>());
        Assert.Contains("\"My.Game\"", File.ReadAllText(_temp.Combine("holysheet.json")));
        Assert.True(saved["sources"]![0]!["exists"]!.GetValue<bool>());

        // What the engine would refuse is refused here, and the file stays as it was.
        config["sources"]![0]!["type"] = "nonsense";
        string before = File.ReadAllText(_temp.Combine("holysheet.json"));
        await Assert.ThrowsAnyAsync<Exception>(() => Call("config.save", new JsonObject { ["config"] = config }));
        Assert.Equal(before, File.ReadAllText(_temp.Combine("holysheet.json")));
    }

    [Fact]
    public async Task A_config_that_does_not_parse_is_reported_instead_of_opened()
    {
        File.WriteAllText(_temp.Combine("holysheet.json"), "{ \"sources\": [ }");

        JsonObject state = await Call("project.open", new JsonObject { ["path"] = _temp.Path });

        Assert.True(state["hasConfig"]!.GetValue<bool>());
        Assert.Null(state["config"]);
        Assert.Contains("holysheet.json", state["configError"]!.GetValue<string>());
    }

    [Fact]
    public async Task Signing_creates_a_key_once_and_the_built_data_verifies()
    {
        await InitSample();

        JsonObject signed = await Call("signing.createKeyPair");
        Assert.True(signed["keyFileExists"]!.GetValue<bool>());
        Assert.False(signed["keyIgnored"]!.GetValue<bool>());
        Assert.False(string.IsNullOrEmpty(signed["config"]!["signing"]!["publicKey"]!.GetValue<string>()));
        await Assert.ThrowsAsync<IOException>(() => Call("signing.createKeyPair"));

        Assert.True((await Call("signing.ignoreKey"))["keyIgnored"]!.GetValue<bool>());

        Assert.Equal("built", (await Run(commit: true))["status"]!.GetValue<string>());
        JsonObject report = await Call("signing.verify");
        Assert.True(report["success"]!.GetValue<bool>(), report.ToJsonString());
        Assert.Equal("valid", report["signature"]!.GetValue<string>());

        string data = report["directory"]!.GetValue<string>();
        File.WriteAllBytes(Path.Combine(data, "Item.bytes"), [1, 2, 3]);
        JsonObject tampered = await Call("signing.verify");
        Assert.False(tampered["success"]!.GetValue<bool>());
        Assert.Contains(tampered["problems"]!.AsArray(), problem => problem!.GetValue<string>().StartsWith("Item.bytes", StringComparison.Ordinal));

        JsonObject stopped = await Call("signing.stop");
        Assert.Null(stopped["config"]!["signing"]);
    }

    [Fact]
    public async Task Sources_and_problems_open_at_their_file()
    {
        await InitSample();
        await Run(commit: false);

        JsonObject sheet = await Call("source.locate", new JsonObject { ["workbook"] = "Sheets", ["sheet"] = "Item" });
        Assert.Equal(_temp.Combine("Sheets", "Item.csv"), sheet["target"]!.GetValue<string>());

        JsonObject cell = await Call("problem.locate", new JsonObject { ["location"] = "Sheets / Monster!C4" });
        Assert.Equal(_temp.Combine("Sheets", "Monster.csv"), cell["target"]!.GetValue<string>());

        JsonObject config = await Call("problem.locate", new JsonObject { ["location"] = "config" });
        Assert.Equal(_temp.Combine("holysheet.json"), config["target"]!.GetValue<string>());

        Assert.Equal("Sheets/Item.csv", (await Call("path.toProject", new JsonObject { ["path"] = _temp.Combine("Sheets", "Item.csv") }))["path"]!.GetValue<string>());
    }

    [Fact]
    public async Task Refresh_reads_a_saved_sheet_without_writing_build_output()
    {
        await InitSample();
        JsonObject previous = await Run(commit: false);

        File.AppendAllText(_temp.Combine("Sheets", "Item.csv"), "3001,Shield,Armor,,70,,\n");

        JsonObject refreshed = await Run(commit: false);
        Assert.Equal("checked", refreshed["status"]!.GetValue<string>());
        Assert.Equal(previous["rows"]!.GetValue<int>() + 1, refreshed["rows"]!.GetValue<int>());
        Assert.False(Directory.Exists(_temp.Combine("Generated")));
    }

    [Fact]
    public async Task The_wire_carries_one_message_per_line_with_the_request_id()
    {
        var output = new StringWriter();
        string requests = "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"app.info\"}\n"
            + "{\"jsonrpc\":\"2.0\",\"id\":\"x\",\"method\":\"no.such\"}\n"
            + "not json\n";
        var server = new RpcServer(new StringReader(requests), output);

        await server.RunAsync();

        JsonObject[] messages = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)!.AsObject()).ToArray();
        Assert.Equal(3, messages.Length);
        JsonObject info = messages.Single(message => message["id"]?.ToJsonString() == "7");
        Assert.Equal("holysheet/2.0.0", info["result"]!["tool"]!.GetValue<string>());
        JsonObject unknown = messages.Single(message => message["id"]?.ToJsonString() == "\"x\"");
        Assert.Equal(RpcException.MethodNotFound, unknown["error"]!["code"]!.GetValue<int>());
        JsonObject garbage = messages.Single(message => message["id"] == null);
        Assert.Equal(RpcException.ParseError, garbage["error"]!["code"]!.GetValue<int>());
    }

    private static void CopyDirectory(string from, string to)
    {
        foreach (string directory in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, directory)));
        }

        foreach (string file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), overwrite: true);
        }
    }

    /// <summary>Collects the notifications the server writes, so a test can wait for one.</summary>
    private sealed class NotificationWriter(TextWriter inner, ConcurrentQueue<JsonObject> notifications) : TextWriter
    {
        public override System.Text.Encoding Encoding => inner.Encoding;

        public override void Write(char value) => inner.Write(value);

        public override void WriteLine(string? value)
        {
            inner.WriteLine(value);
            if (value != null && JsonNode.Parse(value) is JsonObject message && message["method"] != null)
            {
                notifications.Enqueue(message);
            }
        }
    }
}
