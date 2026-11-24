#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HolySheet.Core.Config;
using HolySheet.Core.Csv;

namespace HolySheet.Core.Sources
{
    public sealed class GoogleEndpoints
    {
        public string SheetsApi { get; set; } = "https://sheets.googleapis.com";

        public string Docs { get; set; } = "https://docs.google.com";

        public string DriveApi { get; set; } = "https://www.googleapis.com";

        public int MaxAttempts { get; set; } = 5;

        public Func<TimeSpan, CancellationToken, Task> Wait { get; set; } = Task.Delay;
    }

    internal class GoogleSourceException : Exception
    {
        public GoogleSourceException(string message, string hint) : base(message)
        {
            Hint = hint;
        }

        public string Hint { get; }
    }

    internal sealed class GoogleApiDisabledException : GoogleSourceException
    {
        public GoogleApiDisabledException(string api, string? project, string? enableUrl) : base($"The {api} is not enabled in Google Cloud project {project ?? "of these credentials"}.",
            $"Enable it at {enableUrl ?? "console.cloud.google.com/apis/library"} and try again a few minutes later.")
        {
            Api = api;
        }

        public string Api { get; }
    }

    public sealed class GoogleSheetsSource : IWorkbookSource
    {
        private readonly SourceConfig source;
        private readonly GoogleLocalCopies localCopies;
        private readonly string baseDirectory;
        private readonly ICsvParser parser;
        private readonly string? snapshotDirectory;
        private readonly HttpMessageHandler? httpHandler;
        private readonly SheetSelection? only;
        private readonly string? remoteCacheDirectory;
        private readonly IReadOnlyList<IGoogleCredentialProvider>? credentialProviders;

        public GoogleSheetsSource(SourceConfig source, string baseDirectory, ICsvParser parser, string? snapshotDirectory = null, HttpMessageHandler? httpHandler = null, GoogleEndpoints? endpoints = null, SheetSelection? only = null, string? remoteCacheDirectory = null, IReadOnlyList<IGoogleCredentialProvider>? credentialProviders = null)
        {
            this.source = source;
            localCopies = new GoogleLocalCopies(baseDirectory);
            this.baseDirectory = baseDirectory;
            this.parser = parser;
            this.snapshotDirectory = snapshotDirectory;
            this.httpHandler = httpHandler;
            this.only = only;
            this.remoteCacheDirectory = remoteCacheDirectory;
            this.credentialProviders = credentialProviders;
            _endpoints = endpoints ?? new GoogleEndpoints();
        }

        private const int MaxConcurrentDownloads = 4;
        private const string Scopes = "https://www.googleapis.com/auth/drive.readonly https://www.googleapis.com/auth/spreadsheets.readonly";

        private readonly GoogleEndpoints _endpoints;

        public string Description => "google:" + source.SpreadsheetId;

        internal ResolvedAuth? PresetAuth { get; set; }

        private string? _account;

        public IReadOnlyList<string> WatchPaths => Array.Empty<string>();

        internal long? KnownVersion { get; set; }

        internal bool RefreshGoogle { get; init; }

        public async Task<IReadOnlyList<Workbook>> LoadAsync(DiagnosticBag diagnostics, CancellationToken cancellationToken)
        {
            string id = source.SpreadsheetId ?? "";
            string location = source.Name ?? Description;
            using HttpClient client = httpHandler == null ? new HttpClient() : new HttpClient(httpHandler, disposeHandler: false);
            client.Timeout = TimeSpan.FromSeconds(100);
            try
            {
                (string? apiKey, string? bearer, string? account) = PresetAuth ?? await ResolveAuthAsync(client, cancellationToken).ConfigureAwait(false);
                _account = account;
                string? title = source.Name;
                if (only != null && title != null && !only.Touches(title))
                {
                    return Array.Empty<Workbook>();
                }

                bool hasCredentials = apiKey != null || bearer != null;

                long? version = KnownVersion ?? (hasCredentials && remoteCacheDirectory != null ? await TryGetVersionAsync(client, id, apiKey, bearer, cancellationToken).ConfigureAwait(false) : null);
                // A manual refresh reads the cells themselves: Drive can still report the previous version after an edit.
                if (!RefreshGoogle && version != null && RemoteCopy.TryLoad(remoteCacheDirectory, id, version.Value) is { } copy)
                {
                    string name = title ?? copy.Title;
                    if (only != null && !only.Touches(name))
                    {
                        return Array.Empty<Workbook>();
                    }

                    List<(string Name, string Text)> kept = copy.Tabs.Where(tab => only == null || only.Includes(name, tab.Name)).ToList();
                    WriteSnapshot(name, kept.Select(tab => tab.Name).ToList(), kept.Select(tab => tab.Text).ToList(), complete: only == null);
                    return new[] { Linked(ToWorkbook(name, kept, diagnostics)) };
                }

                string workbookName;
                List<(string Name, string Text)> fetched;
                bool complete = only == null;
                if (!hasCredentials)
                {
                    workbookName = title ?? id;
                    if (source.Sheets.Count == 0)
                    {
                        if (!RefreshGoogle && localCopies.HasCopies(id))
                        {
                            fetched = await ReadExportTabsAsync(client, id, workbookName, bearer, cancellationToken).ConfigureAwait(false);
                        }
                        else
                        {
                            fetched = await FetchXlsxExportAsync(client, id, workbookName, bearer: null, cancellationToken).ConfigureAwait(false);
                            complete = true;
                        }
                    }
                    else
                    {
                        var wanted = source.Sheets.Where(tab => only == null || only.Includes(workbookName, tab.Name)).Select(tab => tab.Name).ToList();
                        if (wanted.Count == 0) return Array.Empty<Workbook>();
                        fetched = await ReadTabsAsync(client, id, wanted, apiKey, bearer, cancellationToken).ConfigureAwait(false);
                    }
                }
                else
                {
                    try
                    {
                        IReadOnlyList<GoogleSheetRef> tabs = source.Sheets;
                        if (tabs.Count == 0)
                        {
                            (string listedTitle, List<GoogleSheetRef> listed) = await ListTabsAsync(client, id, apiKey, bearer, cancellationToken).ConfigureAwait(false);
                            title ??= listedTitle;
                            tabs = listed;
                        }

                        workbookName = title ?? id;
                        if (only != null && !only.Touches(workbookName))
                        {
                            return Array.Empty<Workbook>();
                        }

                        List<string> wanted = tabs.Select(tab => tab.Name).Where(name => only == null || only.Includes(workbookName, name)).ToList();
                        if (wanted.Count == 0)
                        {
                            return Array.Empty<Workbook>();
                        }

                        fetched = await ReadTabsAsync(client, id, wanted, apiKey, bearer, cancellationToken).ConfigureAwait(false);
                    }
                    catch (GoogleApiDisabledException disabled) when (disabled.Api.Contains("Sheets"))
                    {
                        workbookName = title ?? id;
                        if (!RefreshGoogle && localCopies.HasCopies(id))
                        {
                            diagnostics.AddWarning(location, disabled.Message + " Live tabs were read as CSV exports.", disabled.Hint);
                            fetched = await ReadExportTabsAsync(client, id, workbookName, bearer, cancellationToken).ConfigureAwait(false);
                        }
                        else
                        {
                            diagnostics.AddWarning(location, disabled.Message + " The spreadsheet was read as one xlsx export instead, so every tab is downloaded.", disabled.Hint);
                            fetched = await FetchXlsxExportAsync(client, id, workbookName, bearer, cancellationToken).ConfigureAwait(false);
                            complete = true;
                        }
                    }
                }

                WriteSnapshot(workbookName, fetched.Select(tab => tab.Name).ToList(), fetched.Select(tab => tab.Text).ToList(), complete);
                if (version != null)
                {
                    if (complete && (RefreshGoogle || !fetched.Any(tab => File.Exists(localCopies.CsvPath(id, tab.Name)))))
                    {
                        RemoteCopy.Save(remoteCacheDirectory, id, version.Value, workbookName, fetched);
                    }
                    else
                    {
                        // Some tabs are now newer than the copy: it no longer matches any single version.
                        RemoteCopy.Invalidate(remoteCacheDirectory, id);
                    }
                }

                return new[] { Linked(ToWorkbook(workbookName, fetched, diagnostics)) };
            }
            catch (GoogleSourceException exception)
            {
                diagnostics.AddError(location, exception.Message, exception.Hint);
            }
            catch (HttpRequestException exception)
            {
                diagnostics.AddError(location, $"Google could not be reached: {exception.Message}", "Check the network connection. Use --offline to build from the saved snapshot.");
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                diagnostics.AddError(location, "The request to Google timed out.", "Try again. Use --offline to build from the saved snapshot.");
            }

            return Array.Empty<Workbook>();
        }

        private Workbook ToWorkbook(string workbookName, IReadOnlyList<(string Name, string Text)> tabs, DiagnosticBag diagnostics)
        {
            var sheets = new List<Sheet>(tabs.Count);
            foreach ((string name, string text) in tabs)
            {
                try
                {
                    sheets.Add(Sheet.FromGrid(workbookName, name, parser.Parse(text)));
                    if (RefreshGoogle)
                    {
                        string path = localCopies.CsvPath(source.SpreadsheetId!, name);
                        if (File.Exists(path)) GoogleLocalCopies.Write(path, text, existingOnly: true);
                    }
                }
                catch (CsvFormatException exception)
                {
                    diagnostics.AddError($"{workbookName} / {name}", $"The CSV that Google returned could not be read: {exception.Message}");
                }
            }

            return new Workbook(workbookName, sheets);
        }

        internal async Task<long?> TryGetVersionAsync(HttpClient client, string fileId, string? apiKey, string? bearer, CancellationToken cancellationToken)
        {
            try
            {
                string url = $"{_endpoints.DriveApi}/drive/v3/files/{Uri.EscapeDataString(fileId)}?fields=version&supportsAllDrives=true" + (apiKey == null ? "" : "&key=" + Uri.EscapeDataString(apiKey));
                byte[] bytes = await GetAsync(client, url, bearer, expectHtml: false, cancellationToken).ConfigureAwait(false);
                return ParseVersion(JsonValue.Parse(Encoding.UTF8.GetString(bytes)).AsObject?["version"]);
            }
            catch (Exception exception) when (exception is GoogleSourceException or HttpRequestException or JsonFormatException)
            {
                return null;
            }
        }

        internal static long? ParseVersion(JsonValue? value)
        {
            if (value?.AsString is { } text && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
            {
                return parsed;
            }

            return value?.AsNumber is { } number ? (long)number : null;
        }

        private const int RangesPerRequest = 50;

        private async Task<List<(string Name, string Text)>> FetchBatchAsync(HttpClient client, string id, List<string> tabs, string? apiKey, string? bearer, CancellationToken cancellationToken)
        {
            var result = new List<(string Name, string Text)>(tabs.Count);
            for (int start = 0; start < tabs.Count; start += RangesPerRequest)
            {
                List<string> group = tabs.Skip(start).Take(RangesPerRequest).ToList();
                var url = new StringBuilder($"{_endpoints.SheetsApi}/v4/spreadsheets/{Uri.EscapeDataString(id)}/values:batchGet?valueRenderOption=FORMATTED_VALUE&majorDimension=ROWS");
                foreach (string tab in group)
                {
                    url.Append("&ranges=").Append(Uri.EscapeDataString("'" + tab.Replace("'", "''") + "'"));
                }

                if (apiKey != null)
                {
                    url.Append("&key=").Append(Uri.EscapeDataString(apiKey));
                }

                byte[] bytes = await GetAsync(client, url.ToString(), bearer, expectHtml: false, cancellationToken).ConfigureAwait(false);
                IReadOnlyList<JsonValue> ranges;
                try
                {
                    ranges = JsonValue.Parse(Encoding.UTF8.GetString(bytes)).AsObject?.GetArray("valueRanges") ?? throw new JsonFormatException("valueRanges is missing");
                }
                catch (JsonFormatException exception)
                {
                    throw new GoogleSourceException($"The Sheets API answer could not be read: {exception.Message}", "Try again later.");
                }

                if (ranges.Count != group.Count)
                {
                    throw new GoogleSourceException($"The Sheets API returned {ranges.Count} ranges for {group.Count} tabs.", "Try again later.");
                }

                for (int index = 0; index < group.Count; index++)
                {
                    var rows = new List<IReadOnlyList<string>>();
                    foreach (JsonValue row in ranges[index].AsObject?.GetArray("values") ?? Array.Empty<JsonValue>())
                    {
                        rows.Add((row.AsArray ?? Array.Empty<JsonValue>()).Select(CellText).ToList());
                    }

                    result.Add((group[index], CsvWriter.Write(rows)));
                }
            }

            return result;
        }

        private static string CellText(JsonValue cell)
        {
            return cell.AsString ?? (cell.AsNumber is { } number ? number.ToString("R", CultureInfo.InvariantCulture) : cell.AsBoolean is { } flag ? (flag ? "TRUE" : "FALSE") : "");
        }

        private async Task<List<(string Name, string Text)>> ReadTabsAsync(HttpClient client, string id, List<string> names, string? apiKey, string? bearer, CancellationToken cancellationToken, IReadOnlyList<GoogleSheetRef>? exportTabs = null)
        {
            var local = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!RefreshGoogle)
            {
                foreach (string name in names)
                {
                    string path = localCopies.CsvPath(id, name);
                    if (File.Exists(path)) local[name] = SourceFiles.ReadAllText(path);
                }
            }

            List<string> live = names.Where(name => !local.ContainsKey(name)).ToList();
            if (live.Count > 0)
            {
                var fetched = exportTabs == null && (apiKey != null || bearer != null)
                    ? await FetchBatchAsync(client, id, live, apiKey, bearer, cancellationToken).ConfigureAwait(false)
                    : await FetchCsvExportAsync(client, id, live, bearer, cancellationToken, exportTabs).ConfigureAwait(false);
                foreach (var tab in fetched) local[tab.Name] = tab.Text;
            }

            return names.Select(name => (name, local[name])).ToList();
        }

        internal async Task<string> DownloadTabAsync(string id, string name, CancellationToken cancellationToken)
        {
            using HttpClient client = httpHandler == null ? new HttpClient() : new HttpClient(httpHandler, disposeHandler: false);
            (string? apiKey, string? bearer, string? account) = await ResolveAuthAsync(client, cancellationToken).ConfigureAwait(false);
            _account = account;
            var names = new List<string> { name };
            if (apiKey != null || bearer != null)
            {
                try { return (await FetchBatchAsync(client, id, names, apiKey, bearer, cancellationToken).ConfigureAwait(false))[0].Text; }
                catch (GoogleApiDisabledException disabled) when (disabled.Api.Contains("Sheets")) { }
            }

            return (await FetchCsvExportAsync(client, id, names, bearer, cancellationToken).ConfigureAwait(false))[0].Text;
        }

        private static readonly Regex ViewerTab = new Regex(
            @"items\.push\(\{\s*name:\s*""(?<name>(?:\\.|[^""\\])*)""\s*,\s*pageUrl:\s*""(?:\\.|[^""\\])*""\s*,\s*gid:\s*""(?<gid>[0-9]+)""",
            RegexOptions.CultureInvariant);

        private async Task<IReadOnlyList<GoogleSheetRef>> ExportTabsAsync(HttpClient client, string id, string? bearer, CancellationToken cancellationToken)
        {
            if (source.Sheets.Count > 0) return source.Sheets;
            string url = $"{_endpoints.Docs}/spreadsheets/d/{Uri.EscapeDataString(id)}/htmlview";
            byte[] bytes = await GetAsync(client, url, bearer, expectHtml: true, cancellationToken).ConfigureAwait(false);
            var tabs = new List<GoogleSheetRef>();
            foreach (Match match in ViewerTab.Matches(Encoding.UTF8.GetString(bytes)))
            {
                if (long.TryParse(match.Groups["gid"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long gid))
                    tabs.Add(new GoogleSheetRef(Regex.Unescape(match.Groups["name"].Value), gid));
            }

            if (tabs.Count == 0)
                throw new GoogleSourceException("Google did not list the tabs for this CSV export.", "List the tabs with their gid in the source settings, or enable the Sheets API.");
            return tabs;
        }

        private async Task<List<(string Name, string Text)>> ReadExportTabsAsync(HttpClient client, string id, string workbook, string? bearer, CancellationToken cancellationToken)
        {
            var tabs = await ExportTabsAsync(client, id, bearer, cancellationToken).ConfigureAwait(false);
            var wanted = tabs.Where(tab => only == null || only.Includes(workbook, tab.Name)).Select(tab => tab.Name).ToList();
            return await ReadTabsAsync(client, id, wanted, null, bearer, cancellationToken, tabs).ConfigureAwait(false);
        }

        private async Task<List<(string Name, string Text)>> FetchCsvExportAsync(HttpClient client, string id, List<string> tabs, string? bearer, CancellationToken cancellationToken, IReadOnlyList<GoogleSheetRef>? exportTabs = null)
        {
            var references = exportTabs ?? await ExportTabsAsync(client, id, bearer, cancellationToken).ConfigureAwait(false);
            var texts = new string[tabs.Count];
            using (var gate = new SemaphoreSlim(MaxConcurrentDownloads))
            {
                var downloads = new Task[tabs.Count];
                for (int index = 0; index < tabs.Count; index++)
                {
                    int tabIndex = index;
                    downloads[index] = Task.Run(async () =>
                    {
                        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                        try
                        {
                            GoogleSheetRef tab = references.FirstOrDefault(candidate => candidate.Name == tabs[tabIndex])
                                ?? throw new GoogleSourceException($"Google did not list the tab '{tabs[tabIndex]}'.", "Refresh the project to read the current tab names.");
                            string url = $"{_endpoints.Docs}/spreadsheets/d/{Uri.EscapeDataString(id)}/export?format=csv&gid={tab.Gid.ToString(CultureInfo.InvariantCulture)}";
                            byte[] bytes = await GetAsync(client, url, bearer, expectHtml: false, cancellationToken).ConfigureAwait(false);
                            texts[tabIndex] = new UTF8Encoding(false).GetString(bytes);
                        }
                        finally
                        {
                            gate.Release();
                        }
                    }, cancellationToken);
                }

                await Task.WhenAll(downloads).ConfigureAwait(false);
            }

            return tabs.Select((tab, index) => (tab, texts[index])).ToList();
        }

        private async Task<List<(string Name, string Text)>> FetchXlsxExportAsync(HttpClient client, string id, string workbookName, string? bearer, CancellationToken cancellationToken)
        {
            Workbook workbook = await LoadViaXlsxExportAsync(client, id, workbookName, cancellationToken, bearer).ConfigureAwait(false);
            return workbook.Sheets.Select(sheet => (sheet.Name, CsvWriter.Write(sheet.ToGrid()))).ToList();
        }

        public static string SpreadsheetLink(string id) => "https://docs.google.com/spreadsheets/d/" + Uri.EscapeDataString(id) + "/edit";

        private Workbook Linked(Workbook workbook) => workbook.WithOrigin(new WorkbookOrigin(SourceType.Google, source.SpreadsheetId ?? "", SpreadsheetLink(source.SpreadsheetId ?? "")));

        private async Task<Workbook> LoadViaXlsxExportAsync(HttpClient client, string id, string workbookName, CancellationToken cancellationToken, string? bearer = null)
        {
            string url = $"{_endpoints.Docs}/spreadsheets/d/{Uri.EscapeDataString(id)}/export?format=xlsx";
            byte[] bytes = await GetAsync(client, url, bearer, expectHtml: false, cancellationToken).ConfigureAwait(false);
            Workbook workbook;
            try
            {
                using var stream = new MemoryStream(bytes, writable: false);
                workbook = XlsxReader.Read(stream, workbookName);
            }
            catch (XlsxFormatException exception)
            {
                throw new GoogleSourceException($"The export Google returned is not a workbook: {exception.Message}",
                    "Share the spreadsheet as 'Anyone with the link can view', or configure an API key or a service account.");
            }

            return workbook;
        }

        private async Task<(string Title, List<GoogleSheetRef> Tabs)> ListTabsAsync(HttpClient client, string id, string? apiKey, string? bearer, CancellationToken cancellationToken)
        {
            string url = $"{_endpoints.SheetsApi}/v4/spreadsheets/{Uri.EscapeDataString(id)}?fields=properties.title,sheets.properties(sheetId,title)";
            if (apiKey != null)
            {
                url += "&key=" + Uri.EscapeDataString(apiKey);
            }

            byte[] bytes = await GetAsync(client, url, bearer, expectHtml: false, cancellationToken).ConfigureAwait(false);
            JsonObject root;
            try
            {
                root = JsonValue.Parse(Encoding.UTF8.GetString(bytes)).AsObject ?? throw new JsonFormatException("not an object");
            }
            catch (JsonFormatException exception)
            {
                throw new GoogleSourceException($"The Sheets API answer could not be read: {exception.Message}", "Try again; if it keeps happening, list the tabs in the config with their gid.");
            }

            string title = root.GetObject("properties")?.GetString("title") ?? id;
            var tabs = new List<GoogleSheetRef>();
            foreach (JsonValue item in root.GetArray("sheets") ?? Array.Empty<JsonValue>())
            {
                JsonObject? properties = item.AsObject?.GetObject("properties");
                if (properties?.GetString("title") is { } name)
                {
                    tabs.Add(new GoogleSheetRef(name, (long)(properties.GetNumber("sheetId") ?? 0)));
                }
            }

            return (title, tabs);
        }

        internal async Task<byte[]> GetAsync(HttpClient client, string url, string? bearer, bool expectHtml, CancellationToken cancellationToken, Func<HttpStatusCode, GoogleSourceException>? describe = null)
        {
            describe ??= DescribeFailure;
            for (int attempt = 1; ; attempt++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (bearer != null)
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
                }

                using HttpResponseMessage response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    if (!expectHtml && response.Content.Headers.ContentType?.MediaType == "text/html")
                    {
                        throw describe(HttpStatusCode.Unauthorized);
                    }

                    return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                }

                (string? reason, GoogleApiDisabledException? disabled) = await ReadErrorAsync(response).ConfigureAwait(false);
                if (disabled != null)
                {
                    throw disabled;
                }

                bool rateLimited = response.StatusCode == (HttpStatusCode)429 || (response.StatusCode == HttpStatusCode.Forbidden && IsRateLimitReason(reason));
                bool transient = rateLimited || response.StatusCode is HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;
                if (!transient || attempt >= Math.Max(1, _endpoints.MaxAttempts))
                {
                    throw describe(rateLimited ? (HttpStatusCode)429 : response.StatusCode);
                }

                await _endpoints.Wait(RetryDelay(response, attempt), cancellationToken).ConfigureAwait(false);
            }
        }

        private static bool IsRateLimitReason(string? reason) =>
            reason is "rateLimitExceeded" or "userRateLimitExceeded" or "RATE_LIMIT_EXCEEDED" or "RESOURCE_EXHAUSTED";

        private static readonly Random Jitter = new();

        internal static TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
        {
            TimeSpan cap = TimeSpan.FromSeconds(60);
            RetryConditionHeaderValue? retryAfter = response.Headers.RetryAfter;
            if (retryAfter?.Delta is { } delta)
            {
                return delta < cap ? delta : cap;
            }

            if (retryAfter?.Date is { } date && date - DateTimeOffset.UtcNow is { } wait && wait > TimeSpan.Zero)
            {
                return wait < cap ? wait : cap;
            }

            double jitter;
            lock (Jitter)
            {
                jitter = Jitter.NextDouble();
            }

            return TimeSpan.FromSeconds(Math.Min(Math.Pow(2, attempt - 1), 32) + jitter);
        }

        private GoogleSourceException DescribeFailure(HttpStatusCode status)
        {
            string id = source.SpreadsheetId ?? "";
            switch (status)
            {
                case HttpStatusCode.Unauthorized:
                case HttpStatusCode.Forbidden:
                    return new GoogleSourceException($"Google refused access to spreadsheet '{id}' ({(int)status}).", source.Auth.Type switch
                    {
                        GoogleAuthType.ServiceAccount => $"Share the spreadsheet with the service account {_account ?? "(client_email in the key file)"} as a viewer.",
                        GoogleAuthType.ApiKey => "An API key only reads link-shared spreadsheets: share it as 'Anyone with the link can view', and check that the Sheets API is enabled for the key.",
                        GoogleAuthType.AccessToken => "The access token is expired or lacks the drive.readonly scope.",
                        GoogleAuthType.Custom or GoogleAuthType.Command => $"Share the spreadsheet with {_account ?? "the account these credentials belong to"} as a viewer, and check that the credentials are not expired.",
                        _ => "Share the spreadsheet as 'Anyone with the link can view', or configure an API key or a service account."
                    });
                case HttpStatusCode.NotFound:
                    return new GoogleSourceException($"Google has no spreadsheet '{id}', or a listed tab gid does not exist (404).",
                        "Copy the id from the URL: docs.google.com/spreadsheets/d/<id>/edit. A tab's gid is the number after #gid= in its URL.");
                case (HttpStatusCode)429:
                    return new GoogleSourceException("Google is rate limiting requests (429).", "Wait a minute and try again.");
                default:
                    return new GoogleSourceException($"Google answered with status {(int)status}.", "Try again later. Use --offline to build from the saved snapshot.");
            }
        }

        internal sealed record ResolvedAuth(string? ApiKey, string? Bearer, string? Account);

        internal async Task<ResolvedAuth> ResolveAuthAsync(HttpClient client, CancellationToken cancellationToken)
        {
            GoogleAuthConfig auth = source.Auth;
            switch (auth.Type)
            {
                case GoogleAuthType.ApiKey:
                    string? key = auth.ApiKey ?? ReadEnvironment(auth.ApiKeyEnv);
                    if (string.IsNullOrEmpty(key))
                    {
                        throw new GoogleSourceException("No API key is configured.", "Set auth.apiKeyEnv to the name of an environment variable that holds the key (or auth.apiKey for a key you are willing to commit).");
                    }

                    return new ResolvedAuth(key, null, null);
                case GoogleAuthType.AccessToken:
                    string? token = ReadEnvironment(auth.AccessTokenEnv);
                    if (string.IsNullOrEmpty(token))
                    {
                        throw new GoogleSourceException("No access token is available.", "Set auth.accessTokenEnv to the name of an environment variable that holds an OAuth access token.");
                    }

                    return new ResolvedAuth(null, token, null);
                case GoogleAuthType.ServiceAccount:
                    return await ExchangeServiceAccountKeyAsync(client, ReadKeyFile(auth), "The service account key file", cancellationToken).ConfigureAwait(false);
                case GoogleAuthType.Custom:
                    return await FromCredentialAsync(client, await CallProviderAsync(client, auth.Provider, cancellationToken).ConfigureAwait(false),
                        $"The key from credential provider '{auth.Provider}'", cancellationToken).ConfigureAwait(false);
                case GoogleAuthType.Command:
                    return await FromCredentialAsync(client, await CredentialCommand.RunAsync(auth.Command, baseDirectory, cancellationToken).ConfigureAwait(false),
                        $"The key printed by '{auth.Command}'", cancellationToken).ConfigureAwait(false);
                default:
                    return new ResolvedAuth(null, null, null);
            }
        }

        private async Task<GoogleCredential> CallProviderAsync(HttpClient client, string? name, CancellationToken cancellationToken)
        {
            IGoogleCredentialProvider provider = GoogleCredentialProviders.Find(credentialProviders, name);
            GoogleCredential? credential;
            try
            {
                credential = await provider.GetCredentialAsync(new GoogleCredentialRequest(source, baseDirectory, Scopes, client), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (!(exception is OperationCanceledException && cancellationToken.IsCancellationRequested) && !(exception is GoogleSourceException))
            {
                throw new GoogleSourceException($"The credential provider '{name}' failed: {exception.GetType().Name}: {exception.Message}", $"Fix {provider.GetType().FullName}.GetCredentialAsync.");
            }

            return credential ?? throw new GoogleSourceException($"The credential provider '{name}' returned no credentials.", "Return GoogleCredential.None for a public spreadsheet.");
        }

        private async Task<ResolvedAuth> FromCredentialAsync(HttpClient client, GoogleCredential credential, string keyDescription, CancellationToken cancellationToken)
        {
            if (credential.ServiceAccountKeyJson != null)
            {
                return await ExchangeServiceAccountKeyAsync(client, credential.ServiceAccountKeyJson, keyDescription, cancellationToken).ConfigureAwait(false);
            }

            return new ResolvedAuth(credential.ApiKey, credential.AccessToken, credential.Account);
        }

        private static async Task<(string? Reason, GoogleApiDisabledException? Disabled)> ReadErrorAsync(HttpResponseMessage response)
        {
            try
            {
                string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                JsonObject? error = body.Length == 0 ? null : JsonValue.Parse(body).AsObject?.GetObject("error");
                if (error == null)
                {
                    return (null, null);
                }

                string? reason = null;
                foreach (JsonValue item in error.GetArray("details") ?? Array.Empty<JsonValue>())
                {
                    JsonObject? detail = item.AsObject;
                    reason ??= detail?.GetString("reason");
                    if (detail?.GetString("reason") == "SERVICE_DISABLED")
                    {
                        JsonObject? metadata = detail.GetObject("metadata");
                        string service = metadata?.GetString("serviceTitle") ?? metadata?.GetString("service") ?? "Google API";
                        string? project = metadata?.GetString("consumer")?.Replace("projects/", "");
                        return (reason, new GoogleApiDisabledException(service.EndsWith("API", StringComparison.Ordinal) ? service : service + " API", project, metadata?.GetString("activationUrl")));
                    }
                }

                foreach (JsonValue item in error.GetArray("errors") ?? Array.Empty<JsonValue>())
                {
                    reason ??= item.AsObject?.GetString("reason");
                }

                return (reason, null);
            }
            catch (Exception exception) when (exception is JsonFormatException or InvalidOperationException or HttpRequestException)
            {
                return (null, null);
            }
        }

        internal string? ServiceAccountEmail()
        {
            try
            {
                string? keyFile = source.Auth.KeyFile;
                if (source.Auth.Type != GoogleAuthType.ServiceAccount || string.IsNullOrEmpty(keyFile))
                {
                    return null;
                }

                return JsonValue.Parse(File.ReadAllText(Path.GetFullPath(Path.Combine(baseDirectory, keyFile!)))).AsObject?.GetString("client_email");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonFormatException)
            {
                return null;
            }
        }

        private static string? ReadEnvironment(string? name)
        {
            return string.IsNullOrEmpty(name) ? null : Environment.GetEnvironmentVariable(name);
        }

        private string ReadKeyFile(GoogleAuthConfig auth)
        {
            if (string.IsNullOrEmpty(auth.KeyFile))
            {
                throw new GoogleSourceException("No service account key file is configured.", "Set auth.keyFile to the JSON key file downloaded from Google Cloud. Keep that file out of version control.");
            }

            string keyPath = Path.GetFullPath(Path.Combine(baseDirectory, auth.KeyFile));
            if (!File.Exists(keyPath))
            {
                throw new GoogleSourceException($"The service account key file '{keyPath}' does not exist.", "Fix auth.keyFile; the path is relative to the config file.");
            }

            return File.ReadAllText(keyPath);
        }

        private static async Task<ResolvedAuth> ExchangeServiceAccountKeyAsync(HttpClient client, string keyJson, string keyDescription, CancellationToken cancellationToken)
        {
            string email;
            string tokenUri;
            RSAParameters key;
            try
            {
                JsonObject json = JsonValue.Parse(keyJson).AsObject ?? throw new JsonFormatException("not an object");
                email = json.GetString("client_email") ?? throw new JsonFormatException("client_email is missing");
                tokenUri = json.GetString("token_uri") ?? "https://oauth2.googleapis.com/token";
                key = Pkcs8.ReadRsaPrivateKey(json.GetString("private_key") ?? throw new JsonFormatException("private_key is missing"));
            }
            catch (Exception exception) when (exception is JsonFormatException or FormatException)
            {
                throw new GoogleSourceException($"{keyDescription} could not be read as a service account key: {exception.Message}", "Download a new JSON key for the service account from Google Cloud.");
            }

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string header = Base64Url(Encoding.UTF8.GetBytes("{\"alg\":\"RS256\",\"typ\":\"JWT\"}"));
            string claims = Base64Url(Encoding.UTF8.GetBytes(JsonValue.Object()
                .Set("iss", email).Set("scope", Scopes).Set("aud", tokenUri).Set("iat", now).Set("exp", now + 3600).ToValue().ToJson(indented: false)));
            string signingInput = header + "." + claims;
            string signature;
            using (RSA rsa = RSA.Create())
            {
                rsa.ImportParameters(key);
                signature = Base64Url(rsa.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
            }

            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                ["assertion"] = signingInput + "." + signature
            });
            using HttpResponseMessage response = await client.PostAsync(tokenUri, content, cancellationToken).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            string? token = null;
            try
            {
                token = JsonValue.Parse(body).AsObject?.GetString("access_token");
            }
            catch (JsonFormatException)
            {
            }

            if (!response.IsSuccessStatusCode || token == null)
            {
                throw new GoogleSourceException($"Google did not issue a token for the service account {email} ({(int)response.StatusCode}).",
                    "Check that the key was not deleted or disabled in Google Cloud, and that this machine's clock is correct.");
            }

            return new ResolvedAuth(null, token, email);
        }

        private static string Base64Url(byte[] bytes)
        {
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private void WriteSnapshot(string workbookName, IReadOnlyList<string> names, IReadOnlyList<string> texts, bool complete = true)
        {
            if (snapshotDirectory != null)
            {
                WriteSnapshot(snapshotDirectory, workbookName, names, texts, complete);
            }
        }

        internal static void WriteSnapshot(string snapshotDirectory, string workbookName, IReadOnlyList<string> names, IReadOnlyList<string> texts, bool complete)
        {
            Directory.CreateDirectory(snapshotDirectory);
            SnapshotIndex previous = SnapshotIndex.Read(snapshotDirectory);
            var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var encoding = new UTF8Encoding(false);

            for (int index = 0; index < names.Count; index++)
            {
                string fileName = SafeFileName(names[index]) + ".csv";
                kept.Add(fileName);
                string path = Path.Combine(snapshotDirectory, fileName);
                byte[] bytes = encoding.GetBytes(texts[index].Replace("\r\n", "\n"));
                if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
                {
                    File.WriteAllBytes(path, bytes);
                }
            }

            var owned = new SortedSet<string>(kept, StringComparer.OrdinalIgnoreCase);
            foreach (string file in previous.Files)
            {
                if (kept.Contains(file))
                {
                    continue;
                }

                if (complete)
                {
                    File.Delete(Path.Combine(snapshotDirectory, file));
                }
                else
                {
                    // A partial fetch leaves the other tabs as they were, and they stay the snapshot's own.
                    owned.Add(file);
                }
            }

            SnapshotIndex.Write(snapshotDirectory, workbookName, owned);
        }

        internal static string SafeFileName(string name)
        {
            if (name.Length == 0 || name == "." || name == "..")
            {
                return "_" + name;
            }

            var builder = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                builder.Append(c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' ? '_' : c);
            }

            return builder.ToString();
        }
    }

    internal static class Pkcs8
    {
        public static RSAParameters ReadRsaPrivateKey(string pem)
        {
            var base64 = new StringBuilder();
            foreach (string line in pem.Replace("\\n", "\n").Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.Length > 0 && !trimmed.StartsWith("-----", StringComparison.Ordinal))
                {
                    base64.Append(trimmed);
                }
            }

            byte[] der = Convert.FromBase64String(base64.ToString());
            try
            {
                var outer = new DerReader(der).ReadSequence();
                outer.ReadInteger();
                outer.ReadSequence();
                var key = new DerReader(outer.ReadOctetString()).ReadSequence();
                key.ReadInteger();
                byte[] modulus = key.ReadInteger();
                byte[] exponent = key.ReadInteger();
                int half = (modulus.Length + 1) / 2;
                return new RSAParameters
                {
                    Modulus = modulus,
                    Exponent = exponent,
                    D = Pad(key.ReadInteger(), modulus.Length),
                    P = Pad(key.ReadInteger(), half),
                    Q = Pad(key.ReadInteger(), half),
                    DP = Pad(key.ReadInteger(), half),
                    DQ = Pad(key.ReadInteger(), half),
                    InverseQ = Pad(key.ReadInteger(), half)
                };
            }
            catch (IndexOutOfRangeException)
            {
                throw new FormatException("the private key is not PKCS#8 RSA");
            }
        }

        private static byte[] Pad(byte[] value, int length)
        {
            if (value.Length >= length)
            {
                return value;
            }

            byte[] padded = new byte[length];
            Buffer.BlockCopy(value, 0, padded, length - value.Length, value.Length);
            return padded;
        }

        private sealed class DerReader
        {
            private readonly byte[] data;

            public DerReader(byte[] data, int start = 0, int end = -1)
            {
                this.data = data;
                _end = end < 0 ? data.Length : end;
                _position = start;
            }

            private readonly int _end;
            private int _position;

            public DerReader ReadSequence()
            {
                (int start, int length) = ReadHeader(0x30);
                _position = start + length;
                return new DerReader(data, start, start + length);
            }

            public byte[] ReadOctetString()
            {
                (int start, int length) = ReadHeader(0x04);
                _position = start + length;
                return data.AsSpan(start, length).ToArray();
            }

            public byte[] ReadInteger()
            {
                (int start, int length) = ReadHeader(0x02);
                _position = start + length;
                while (length > 1 && data[start] == 0)
                {
                    start++;
                    length--;
                }

                return data.AsSpan(start, length).ToArray();
            }

            private (int Start, int Length) ReadHeader(byte expectedTag)
            {
                if (_position >= _end || data[_position] != expectedTag)
                {
                    throw new FormatException("the private key is not PKCS#8 RSA");
                }

                _position++;
                int length = data[_position++];
                if ((length & 0x80) != 0)
                {
                    int count = length & 0x7F;
                    length = 0;
                    for (int index = 0; index < count; index++)
                    {
                        length = (length << 8) | data[_position++];
                    }
                }

                if (_position + length > _end)
                {
                    throw new FormatException("the private key is truncated");
                }

                return (_position, length);
            }
        }
    }

    internal sealed class SnapshotIndex
    {
        public const string FileName = ".holysheet-snapshot";

        private const string Header = "holysheet-snapshot 1";

        private SnapshotIndex(string? workbook, IReadOnlyCollection<string> files)
        {
            Workbook = workbook;
            Files = files;
        }

        /// <summary>The workbook name the last online build used. Null for a snapshot written before the index existed.</summary>
        public string? Workbook { get; }

        public IReadOnlyCollection<string> Files { get; }

        public static SnapshotIndex Read(string directory)
        {
            string path = Path.Combine(directory, FileName);
            var files = new List<string>();
            string? workbook = null;
            try
            {
                if (!File.Exists(path))
                {
                    return new SnapshotIndex(null, files);
                }

                string[] lines = File.ReadAllText(path, Encoding.UTF8).Split('\n');
                if (lines.Length == 0 || lines[0].TrimEnd('\r') != Header)
                {
                    return new SnapshotIndex(null, files);
                }

                foreach (string raw in lines.Skip(1))
                {
                    string line = raw.TrimEnd('\r');
                    int tab = line.IndexOf('\t');
                    if (tab < 0)
                    {
                        continue;
                    }

                    string key = line.Substring(0, tab);
                    string value = Unescape(line.Substring(tab + 1));
                    if (key == "workbook")
                    {
                        workbook = value;
                    }
                    else if (key == "file" && IsOwnFileName(value))
                    {
                        files.Add(value);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Unreadable: own nothing, so nothing is deleted.
            }

            return new SnapshotIndex(workbook, files);
        }

        public static void Write(string directory, string workbook, IEnumerable<string> files)
        {
            var text = new StringBuilder();
            text.Append(Header).Append('\n');
            text.Append("# Written by HolySheet. Only the files listed here are replaced or removed by the next online build.\n");
            text.Append("workbook\t").Append(Escape(workbook)).Append('\n');
            foreach (string file in files)
            {
                text.Append("file\t").Append(Escape(file)).Append('\n');
            }

            string path = Path.Combine(directory, FileName);
            string content = text.ToString();
            if (!File.Exists(path) || File.ReadAllText(path, Encoding.UTF8) != content)
            {
                File.WriteAllText(path, content, new UTF8Encoding(false));
            }
        }

        private static bool IsOwnFileName(string name) =>
            name.Length > 0 && name == Path.GetFileName(name) && name != "." && name != ".." && name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);

        private static string Escape(string text) => text.Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\n", "\\n").Replace("\r", "\\r");

        private static string Unescape(string text)
        {
            var builder = new StringBuilder(text.Length);
            for (int index = 0; index < text.Length; index++)
            {
                char c = text[index];
                if (c != '\\' || index + 1 == text.Length)
                {
                    builder.Append(c);
                    continue;
                }

                char next = text[++index];
                builder.Append(next switch { 't' => '\t', 'n' => '\n', 'r' => '\r', _ => next });
            }

            return builder.ToString();
        }
    }
}
