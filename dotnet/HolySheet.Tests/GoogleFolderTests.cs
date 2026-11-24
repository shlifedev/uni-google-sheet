using System.Net;
using System.Text;
using HolySheet.Core;
using HolySheet.Core.Config;
using HolySheet.Core.Sources;
using Xunit;

namespace HolySheet.Tests;

/// <summary>The Google Drive folder source against a stand-in for Google's servers. No network is used.</summary>
public class GoogleFolderTests
{
    private static readonly GoogleEndpoints Endpoints = new() { SheetsApi = "https://sheets.test", Docs = "https://docs.test", DriveApi = "https://drive.test" };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add(Uri.UnescapeDataString(request.RequestUri!.ToString()));
            }

            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Text(string body, string mediaType = "text/csv") =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, mediaType) };

    private static SourceConfig Folder(string? snapshot = null) => new()
    {
        Type = SourceType.GoogleFolder, FolderId = "folder-id", Snapshot = snapshot, Auth = { Type = GoogleAuthType.ApiKey, ApiKey = "KEY" }
    };

    // A folder with a spreadsheet (Quests: tabs Quest and Reward), an uploaded workbook (Game.xlsx) and a draft (#Draft),
    // listed over two pages, as the Drive API does for big folders.
    private static Func<HttpRequestMessage, HttpResponseMessage> Drive(bool removeWorkbook = false) => request =>
    {
        string url = Uri.UnescapeDataString(request.RequestUri!.ToString());
        if (url.StartsWith("https://drive.test/drive/v3/files/folder-id?", StringComparison.Ordinal))
        {
            return Text("""{ "id": "folder-id", "name": "Game Data", "mimeType": "application/vnd.google-apps.folder" }""", "application/json");
        }

        if (url.StartsWith("https://drive.test/drive/v3/files?", StringComparison.Ordinal))
        {
            Assert.Contains("'folder-id' in parents", url);
            Assert.Contains("key=KEY", url);
            if (!url.Contains("pageToken=page-2"))
            {
                return Text("""{ "nextPageToken": "page-2", "files": [ { "id": "sheet-1", "name": "Quests", "mimeType": "application/vnd.google-apps.spreadsheet" }, { "id": "draft-1", "name": "#Draft", "mimeType": "application/vnd.google-apps.spreadsheet" } ] }""", "application/json");
            }

            return Text(removeWorkbook ? """{ "files": [] }""" : """{ "files": [ { "id": "xlsx-1", "name": "Game.xlsx", "mimeType": "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" } ] }""", "application/json");
        }

        if (url.StartsWith("https://sheets.test/v4/spreadsheets/sheet-1?", StringComparison.Ordinal))
        {
            return Text("""{ "properties": { "title": "Quests" }, "sheets": [ { "properties": { "sheetId": 0, "title": "Quest" } }, { "properties": { "sheetId": 5, "title": "Reward" } } ] }""", "application/json");
        }

        if (url.StartsWith("https://sheets.test/v4/spreadsheets/sheet-1/values:batchGet?", StringComparison.Ordinal))
        {
            // Only the tabs asked for, in the order asked: a partial build asks for fewer.
            var ranges = new List<string>();
            if (url.Contains("ranges='Quest'"))
            {
                ranges.Add("""{ "range": "Quest!A1:B5", "values": [ ["Key<int>", "string"], ["Id", "Name"], [], ["1", "First steps"], ["2", "Goblins, again"] ] }""");
            }

            if (url.Contains("ranges='Reward'"))
            {
                ranges.Add("""{ "range": "Reward!A1:B4", "values": [ ["Key<int>", "Ref<Quest>"], ["Id", "Quest"], [], ["1", "2"] ] }""");
            }

            return Text("{ \"valueRanges\": [" + string.Join(",", ranges) + "] }", "application/json");
        }

        if (url.StartsWith("https://drive.test/drive/v3/files/xlsx-1?alt=media", StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(File.ReadAllBytes(Repo.Fixture("Game.libreoffice.xlsx"))) };
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    };

    private static (BuildResult Result, StubHandler Handler) Build(SourceConfig source, Func<HttpRequestMessage, HttpResponseMessage> respond, string? baseDirectory = null, bool offline = false, SheetSelection? only = null, HolySheetConfig? config = null)
    {
        var handler = new StubHandler(respond);
        config ??= new HolySheetConfig { BaseDirectory = baseDirectory ?? Path.GetTempPath(), Sources = { source }, CsvBackend = "builtin" };
        return (TableBuild.Run(config, new TableBuildOptions { HttpHandler = handler, GoogleEndpoints = Endpoints, Offline = offline, Only = only }), handler);
    }

    [Fact]
    public void Every_spreadsheet_and_xlsx_file_in_the_folder_becomes_a_workbook()
    {
        (BuildResult result, StubHandler handler) = Build(Folder(), Drive());
        Assert.True(result.Success, Sheets.Errors(result));
        Assert.Equal(["Game.xlsx", "Quests"], result.Workbooks.Select(workbook => workbook.Name));
        // Each file keeps its own address, so the window can open it.
        Assert.Equal(new WorkbookOrigin(SourceType.GoogleFolder, "folder-id", "https://docs.google.com/spreadsheets/d/sheet-1/edit"), result.WorkbookOrigins["Quests"]);
        Assert.Equal("https://drive.google.com/file/d/xlsx-1/view", result.WorkbookOrigins["Game.xlsx"].Link);
        Assert.Contains(result.Tables, table => table.Schema.Name == "Quest");
        Assert.Contains(result.Tables, table => table.Schema.Name == "Reward");
        Assert.Contains(result.Tables, table => table.Schema.Name == "Item");
        Assert.Equal("Goblins, again", result.Tables.Single(table => table.Schema.Name == "Quest").Rows[1].Values[1]);
        // The draft is not even downloaded.
        Assert.DoesNotContain(handler.Requests, request => request.Contains("draft-1"));
        Assert.Equal(2, handler.Requests.Count(request => request.StartsWith("https://drive.test/drive/v3/files?", StringComparison.Ordinal)));
    }

    [Fact]
    public void Two_files_with_the_same_name_are_an_error()
    {
        (BuildResult result, _) = Build(Folder(), request =>
            request.RequestUri!.AbsolutePath == "/drive/v3/files/folder-id"
                ? Text("""{ "id": "folder-id", "name": "Game Data", "mimeType": "application/vnd.google-apps.folder" }""", "application/json")
                : request.RequestUri!.AbsolutePath == "/drive/v3/files"
                ? Text("""{ "files": [ { "id": "a", "name": "Quests", "mimeType": "application/vnd.google-apps.spreadsheet" }, { "id": "b", "name": "Quests", "mimeType": "application/vnd.google-apps.spreadsheet" } ] }""", "application/json")
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        Assert.False(result.Success);
        Assert.Contains("Two files in the folder are named 'Quests' (ids a and b)", Sheets.Errors(result));
    }

    [Fact]
    public void A_folder_without_credentials_is_a_config_error()
    {
        var source = Folder();
        source.Auth.Type = GoogleAuthType.Public;
        (BuildResult result, StubHandler handler) = Build(source, _ => throw new InvalidOperationException("nothing should be requested"));
        Assert.False(result.Success);
        Assert.Contains("cannot be listed without credentials", Sheets.Errors(result));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void A_refused_folder_says_what_to_share_with_whom()
    {
        var source = Folder();
        source.Auth = new GoogleAuthConfig { Type = GoogleAuthType.AccessToken, AccessTokenEnv = "HOLYSHEET_TEST_FOLDER_TOKEN" };
        Environment.SetEnvironmentVariable("HOLYSHEET_TEST_FOLDER_TOKEN", "token");
        try
        {
            (BuildResult result, _) = Build(source, _ => new HttpResponseMessage(HttpStatusCode.Forbidden));
            Assert.Contains("Google refused access to Drive folder 'folder-id' (403)", Sheets.Errors(result));
            Assert.Contains("drive.readonly", Sheets.Errors(result));
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOLYSHEET_TEST_FOLDER_TOKEN", null);
        }
    }

    [Fact]
    public void A_folder_that_is_not_shared_names_the_account_to_share_it_with()
    {
        using var temp = new TempDir();
        // A key file is needed for the e-mail; the token request itself is answered by the stub below.
        File.WriteAllText(temp.Combine("sa.json"), "{ \"client_email\": \"reader@project.iam.gserviceaccount.com\" }");
        var source = Folder();
        source.Auth = new GoogleAuthConfig { Type = GoogleAuthType.ServiceAccount, KeyFile = "sa.json" };
        var helper = new GoogleSheetsSource(source, temp.Path, HolySheet.Core.Csv.BuiltinCsvParser.Instance);
        Assert.Equal("reader@project.iam.gserviceaccount.com", helper.ServiceAccountEmail());

        // Drive answers 404 for a folder the caller cannot see (and an empty list for its children).
        source.Auth = new GoogleAuthConfig { Type = GoogleAuthType.ApiKey, ApiKey = "KEY" };
        (BuildResult result, StubHandler handler) = Build(source, _ => new HttpResponseMessage(HttpStatusCode.NotFound), temp.Path);
        Assert.False(result.Success);
        Assert.Contains("that these credentials can see (404)", Sheets.Errors(result));
        Assert.Contains("Anyone with the link", Sheets.Errors(result));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public void An_empty_folder_is_a_warning_and_a_spreadsheet_id_is_not_a_folder()
    {
        (BuildResult empty, _) = Build(Folder(), request => request.RequestUri!.AbsolutePath == "/drive/v3/files/folder-id"
            ? Text("""{ "id": "folder-id", "name": "Game Data", "mimeType": "application/vnd.google-apps.folder" }""", "application/json")
            : Text("""{ "files": [] }""", "application/json"));
        Assert.Contains(empty.Diagnostics.Items, item => item.Message.Contains("The Drive folder 'Game Data' has no Google spreadsheet or .xlsx file"));

        (BuildResult sheet, _) = Build(Folder(), _ => Text("""{ "id": "folder-id", "name": "Quests", "mimeType": "application/vnd.google-apps.spreadsheet" }""", "application/json"));
        Assert.Contains("is a file (Quests), not a folder", Sheets.Errors(sheet));
        Assert.Contains("Google Sheets source", Sheets.Errors(sheet));
    }

    private const string SheetsDisabled = """
        { "error": { "code": 403, "message": "Google Sheets API has not been used in project 42 before or it is disabled.", "status": "PERMISSION_DENIED",
          "details": [ { "@type": "type.googleapis.com/google.rpc.ErrorInfo", "reason": "SERVICE_DISABLED", "domain": "googleapis.com",
            "metadata": { "service": "sheets.googleapis.com", "serviceTitle": "Google Sheets API", "consumer": "projects/42",
                          "activationUrl": "https://console.developers.google.com/apis/api/sheets.googleapis.com/overview?project=42" } } ] } }
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Without_the_sheets_api_a_spreadsheet_is_read_as_one_xlsx_export_and_the_fix_is_named(bool selectTabs)
    {
        var source = new SourceConfig { Type = SourceType.Google, SpreadsheetId = "sheet-1", Auth = { Type = GoogleAuthType.ApiKey, ApiKey = "KEY" } };
        if (selectTabs) source.Sheets.Add(new GoogleSheetRef("Text", 5));
        (BuildResult result, StubHandler handler) = Build(source, request =>
        {
            string url = request.RequestUri!.ToString();
            if (url.StartsWith("https://sheets.test/", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent(SheetsDisabled, Encoding.UTF8, "application/json") };
            }

            return url.StartsWith("https://docs.test/spreadsheets/d/sheet-1/export?format=xlsx", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(File.ReadAllBytes(Repo.Fixture("Game.libreoffice.xlsx"))) }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        Assert.True(result.Success, Sheets.Errors(result));
        if (selectTabs) Assert.Equal(["Text"], Assert.Single(result.Workbooks).Sheets.Select(sheet => sheet.Name));
        else Assert.Contains(result.Tables, table => table.Schema.Name == "Item");
        Diagnostic warning = Assert.Single(result.Diagnostics.Items, item => item.Severity == Severity.Warning);
        Assert.Contains("The Google Sheets API is not enabled in Google Cloud project 42", warning.Message);
        Assert.Contains("https://console.developers.google.com/apis/api/sheets.googleapis.com/overview?project=42", warning.Hint);
        // The Drive version check, the refused tab list, and the xlsx export.
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public void A_disabled_drive_api_is_reported_as_such_not_as_a_sharing_problem()
    {
        string driveDisabled = SheetsDisabled.Replace("Google Sheets API", "Google Drive API").Replace("sheets.googleapis.com", "drive.googleapis.com");
        (BuildResult result, _) = Build(Folder(), _ => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent(driveDisabled, Encoding.UTF8, "application/json") });
        Assert.False(result.Success);
        Assert.Contains("The Google Drive API is not enabled in Google Cloud project 42", Sheets.Errors(result));
        Assert.Contains("drive.googleapis.com/overview?project=42", Sheets.Errors(result));
        Assert.DoesNotContain("Share the folder", Sheets.Errors(result));
    }

    [Fact]
    public void A_partial_build_downloads_only_the_selected_file_and_tab()
    {
        using var temp = new TempDir();
        var config = new HolySheetConfig { BaseDirectory = temp.Path, Sources = { Folder() }, CsvBackend = "builtin" };
        (BuildResult full, _) = Build(config.Sources[0], Drive(), config: config);
        Assert.True(full.Success, Sheets.Errors(full));
        TableBuild.Commit(config, full);

        (BuildResult partial, StubHandler handler) = Build(config.Sources[0], Drive(), only: SheetSelection.Parse(["Quests/Reward"]), config: config);
        Assert.True(partial.Success, Sheets.Errors(partial));
        Assert.Equal(["Quests / Reward"], partial.RefreshedSheets);
        Assert.DoesNotContain(handler.Requests, request => request.Contains("xlsx-1"));
        string batch = Assert.Single(handler.Requests, request => request.Contains("values:batchGet"));
        Assert.Contains("ranges='Reward'", batch);
        Assert.DoesNotContain("ranges='Quest'", batch);
        Assert.Equal(full.Output!.ContentHash, partial.Output!.ContentHash);
    }

    [Fact]
    public void The_snapshot_keeps_one_folder_per_workbook_and_an_offline_build_reads_it()
    {
        using var temp = new TempDir();
        (BuildResult online, _) = Build(Folder("Snapshot"), Drive(), temp.Path);
        Assert.True(online.Success, Sheets.Errors(online));
        Assert.True(File.Exists(temp.Combine("Snapshot", "Quests", "Reward.csv")));
        Assert.True(File.Exists(temp.Combine("Snapshot", "Game.xlsx", "Item.csv")));
        Assert.Contains("Game.xlsx\tGame.xlsx", File.ReadAllText(temp.Combine("Snapshot", GoogleFolderSource.IndexFileName)));

        (BuildResult offline, StubHandler handler) = Build(Folder("Snapshot"), _ => throw new InvalidOperationException("offline builds must not use the network"), temp.Path, offline: true);
        Assert.True(offline.Success, Sheets.Errors(offline));
        Assert.Empty(handler.Requests);
        Assert.Equal(online.Output!.ContentHash, offline.Output!.ContentHash);
        Assert.Equal(["Game.xlsx", "Quests"], offline.Workbooks.Select(workbook => workbook.Name));
        Assert.Equal("https://docs.google.com/spreadsheets/d/sheet-1/edit", offline.WorkbookOrigins["Quests"].Link);

        // A file that leaves the folder leaves the snapshot at the next online build.
        (BuildResult after, _) = Build(Folder("Snapshot"), Drive(removeWorkbook: true), temp.Path);
        Assert.True(after.Success, Sheets.Errors(after));
        Assert.False(Directory.Exists(temp.Combine("Snapshot", "Game.xlsx")));
        Assert.DoesNotContain("Game.xlsx", File.ReadAllText(temp.Combine("Snapshot", GoogleFolderSource.IndexFileName)));
    }

    [Fact]
    public void The_snapshot_leaves_folders_it_did_not_make_alone()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(temp.Combine("Snapshot", "Art"));
        File.WriteAllText(temp.Combine("Snapshot", "Art", "keep.txt"), "not a workbook");

        (BuildResult online, _) = Build(Folder("Snapshot"), Drive(), temp.Path);
        Assert.True(online.Success, Sheets.Errors(online));
        (BuildResult again, _) = Build(Folder("Snapshot"), Drive(removeWorkbook: true), temp.Path);
        Assert.True(again.Success, Sheets.Errors(again));

        Assert.False(Directory.Exists(temp.Combine("Snapshot", "Game.xlsx")));
        Assert.True(File.Exists(temp.Combine("Snapshot", "Art", "keep.txt")));
    }

    [Fact]
    public void The_config_keeps_a_folder_source_and_takes_a_pasted_url()
    {
        HolySheetConfig parsed = HolySheetConfig.Parse("""{ "sources": [ { "type": "googleFolder", "folderId": "https://drive.google.com/drive/folders/1AbC_d-9?usp=sharing", "auth": { "type": "apiKey", "apiKeyEnv": "KEY" }, "snapshot": "Snap" } ] }""", "/tmp");
        SourceConfig source = Assert.Single(parsed.Sources);
        Assert.Equal(SourceType.GoogleFolder, source.Type);
        Assert.Equal("1AbC_d-9", source.FolderId);
        Assert.True(source.IsRemote);

        SourceConfig again = Assert.Single(HolySheetConfig.Parse(parsed.ToJson(), "/tmp").Sources);
        Assert.Equal(SourceType.GoogleFolder, again.Type);
        Assert.Equal("1AbC_d-9", again.FolderId);
        Assert.Equal("KEY", again.Auth.ApiKeyEnv);
        Assert.Equal("Snap", again.Snapshot);
        Assert.Contains("\"googleFolder\"", parsed.ToJson());
    }
}
