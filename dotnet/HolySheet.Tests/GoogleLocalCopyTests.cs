using System.Net;
using System.Text;
using System.Text.Json;
using HolySheet.Core;
using HolySheet.Core.Config;
using HolySheet.Core.Sources;
using Xunit;

namespace HolySheet.Tests;

public class GoogleLocalCopyTests
{
    private const string Book = "Remote";

    private sealed class Google : HttpMessageHandler
    {
        public readonly List<string> Requests = [];
        public int Value = 160;
        public bool Invalid;
        public bool Fail;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = Uri.UnescapeDataString(request.RequestUri!.ToString());
            Requests.Add(url);
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            string body;
            if (Fail) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            if (url.Contains("values:batchGet"))
            {
                var ranges = new[] { "Gear", "Price" }.Where(n => url.Contains("ranges='" + n + "'"));
                body = JsonSerializer.Serialize(new { valueRanges = ranges.Select(n => new { values = Rows(n) }) });
            }
            else if (url.Contains("/drive/v3/files/folder?"))
                body = """{"name":"Folder","mimeType":"application/vnd.google-apps.folder"}""";
            else if (url.Contains("/drive/v3/files?"))
                body = """{"files":[{"id":"doc","name":"Remote","mimeType":"application/vnd.google-apps.spreadsheet","version":"8"}]}""";
            else if (url.Contains("/drive/v3/files/doc?"))
                body = """{"version":"8"}""";
            else if (url.Contains("fields=properties.title"))
                body = """{"properties":{"title":"Remote"},"sheets":[{"properties":{"title":"Gear","sheetId":5}},{"properties":{"title":"Price","sheetId":6}}]}""";
            else if (url.EndsWith("/htmlview"))
                body = """items.push({name: "First", pageUrl: "example", gid: "0"});items.push({name: "Hero\x27s Gear", pageUrl: "another", gid: "37"});""";
            else if (url.Contains("format=csv"))
                body = string.Join("\n", Rows("Gear").Select(row => string.Join(",", row)));
            else
                throw new InvalidOperationException("Unexpected request " + url);
            response.Content = new StringContent(body, Encoding.UTF8, url.Contains("format=csv") ? "text/csv" : "application/json");
            return Task.FromResult(response);
        }

        private string[][] Rows(string tab) => [["Key<int>", Invalid && tab == "Gear" ? "" : "int"], ["Id", "Power"], [], ["1", Value.ToString()]];
    }

    private static int Power(BuildResult result, string sheet) =>
        int.Parse(result.Workbooks.Single(b => b.Name == Book).Sheets.Single(s => s.Name == sheet).GetCell(3, 1).Text);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task One_tab_is_persistent_independent_of_Drive_version_and_refreshes_in_preview(bool folder)
    {
        using var temp = new TempDir();
        var google = new Google();
        var options = new TableBuildOptions { HttpHandler = google, KeepGooglePreview = true };
        var source = folder
            ? new SourceConfig { Type = SourceType.GoogleFolder, FolderId = "folder" }
            : new SourceConfig { Type = SourceType.Google, SpreadsheetId = "doc" };
        source.Auth = new GoogleAuthConfig { Type = GoogleAuthType.ApiKey, ApiKey = "test" };
        var config = new HolySheetConfig { BaseDirectory = temp.Path, CsvBackend = "builtin", Sources = { source } };
        BuildResult preview = TableBuild.Run(config, options);
        Assert.True(preview.Success, Sheets.Errors(preview));
        preview.ReleaseBuildData();
        google.Requests.Clear();
        google.Value = 150;
        preview = await GoogleTabCopies.ChangeAsync(config, preview, Book, "Gear", true, options);
        Assert.Contains("ranges='Gear'", Assert.Single(google.Requests));
        Assert.DoesNotContain("'Price'", google.Requests[0]);
        Assert.Equal(150, Power(preview, "Gear"));
        Assert.Equal(160, Power(preview, "Price"));
        Assert.NotNull(preview.GoogleTabs[(Book, "Gear")].LocalCopyUtc);
        Assert.Null(preview.GoogleTabs[(Book, "Price")].LocalCopyUtc);

        google.Value = 130;
        google.Requests.Clear();
        preview = TableBuild.Run(config, options);
        Assert.Equal(150, Power(preview, "Gear"));
        Assert.Equal(130, Power(preview, "Price"));
        Assert.Contains(google.Requests, url => url.Contains("ranges='Price'"));
        Assert.DoesNotContain(google.Requests, url => url.Contains("ranges='Gear'"));

        google.Invalid = true;
        preview = await GoogleTabCopies.ChangeAsync(config, preview, Book, "Gear", true, options);
        Assert.True(preview.Diagnostics.HasErrors);
        google.Invalid = false;
        google.Value = 110;
        google.Requests.Clear();
        preview = await GoogleTabCopies.ChangeAsync(config, preview, Book, "Gear", true, options);
        Assert.True(preview.Success, Sheets.Errors(preview));
        Assert.Single(google.Requests);
        Assert.Equal(110, Power(preview, "Gear"));
        Assert.Equal(130, Power(preview, "Price"));

        var copy = new GoogleLocalCopies(temp.Path);
        string path = copy.CsvPath("doc", "Gear");
        var previousBytes = File.ReadAllBytes(path);
        var previousTime = File.GetLastWriteTimeUtc(path);
        google.Fail = true;
        await Assert.ThrowsAsync<GoogleSourceException>(() => GoogleTabCopies.ChangeAsync(config, preview, Book, "Gear", true, options));
        Assert.Equal(previousBytes, File.ReadAllBytes(path));
        Assert.Equal(previousTime, File.GetLastWriteTimeUtc(path));
        google.Fail = false;

        preview = await GoogleTabCopies.ChangeAsync(config, preview, Book, "Price", true, options);
        Assert.True(config.CanPreviewOffline());
        google.Requests.Clear();
        google.Fail = true;
        preview = TableBuild.Run(config, options);
        Assert.True(preview.Success, Sheets.Errors(preview));
        Assert.Empty(google.Requests);
        google.Fail = false;
        google.Value = 99;
        options.RefreshGoogle = true;
        preview = TableBuild.Run(config, options);
        Assert.Equal(99, Power(preview, "Gear"));
        Assert.Contains(",99", File.ReadAllText(path));
        options.RefreshGoogle = false;
        google.Requests.Clear();
        preview = await GoogleTabCopies.ChangeAsync(config, preview, Book, "Gear", false, options);
        Assert.False(File.Exists(path));
        Assert.Empty(google.Requests);
        Assert.Null(preview.GoogleTabs[(Book, "Gear")].LocalCopyUtc);
        Assert.False(config.CanPreviewOffline());
        google.Value = 80;
        preview = TableBuild.Run(config, options);
        Assert.Equal(80, Power(preview, "Gear"));
        Assert.Equal(99, Power(preview, "Price"));
    }

    [Fact]
    public async Task Pinning_from_an_incomplete_snapshot_keeps_other_configured_tabs_live()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(temp.Combine("snapshot"));
        File.WriteAllText(temp.Combine("snapshot", "Gear.csv"), "Key<int>,int\nId,Power\n,\n1,2");
        var source = new SourceConfig { Type = SourceType.Google, SpreadsheetId = "doc", Name = Book, Snapshot = "snapshot",
            Auth = { Type = GoogleAuthType.ApiKey, ApiKey = "test" }, Sheets = { new GoogleSheetRef("Gear", 5), new GoogleSheetRef("Price", 6) } };
        var config = new HolySheetConfig { BaseDirectory = temp.Path, Sources = { source } };
        var google = new Google();
        var options = new TableBuildOptions { Offline = true, KeepGooglePreview = true, HttpHandler = google };
        var preview = TableBuild.Run(config, options);
        await GoogleTabCopies.ChangeAsync(config, preview, Book, "Gear", true, options);
        google.Requests.Clear();
        google.Value = 100;
        options.Offline = false;
        var normal = TableBuild.Run(config, options);
        Assert.Equal(160, Power(normal, "Gear"));
        Assert.Equal(100, Power(normal, "Price"));
        Assert.Contains(google.Requests, url => url.Contains("ranges='Price'"));
        Assert.DoesNotContain(google.Requests, url => url.Contains("ranges='Gear'"));
    }

    [Fact]
    public void Drive_uploads_and_local_workbooks_do_not_expose_Google_tab_actions()
    {
        using var temp = new TempDir();
        foreach (string name in new[] { "Native", "Upload.xlsx", "Local" })
        {
            Directory.CreateDirectory(temp.Combine("snapshot", name));
            File.WriteAllText(temp.Combine("snapshot", name, name.Replace(".xlsx", "") + ".csv"), "Key<int>\nId\n\n1");
        }
        File.WriteAllText(temp.Combine("snapshot", "workbooks.index"),
            "Native\tNative\thttps://docs.google.com/spreadsheets/d/native-id/edit\nUpload.xlsx\tUpload.xlsx\thttps://drive.google.com/file/d/file-id/view\n");
        var config = new HolySheetConfig { BaseDirectory = temp.Path, Sources = {
            new SourceConfig { Type = SourceType.GoogleFolder, FolderId = "folder", Snapshot = "snapshot", Auth = { Type = GoogleAuthType.ApiKey, ApiKey = "test" } },
            new SourceConfig { Type = SourceType.Csv, Path = "snapshot/Local" } } };
        var preview = TableBuild.Run(config, new TableBuildOptions { KeepGooglePreview = true, Offline = true });
        Assert.Equal(3, preview.Workbooks.Count);
        var tab = Assert.Single(preview.GoogleTabs.Values);
        Assert.Equal("Native", tab.Sheet);
        Assert.Equal("native-id", tab.SpreadsheetId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Public_tab_uses_one_raw_CSV_export_from_a_snapshot_preview(bool explicitGid)
    {
        using var temp = new TempDir();
        const string tab = "Hero's Gear";
        Directory.CreateDirectory(temp.Combine("snapshot"));
        File.WriteAllText(temp.Combine("snapshot", tab + ".csv"), "Key<int>,int\nId,Power\n,\n1,2\n");
        var source = new SourceConfig { Type = SourceType.Google, SpreadsheetId = "doc", Name = Book, Snapshot = "snapshot" };
        if (explicitGid) source.Sheets.Add(new GoogleSheetRef(tab, 12));
        var config = new HolySheetConfig { BaseDirectory = temp.Path, Sources = { source } };
        var google = new Google();
        var options = new TableBuildOptions { Offline = true, KeepGooglePreview = true, HttpHandler = google };
        var preview = TableBuild.Run(config, options);
        preview = await GoogleTabCopies.ChangeAsync(config, preview, Book, tab, true, options);
        Assert.Equal(160, Power(preview, tab));
        Assert.Contains(explicitGid ? "gid=12" : "gid=37", Assert.Single(google.Requests, url => url.Contains("format=csv")));
        Assert.Equal(explicitGid ? 1 : 2, google.Requests.Count);
        google.Requests.Clear();
        Assert.Equal(160, Power(TableBuild.Run(config, options), tab));
        Assert.Empty(google.Requests);
    }
}
