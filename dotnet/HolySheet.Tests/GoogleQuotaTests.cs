using System.Net;
using System.Text;
using HolySheet.Core;
using HolySheet.Core.Config;
using HolySheet.Core.Schema;
using HolySheet.Core.Sources;
using Xunit;

namespace HolySheet.Tests;

/// <summary>How the Google sources spare the quota: one batch request per spreadsheet, nothing for unchanged files, retries when limited.</summary>
public class GoogleQuotaTests
{
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

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Status(HttpStatusCode status, string body = "") => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    // "Gear" is a table; "#Hero's Notes" is an ignored sheet whose name needs quoting in a range.
    private const string Tabs = """{ "properties": { "title": "Live" }, "sheets": [ { "properties": { "sheetId": 0, "title": "Gear" } }, { "properties": { "sheetId": 9, "title": "#Hero's Notes" } } ] }""";

    private const string Values = """
        { "valueRanges": [
          { "range": "Gear!A1:C6", "values": [ ["Key<int>", "string", "float"], ["Id", "Name", "Weight"], [], ["1", "Sword, \"long\"", "2.5"], ["2", "two\nlines"], ["3", "포션", "0.25"] ] },
          { "range": "'#Hero''s Notes'!A1", "values": [ ["just notes"] ] } ] }
        """;

    private sealed class Google
    {
        public string Version = "41";
        public int FailFirst;
        public HttpStatusCode FailWith = (HttpStatusCode)429;
        public string FailBody = "";
        public string? RetryAfter;

        public HttpResponseMessage Serve(HttpRequestMessage request)
        {
            string url = Uri.UnescapeDataString(request.RequestUri!.ToString());
            if (FailFirst > 0)
            {
                FailFirst--;
                HttpResponseMessage failure = Status(FailWith, FailBody);
                if (RetryAfter != null)
                {
                    failure.Headers.TryAddWithoutValidation("Retry-After", RetryAfter);
                }

                return failure;
            }

            if (url.StartsWith("https://drive.test/drive/v3/files/sheet-id?", StringComparison.Ordinal))
            {
                return Json($$"""{ "version": "{{Version}}" }""");
            }

            if (url.StartsWith("https://sheets.test/v4/spreadsheets/sheet-id?", StringComparison.Ordinal))
            {
                return Json(Tabs);
            }

            return url.StartsWith("https://sheets.test/v4/spreadsheets/sheet-id/values:batchGet?", StringComparison.Ordinal) ? Json(Values) : Status(HttpStatusCode.NotFound);
        }
    }

    private static (BuildResult Result, StubHandler Handler, List<TimeSpan> Waits) Build(string baseDirectory, Google google, SheetSelection? only = null)
    {
        var waits = new List<TimeSpan>();
        var endpoints = new GoogleEndpoints
        {
            SheetsApi = "https://sheets.test", Docs = "https://docs.test", DriveApi = "https://drive.test",
            Wait = (delay, _) => { lock (waits) { waits.Add(delay); } return Task.CompletedTask; }
        };
        var handler = new StubHandler(google.Serve);
        var config = new HolySheetConfig
        {
            BaseDirectory = baseDirectory, CsvBackend = "builtin",
            Sources = { new SourceConfig { Type = SourceType.Google, SpreadsheetId = "sheet-id", Auth = { Type = GoogleAuthType.ApiKey, ApiKey = "KEY" } } }
        };
        return (TableBuild.Run(config, new TableBuildOptions { HttpHandler = handler, GoogleEndpoints = endpoints, Only = only }), handler, waits);
    }

    [Fact]
    public void One_batch_request_brings_every_tab_as_the_author_sees_it()
    {
        using var temp = new TempDir();
        (BuildResult result, StubHandler handler, _) = Build(temp.Path, new Google());
        Assert.True(result.Success, Sheets.Errors(result));
        string batch = Assert.Single(handler.Requests, request => request.Contains("values:batchGet"));
        // Sheet names are quoted, with their own quotes doubled.
        Assert.Contains("ranges='Gear'", batch);
        Assert.Contains("ranges='#Hero''s Notes'", batch);
        Assert.Contains("valueRenderOption=FORMATTED_VALUE", batch);

        TableData item = result.Tables.Single(table => table.Schema.Name == "Gear");
        Assert.Equal("Sword, \"long\"", item.Rows[0].Values[1]);
        Assert.Equal("two\nlines", item.Rows[1].Values[1]);
        Assert.Equal(0f, item.Rows[1].Values[2]);
        Assert.Equal("포션", item.Rows[2].Values[1]);
        Assert.Single(result.Tables);
    }

    [Theory]
    [InlineData(SourceType.Google)]
    [InlineData(SourceType.GoogleFolder)]
    public void Manual_refresh_replaces_a_stale_header_even_if_drive_still_reports_the_same_version(SourceType type)
    {
        using var temp = new TempDir();
        bool corrected = false;
        var handler = new StubHandler(request =>
        {
            string url = request.RequestUri!.GetLeftPart(UriPartial.Path);
            if (url == "https://drive.test/drive/v3/files/folder")
                return Json("""{ "id": "folder", "name": "Data", "mimeType": "application/vnd.google-apps.folder" }""");
            if (url == "https://drive.test/drive/v3/files")
                return Json("""{ "files": [{ "id": "sheet-id", "name": "QQQQ", "mimeType": "application/vnd.google-apps.spreadsheet", "version": "31" }] }""");
            if (url == "https://drive.test/drive/v3/files/sheet-id")
                return Json("""{ "version": "31" }""");
            if (url == "https://sheets.test/v4/spreadsheets/sheet-id")
                return Json("""{ "properties": { "title": "QQQQ" }, "sheets": [{ "properties": { "sheetId": 0, "title": "Hello" } }] }""");
            if (url.EndsWith("/values:batchGet", StringComparison.Ordinal))
            {
                string names = corrected ? """["Id","Value"]""" : """["Id","Value","ㅁ"]""";
                return Json($$"""{ "valueRanges": [{ "range": "Hello!A1:C4", "values": [["Key<int>","float"],{{names}},[],["1","2"]] }] }""");
            }
            return Status(HttpStatusCode.NotFound);
        });
        var config = new HolySheetConfig
        {
            BaseDirectory = temp.Path,
            Sources = { new SourceConfig { Type = type, FolderId = "folder", SpreadsheetId = "sheet-id", Snapshot = "snapshot", Auth = { Type = GoogleAuthType.ApiKey, ApiKey = "KEY" } } }
        };
        var options = new TableBuildOptions
        {
            HttpHandler = handler,
            GoogleEndpoints = new GoogleEndpoints { SheetsApi = "https://sheets.test", DriveApi = "https://drive.test" }
        };

        Assert.Contains(TableBuild.Run(config, options).Diagnostics.Items, d => d.Location == "QQQQ / Hello!C1" && d.Message.Contains("'ㅁ' has no type"));
        corrected = true;
        options.RefreshGoogle = true;
        options.Offline = true;
        handler.Requests.Clear();
        Assert.False(TableBuild.Run(config, options).Success);
        Assert.Empty(handler.Requests);

        options.Offline = false;
        var refreshed = TableBuild.Run(config, options);
        Assert.True(refreshed.Success, Sheets.Errors(refreshed));
        Assert.Single(handler.Requests, request => request.Contains("values:batchGet"));
        options.RefreshGoogle = false;
        handler.Requests.Clear();
        Assert.True(TableBuild.Run(config, options).Success);
        Assert.DoesNotContain(handler.Requests, request => request.Contains("sheets.test"));
        options.Offline = true;
        handler.Requests.Clear();
        Assert.True(TableBuild.Run(config, options).Success);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void An_unchanged_file_costs_one_drive_request_and_no_sheets_requests()
    {
        using var temp = new TempDir();
        var google = new Google();
        (BuildResult first, _, _) = Build(temp.Path, google);
        Assert.True(first.Success, Sheets.Errors(first));

        (BuildResult second, StubHandler handler, _) = Build(temp.Path, google);
        Assert.True(second.Success, Sheets.Errors(second));
        Assert.Equal(["https://drive.test/drive/v3/files/sheet-id?fields=version&supportsAllDrives=true&key=KEY"], handler.Requests);
        Assert.Equal(first.Output!.ContentHash, second.Output!.ContentHash);

        // Someone edits the spreadsheet: Drive reports a new version and the tabs are downloaded again.
        google.Version = "42";
        (BuildResult third, StubHandler after, _) = Build(temp.Path, google);
        Assert.True(third.Success, Sheets.Errors(third));
        Assert.Contains(after.Requests, request => request.Contains("values:batchGet"));
    }

    [Fact]
    public async Task An_unchanged_drive_version_respects_each_configured_tab_list()
    {
        using var temp = new TempDir();
        var google = new Google();
        var handler = new StubHandler(request =>
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/values:batchGet", StringComparison.Ordinal))
                return google.Serve(request);
            int count = request.RequestUri.Query.TrimStart('?').Split('&').Count(part => part.StartsWith("ranges=", StringComparison.Ordinal));
            return Json("""{ "valueRanges": [""" + string.Join(",", Enumerable.Repeat("""{ "values": [["value"]] }""", count)) + "]}");
        });
        var source = new SourceConfig { Type = SourceType.Google, SpreadsheetId = "sheet-id", Auth = { Type = GoogleAuthType.ApiKey, ApiKey = "KEY" } };
        var endpoints = new GoogleEndpoints { SheetsApi = "https://sheets.test", DriveApi = "https://drive.test" };
        string snapshot = temp.Combine("snapshot");
        bool refresh = false;

        async Task Check(string[] expected, bool downloads, params GoogleSheetRef[] configured)
        {
            source.Sheets = configured.ToList();
            var reader = new GoogleSheetsSource(source, temp.Path, HolySheet.Core.Csv.BuiltinCsvParser.Instance,
                snapshot, handler, endpoints, remoteCacheDirectory: temp.Combine("cache")) { RefreshGoogle = refresh };
            handler.Requests.Clear();
            var diagnostics = new DiagnosticBag();
            Workbook book = Assert.Single(await reader.LoadAsync(diagnostics, default));
            Assert.False(diagnostics.HasErrors);
            Assert.Equal(expected, book.Sheets.Select(sheet => sheet.Name));
            Assert.Equal(downloads ? 1 : 0, handler.Requests.Count(url => url.Contains("values:batchGet")));
            Assert.Equal(expected.Order(), Directory.GetFiles(snapshot, "*.csv").Select(Path.GetFileNameWithoutExtension).Order());
        }

        var gear = new GoogleSheetRef("Gear", 0);
        var notes = new GoogleSheetRef("#Hero's Notes", 9);
        await Check(["Gear"], true, gear);
        await Check(["Gear"], false, gear);
        await Check([notes.Name, "Gear"], true, notes, gear);
        await Check([notes.Name], true, notes);
        await Check(["Gear", notes.Name], true); // Discover every tab.
        await Check(["Gear"], false, gear);      // A second configuration of this file keeps its own cache.
        await Check(["Gear"], true, new GoogleSheetRef("Gear", 25));
        refresh = true;
        await Check(["Gear"], true, gear);
        refresh = false;
        await Check([notes.Name, "Gear"], true, notes, gear);
    }

    [Fact]
    public void A_partial_download_of_a_changed_file_does_not_leave_a_mixed_copy()
    {
        using var temp = new TempDir();
        var google = new Google();
        Build(temp.Path, google);
        google.Version = "42";
        Build(temp.Path, google, SheetSelection.Parse(["Live/Gear"]));

        // The copy no longer matches one version, so the next full build downloads everything.
        (BuildResult full, StubHandler handler, _) = Build(temp.Path, google);
        Assert.True(full.Success, Sheets.Errors(full));
        Assert.Contains(handler.Requests, request => request.Contains("values:batchGet"));
    }

    [Fact]
    public void Too_many_requests_are_retried_after_the_time_google_asks_for()
    {
        using var temp = new TempDir();
        var google = new Google { FailFirst = 2, RetryAfter = "3" };
        (BuildResult result, _, List<TimeSpan> waits) = Build(temp.Path, google);
        Assert.True(result.Success, Sheets.Errors(result));
        Assert.Equal([TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3)], waits);
    }

    [Fact]
    public void Server_errors_and_drive_rate_limits_are_retried_with_growing_waits()
    {
        using var temp = new TempDir();
        (BuildResult result, _, List<TimeSpan> waits) = Build(temp.Path, new Google { FailFirst = 3, FailWith = HttpStatusCode.ServiceUnavailable });
        Assert.True(result.Success, Sheets.Errors(result));
        Assert.Equal(3, waits.Count);
        // 1, 2 and 4 seconds, each plus up to one second at random.
        Assert.InRange(waits[0].TotalSeconds, 1, 2);
        Assert.InRange(waits[1].TotalSeconds, 2, 3);
        Assert.InRange(waits[2].TotalSeconds, 4, 5);

        const string driveLimit = """{ "error": { "code": 403, "errors": [ { "domain": "usageLimits", "reason": "userRateLimitExceeded" } ] } }""";
        (BuildResult limited, _, List<TimeSpan> driveWaits) = Build(temp.Path, new Google { FailFirst = 1, FailWith = HttpStatusCode.Forbidden, FailBody = driveLimit });
        Assert.True(limited.Success, Sheets.Errors(limited));
        Assert.Single(driveWaits);
    }

    [Fact]
    public void A_limit_that_does_not_lift_ends_in_a_clear_error_and_a_refusal_is_not_retried()
    {
        using var temp = new TempDir();
        (BuildResult result, StubHandler handler, List<TimeSpan> waits) = Build(temp.Path, new Google { FailFirst = 100 });
        Assert.False(result.Success);
        Assert.Contains("rate limiting", Sheets.Errors(result));
        // The version check is optional and gives up quietly; the tab list is tried five times.
        Assert.Equal(8, waits.Count);

        (BuildResult refused, StubHandler refusedHandler, List<TimeSpan> noWaits) = Build(temp.Path, new Google { FailFirst = 100, FailWith = HttpStatusCode.Forbidden });
        Assert.False(refused.Success);
        Assert.Empty(noWaits);
    }

    [Fact]
    public void A_drive_folder_downloads_only_the_files_whose_version_changed()
    {
        using var temp = new TempDir();
        string sheetVersion = "5";
        var handler = new StubHandler(request =>
        {
            string url = Uri.UnescapeDataString(request.RequestUri!.ToString());
            if (url.StartsWith("https://drive.test/drive/v3/files/folder-id?", StringComparison.Ordinal))
            {
                return Json("""{ "id": "folder-id", "name": "Data", "mimeType": "application/vnd.google-apps.folder" }""");
            }

            if (url.StartsWith("https://drive.test/drive/v3/files?", StringComparison.Ordinal))
            {
                Assert.Contains("version", url);
                return Json($$"""{ "files": [ { "id": "sheet-id", "name": "Live", "mimeType": "application/vnd.google-apps.spreadsheet", "version": "{{sheetVersion}}" }, { "id": "xlsx-1", "name": "Game.xlsx", "mimeType": "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "version": "9" } ] }""");
            }

            if (url.StartsWith("https://drive.test/drive/v3/files/xlsx-1?alt=media", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(File.ReadAllBytes(Repo.Fixture("Game.libreoffice.xlsx"))) };
            }

            return new Google().Serve(request);
        });
        var config = new HolySheetConfig
        {
            BaseDirectory = temp.Path, CsvBackend = "builtin",
            Sources = { new SourceConfig { Type = SourceType.GoogleFolder, FolderId = "folder-id", Auth = { Type = GoogleAuthType.ApiKey, ApiKey = "KEY" } } }
        };
        var endpoints = new GoogleEndpoints { SheetsApi = "https://sheets.test", Docs = "https://docs.test", DriveApi = "https://drive.test", Wait = (_, _) => Task.CompletedTask };
        BuildResult first = TableBuild.Run(config, new TableBuildOptions { HttpHandler = handler, GoogleEndpoints = endpoints });
        Assert.True(first.Success, Sheets.Errors(first));

        handler.Requests.Clear();
        BuildResult second = TableBuild.Run(config, new TableBuildOptions { HttpHandler = handler, GoogleEndpoints = endpoints });
        Assert.True(second.Success, Sheets.Errors(second));
        // Only the folder and its listing: the versions are the same, so neither file is downloaded.
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(first.Output!.ContentHash, second.Output!.ContentHash);

        sheetVersion = "6";
        handler.Requests.Clear();
        TableBuild.Run(config, new TableBuildOptions { HttpHandler = handler, GoogleEndpoints = endpoints });
        Assert.Contains(handler.Requests, request => request.Contains("values:batchGet"));
        Assert.DoesNotContain(handler.Requests, request => request.Contains("alt=media"));

        handler.Requests.Clear();
        TableBuild.Run(config, new TableBuildOptions { HttpHandler = handler, GoogleEndpoints = endpoints, RefreshGoogle = true });
        Assert.Single(handler.Requests, request => request.Contains("values:batchGet"));
        Assert.Single(handler.Requests, request => request.Contains("alt=media"));
    }
}
