using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using UGS.Core.Config;
using UGS.Core.Csv;

namespace UGS.Core.Sources;

/// <summary>How the Google services are reached: their addresses and the retry policy. Replaceable so tests can point the sources at a stand-in.</summary>
public sealed class GoogleEndpoints
{
    public string SheetsApi { get; set; } = "https://sheets.googleapis.com";

    public string Docs { get; set; } = "https://docs.google.com";

    public string DriveApi { get; set; } = "https://www.googleapis.com";

    /// <summary>How many times a request is tried when Google answers "too many requests" or with a server error.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>How to wait between those tries. Replaceable so tests do not wait.</summary>
    public Func<TimeSpan, CancellationToken, Task> Wait { get; set; } = Task.Delay;
}

internal class GoogleSourceException(string message, string hint) : Exception(message)
{
    public string Hint { get; } = hint;
}

/// <summary>Google refused because an API (Sheets or Drive) is not enabled in the Cloud project of the credentials.</summary>
internal sealed class GoogleApiDisabledException(string api, string? project, string? enableUrl)
    : GoogleSourceException($"The {api} is not enabled in Google Cloud project {project ?? "of these credentials"}.",
        $"Enable it at {enableUrl ?? "console.cloud.google.com/apis/library"} and try again a few minutes later.")
{
    public string Api { get; } = api;
}

/// <summary>
/// Reads a Google spreadsheet, as the text the author sees in each cell.
/// <list type="bullet">
/// <item>With credentials: one Sheets API request lists the tabs, one more (values:batchGet) brings all of them. Before that,
/// one Drive request asks for the file's version; if it is the version downloaded last time, nothing else is requested
/// and the tabs come from the local copy.</item>
/// <item>Without credentials: the tabs listed in the config are fetched as CSV exports; with no list, the whole spreadsheet
/// comes as one xlsx export.</item>
/// <item>If the Sheets API is disabled for the credentials' project, the xlsx export is used, with a warning.</item>
/// </list>
/// Requests Google answers with "too many requests" or a server error are retried, waiting longer each time.
/// </summary>
/// <remarks>
/// With a selection, only the selected tabs are downloaded, and the snapshot gets those tabs without losing the others.
/// </remarks>
public sealed class GoogleSheetsSource(SourceConfig source, string baseDirectory, ICsvParser parser, string? snapshotDirectory = null, HttpMessageHandler? httpHandler = null, GoogleEndpoints? endpoints = null, SheetSelection? only = null, string? remoteCacheDirectory = null) : IWorkbookSource
{
    private const int MaxConcurrentDownloads = 4;
    private const string Scopes = "https://www.googleapis.com/auth/drive.readonly https://www.googleapis.com/auth/spreadsheets.readonly";

    private readonly GoogleEndpoints _endpoints = endpoints ?? new GoogleEndpoints();

    public string Description => "google:" + source.SpreadsheetId;

    /// <summary>Credentials already obtained by the caller (a Drive folder reading its spreadsheets), so they are not fetched again per spreadsheet.</summary>
    internal (string? ApiKey, string? Bearer)? PresetAuth { get; set; }

    public IReadOnlyList<string> WatchPaths => [];

    /// <summary>The Drive version of the file when the caller already knows it (a folder listing reports it), to skip asking.</summary>
    internal long? KnownVersion { get; set; }

    public async Task<IReadOnlyList<Workbook>> LoadAsync(DiagnosticBag diagnostics, CancellationToken cancellationToken)
    {
        string id = source.SpreadsheetId ?? "";
        string location = source.Name ?? Description;
        using HttpClient client = httpHandler == null ? new HttpClient() : new HttpClient(httpHandler, disposeHandler: false);
        client.Timeout = TimeSpan.FromSeconds(100);
        try
        {
            (string? apiKey, string? bearer) = PresetAuth ?? await ResolveAuthAsync(client, cancellationToken).ConfigureAwait(false);
            string? title = source.Name;
            if (only != null && title != null && !only.Touches(title))
            {
                return [];
            }

            bool hasCredentials = apiKey != null || bearer != null;

            // 1. Unchanged since the last download? Drive reports the file's version for one cheap request (Drive quota,
            //    not Sheets quota); the same version means the tabs can come from the local copy.
            long? version = KnownVersion ?? (hasCredentials && remoteCacheDirectory != null ? await TryGetVersionAsync(client, id, apiKey, bearer, cancellationToken).ConfigureAwait(false) : null);
            if (version != null && RemoteCopy.TryLoad(remoteCacheDirectory, id, version.Value) is { } copy)
            {
                string name = title ?? copy.Title;
                if (only != null && !only.Touches(name))
                {
                    return [];
                }

                List<(string Name, string Text)> kept = copy.Tabs.Where(tab => only == null || only.Includes(name, tab.Name)).ToList();
                WriteSnapshot(kept.Select(tab => tab.Name).ToList(), kept.Select(tab => tab.Text).ToList(), complete: only == null);
                return [Linked(ToWorkbook(name, kept, diagnostics))];
            }

            // 2. Download.
            string workbookName;
            List<(string Name, string Text)> fetched;
            bool complete = only == null;
            if (!hasCredentials)
            {
                workbookName = title ?? id;
                if (source.Sheets.Count == 0)
                {
                    // Without credentials the tabs cannot be listed; the xlsx export carries them all in one request.
                    fetched = await FetchXlsxExportAsync(client, id, workbookName, bearer: null, cancellationToken).ConfigureAwait(false);
                    complete = true;
                }
                else
                {
                    List<GoogleSheetRef> wanted = source.Sheets.Where(tab => only == null || only.Includes(workbookName, tab.Name)).ToList();
                    if (wanted.Count == 0)
                    {
                        return [];
                    }

                    fetched = await FetchCsvExportAsync(client, id, wanted, bearer, cancellationToken).ConfigureAwait(false);
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
                        return [];
                    }

                    List<string> wanted = tabs.Select(tab => tab.Name).Where(name => only == null || only.Includes(workbookName, name)).ToList();
                    if (wanted.Count == 0)
                    {
                        return [];
                    }

                    // Every tab in one Sheets API request (values:batchGet), instead of one export request per tab.
                    fetched = await FetchBatchAsync(client, id, wanted, apiKey, bearer, cancellationToken).ConfigureAwait(false);
                }
                catch (GoogleApiDisabledException disabled) when (disabled.Api.Contains("Sheets"))
                {
                    // The xlsx export does not go through the Sheets API and carries every tab.
                    diagnostics.AddWarning(location, disabled.Message + " The spreadsheet was read as one xlsx export instead, so every tab is downloaded.", disabled.Hint);
                    workbookName = title ?? id;
                    fetched = await FetchXlsxExportAsync(client, id, workbookName, bearer, cancellationToken).ConfigureAwait(false);
                    complete = true;
                }
            }

            // A partial fetch saves the tabs it fetched and leaves the others as they were.
            WriteSnapshot(fetched.Select(tab => tab.Name).ToList(), fetched.Select(tab => tab.Text).ToList(), complete);
            if (version != null)
            {
                if (complete)
                {
                    RemoteCopy.Save(remoteCacheDirectory, id, version.Value, workbookName, fetched);
                }
                else
                {
                    // Some tabs are now newer than the copy: it no longer matches any single version.
                    RemoteCopy.Invalidate(remoteCacheDirectory, id);
                }
            }

            return [Linked(ToWorkbook(workbookName, fetched, diagnostics))];
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

        return [];
    }

    // Every path ends in CSV text per tab, the same text the snapshot keeps, so online, cached and offline builds agree.
    private Workbook ToWorkbook(string workbookName, IReadOnlyList<(string Name, string Text)> tabs, DiagnosticBag diagnostics)
    {
        var sheets = new List<Sheet>(tabs.Count);
        foreach ((string name, string text) in tabs)
        {
            try
            {
                sheets.Add(Sheet.FromGrid(workbookName, name, parser.Parse(text)));
            }
            catch (CsvFormatException exception)
            {
                diagnostics.AddError($"{workbookName} / {name}", $"The CSV that Google returned could not be read: {exception.Message}");
            }
        }

        return new Workbook(workbookName, sheets);
    }

    /// <summary>The file's Drive version, or null when Drive cannot tell (no access, API disabled): then the file is simply downloaded.</summary>
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

    /// <summary>Drive writes the version as a string (it is a 64-bit number).</summary>
    internal static long? ParseVersion(JsonValue? value)
    {
        if (value?.AsString is { } text && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
        {
            return parsed;
        }

        return value?.AsNumber is { } number ? (long)number : null;
    }

    // Ranges are sent in groups so the request line stays short even for spreadsheets with hundreds of tabs.
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
                // A range that is only a sheet name means the whole sheet; quotes keep names with spaces or digits intact.
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
                // Rows and cells at the end that are empty are left out by Google; reading past them gives blanks anyway.
                var rows = new List<IReadOnlyList<string>>();
                foreach (JsonValue row in ranges[index].AsObject?.GetArray("values") ?? [])
                {
                    rows.Add((row.AsArray ?? []).Select(CellText).ToList());
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

    private async Task<List<(string Name, string Text)>> FetchCsvExportAsync(HttpClient client, string id, List<GoogleSheetRef> tabs, string? bearer, CancellationToken cancellationToken)
    {
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
                        string url = $"{_endpoints.Docs}/spreadsheets/d/{Uri.EscapeDataString(id)}/export?format=csv&gid={tabs[tabIndex].Gid.ToString(CultureInfo.InvariantCulture)}";
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

        return tabs.Select((tab, index) => (tab.Name, texts[index])).ToList();
    }

    private async Task<List<(string Name, string Text)>> FetchXlsxExportAsync(HttpClient client, string id, string workbookName, string? bearer, CancellationToken cancellationToken)
    {
        Workbook workbook = await LoadViaXlsxExportAsync(client, id, workbookName, cancellationToken, bearer).ConfigureAwait(false);
        return workbook.Sheets.Select(sheet => (sheet.Name, CsvWriter.Write(sheet.ToGrid()))).ToList();
    }

    /// <summary>The address that opens a spreadsheet in a browser.</summary>
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
        foreach (JsonValue item in root.GetArray("sheets") ?? [])
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
                // A spreadsheet that is not shared answers the export URL with a sign-in page and status 200.
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

            // Drive reports its rate limits as 403 with a reason; the Sheets API uses 429.
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

    // Google's advice: wait 1, 2, 4, 8 … seconds plus up to one second at random, at most 32 seconds; or as long as
    // the answer's Retry-After header says.
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
                    GoogleAuthType.ServiceAccount => "Share the spreadsheet with the service account's e-mail address (client_email in the key file) as a viewer.",
                    GoogleAuthType.ApiKey => "An API key only reads link-shared spreadsheets: share it as 'Anyone with the link can view', and check that the Sheets API is enabled for the key.",
                    GoogleAuthType.AccessToken => "The access token is expired or lacks the drive.readonly scope.",
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

    internal async Task<(string? ApiKey, string? Bearer)> ResolveAuthAsync(HttpClient client, CancellationToken cancellationToken)
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

                return (key, null);
            case GoogleAuthType.AccessToken:
                string? token = ReadEnvironment(auth.AccessTokenEnv);
                if (string.IsNullOrEmpty(token))
                {
                    throw new GoogleSourceException("No access token is available.", "Set auth.accessTokenEnv to the name of an environment variable that holds an OAuth access token.");
                }

                return (null, token);
            case GoogleAuthType.ServiceAccount:
                return (null, await FetchServiceAccountTokenAsync(client, auth, cancellationToken).ConfigureAwait(false));
            default:
                return (null, null);
        }
    }

    // Google explains a 403 in the body. "SERVICE_DISABLED" means the API is off in the credentials' Cloud project,
    // which no amount of sharing fixes, so it gets its own message with the link Google gives to turn it on.
    // The reason Google gives for a refusal (error.errors[].reason or error.details[].reason), and the disabled API if that was it.
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
            foreach (JsonValue item in error.GetArray("details") ?? [])
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

            foreach (JsonValue item in error.GetArray("errors") ?? [])
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

    /// <summary>The service account's e-mail address from its key file, for messages that say whom to share with. Null when it cannot be read.</summary>
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

    private async Task<string> FetchServiceAccountTokenAsync(HttpClient client, GoogleAuthConfig auth, CancellationToken cancellationToken)
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

        string email;
        string tokenUri;
        RSAParameters key;
        try
        {
            JsonObject json = JsonValue.Parse(File.ReadAllText(keyPath)).AsObject ?? throw new JsonFormatException("not an object");
            email = json.GetString("client_email") ?? throw new JsonFormatException("client_email is missing");
            tokenUri = json.GetString("token_uri") ?? "https://oauth2.googleapis.com/token";
            key = Pkcs8.ReadRsaPrivateKey(json.GetString("private_key") ?? throw new JsonFormatException("private_key is missing"));
        }
        catch (Exception exception) when (exception is JsonFormatException or FormatException)
        {
            throw new GoogleSourceException($"The service account key file could not be read: {exception.Message}", "Download a new JSON key for the service account from Google Cloud.");
        }

        // A self-signed JWT is exchanged for a short-lived access token (OAuth 2.0 JWT bearer flow).
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
            throw new GoogleSourceException($"Google did not issue a token for the service account ({(int)response.StatusCode}).",
                "Check that the key was not deleted or disabled in Google Cloud, and that this machine's clock is correct.");
        }

        return token;
    }

    private static string Base64Url(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private void WriteSnapshot(IReadOnlyList<string> names, IReadOnlyList<string> texts, bool complete = true)
    {
        if (snapshotDirectory != null)
        {
            WriteSnapshot(snapshotDirectory, names, texts, complete);
        }
    }

    // Saves what was fetched, one CSV per tab, and, when the fetch was complete, removes CSV files of tabs that no longer exist.
    internal static void WriteSnapshot(string snapshotDirectory, IReadOnlyList<string> names, IReadOnlyList<string> texts, bool complete)
    {
        Directory.CreateDirectory(snapshotDirectory);
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

        foreach (string file in complete ? Directory.GetFiles(snapshotDirectory, "*.csv", SearchOption.TopDirectoryOnly) : [])
        {
            if (!kept.Contains(Path.GetFileName(file)))
            {
                File.Delete(file);
            }
        }
    }

    internal static string SafeFileName(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            builder.Append(c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' ? '_' : c);
        }

        return builder.ToString();
    }
}

/// <summary>
/// Reads an RSA private key from PKCS#8 PEM text, the form Google puts in service account key files.
/// Written out here because the platform import methods are missing on some Unity runtimes.
/// </summary>
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
            // PrivateKeyInfo ::= SEQUENCE { version, algorithm SEQUENCE, privateKey OCTET STRING }
            var outer = new DerReader(der).ReadSequence();
            outer.ReadInteger();
            outer.ReadSequence();
            // RSAPrivateKey ::= SEQUENCE { version, n, e, d, p, q, dP, dQ, qInv }
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

    private sealed class DerReader(byte[] data, int start = 0, int end = -1)
    {
        private readonly int _end = end < 0 ? data.Length : end;
        private int _position = start;

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

        // Returns the magnitude without the sign byte.
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
