#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HolySheet.Core.Config;
using HolySheet.Core.Csv;

namespace HolySheet.Core.Sources
{
    public sealed class GoogleFolderSource : IWorkbookSource
    {
        private readonly SourceConfig source;
        private readonly string baseDirectory;
        private readonly ICsvParser parser;
        private readonly string? snapshotDirectory;
        private readonly HttpMessageHandler? httpHandler;
        private readonly SheetSelection? only;
        private bool offline;
        private readonly string? remoteCacheDirectory;
        private readonly IReadOnlyList<IGoogleCredentialProvider>? credentialProviders;

        public GoogleFolderSource(SourceConfig source, string baseDirectory, ICsvParser parser, string? snapshotDirectory = null, HttpMessageHandler? httpHandler = null, GoogleEndpoints? endpoints = null, SheetSelection? only = null, bool offline = false, string? remoteCacheDirectory = null, IReadOnlyList<IGoogleCredentialProvider>? credentialProviders = null)
        {
            this.credentialProviders = credentialProviders;
            this.source = source;
            this.baseDirectory = baseDirectory;
            this.parser = parser;
            this.snapshotDirectory = snapshotDirectory;
            this.httpHandler = httpHandler;
            this.only = only;
            this.offline = offline;
            this.remoteCacheDirectory = remoteCacheDirectory;
            _endpoints = endpoints ?? new GoogleEndpoints();
        }

        internal const string SpreadsheetMimeType = "application/vnd.google-apps.spreadsheet";
        internal const string XlsxMimeType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
        internal const string IndexFileName = "workbooks.index";
        private const int MaxConcurrentFiles = 2;

        private readonly GoogleEndpoints _endpoints;

        internal bool RefreshGoogle { get; init; }

        public string Description => "google-folder:" + source.FolderId;

        public IReadOnlyList<string> WatchPaths => offline && snapshotDirectory != null ? new string[] { snapshotDirectory } : Array.Empty<string>();

        public async Task<IReadOnlyList<Workbook>> LoadAsync(DiagnosticBag diagnostics, CancellationToken cancellationToken)
        {
            if (offline && snapshotDirectory != null)
            {
                return await LoadSnapshotAsync(diagnostics, cancellationToken).ConfigureAwait(false);
            }

            string folderId = source.FolderId ?? "";
            string location = source.Name ?? "Drive folder " + folderId;
            var helper = new GoogleSheetsSource(source, baseDirectory, parser, null, httpHandler, _endpoints, credentialProviders: credentialProviders);
            using HttpClient client = httpHandler == null ? new HttpClient() : new HttpClient(httpHandler, disposeHandler: false);
            client.Timeout = TimeSpan.FromSeconds(100);
            try
            {
                GoogleSheetsSource.ResolvedAuth auth = await helper.ResolveAuthAsync(client, cancellationToken).ConfigureAwait(false);
                (string? apiKey, string? bearer, _) = auth;
                _account = auth.Account;
                string folderName = await GetFolderNameAsync(helper, client, folderId, apiKey, bearer, cancellationToken).ConfigureAwait(false);
                location = source.Name ?? folderName;
                List<DriveFile> files = await ListAsync(helper, client, folderId, apiKey, bearer, cancellationToken).ConfigureAwait(false);
                if (files.Count == 0)
                {
                    diagnostics.AddWarning(location, $"The Drive folder '{folderName}' has no Google spreadsheet or .xlsx file directly in it.",
                        "Subfolders are not read: add each as a source of its own. Files whose name starts with # or ~ are skipped.");
                }

                var byName = new Dictionary<string, DriveFile>(StringComparer.Ordinal);
                foreach (DriveFile file in files)
                {
                    if (byName.TryGetValue(file.Name, out DriveFile? other))
                    {
                        diagnostics.AddError($"{location} / {file.Name}", $"Two files in the folder are named '{file.Name}' (ids {other.Id} and {file.Id}).",
                            "Rename one of them: the file name is the workbook name, and it must be unique.");
                    }
                    else
                    {
                        byName.Add(file.Name, file);
                    }
                }

                List<DriveFile> wanted = byName.Values.Where(file => only == null || only.Touches(file.Name)).OrderBy(file => file.Name, StringComparer.Ordinal).ToList();
                var results = new IReadOnlyList<Workbook>[wanted.Count];
                var bags = new DiagnosticBag[wanted.Count];
                using (var gate = new SemaphoreSlim(MaxConcurrentFiles))
                {
                    var loads = new Task[wanted.Count];
                    for (int index = 0; index < wanted.Count; index++)
                    {
                        int fileIndex = index;
                        bags[index] = new DiagnosticBag();
                        loads[index] = Task.Run(async () =>
                        {
                            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                            try
                            {
                                results[fileIndex] = await LoadFileAsync(helper, client, wanted[fileIndex], auth, bags[fileIndex], cancellationToken).ConfigureAwait(false);
                            }
                            finally
                            {
                                gate.Release();
                            }
                        }, cancellationToken);
                    }

                    await Task.WhenAll(loads).ConfigureAwait(false);
                }

                var workbooks = new List<Workbook>();
                for (int index = 0; index < wanted.Count; index++)
                {
                    diagnostics.AddRange(bags[index]);
                    workbooks.AddRange(results[index]);
                }

                WriteIndex(byName.Values.Select(file => (file.Name, (string?)Link(file))), complete: only == null);
                return workbooks;
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

        private sealed record DriveFile(string Id, string Name, string MimeType, long? Version);

        private static string Link(DriveFile file) => file.MimeType == SpreadsheetMimeType
            ? GoogleSheetsSource.SpreadsheetLink(file.Id)
            : "https://drive.google.com/file/d/" + Uri.EscapeDataString(file.Id) + "/view";

        public static string FolderLink(string id) => "https://drive.google.com/drive/folders/" + Uri.EscapeDataString(id);

        internal const string FolderMimeType = "application/vnd.google-apps.folder";

        private string? _account;

        private async Task<string> GetFolderNameAsync(GoogleSheetsSource helper, HttpClient client, string folderId, string? apiKey, string? bearer, CancellationToken cancellationToken)
        {
            string url = $"{_endpoints.DriveApi}/drive/v3/files/{Uri.EscapeDataString(folderId)}?fields={Uri.EscapeDataString("id,name,mimeType")}&supportsAllDrives=true"
                + (apiKey == null ? "" : "&key=" + Uri.EscapeDataString(apiKey));
            byte[] bytes = await helper.GetAsync(client, url, bearer, expectHtml: false, cancellationToken, DescribeFolderFailure).ConfigureAwait(false);
            JsonObject? folder;
            try
            {
                folder = JsonValue.Parse(Encoding.UTF8.GetString(bytes)).AsObject;
            }
            catch (JsonFormatException exception)
            {
                throw new GoogleSourceException($"The Drive API answer could not be read: {exception.Message}", "Try again later.");
            }

            if (folder?.GetString("mimeType") is { } mimeType && mimeType != FolderMimeType)
            {
                throw new GoogleSourceException($"'{folderId}' is a file ({folder.GetString("name")}), not a folder.",
                    mimeType == SpreadsheetMimeType ? "Add a spreadsheet as a Google Sheets source instead." : "Copy the id from a folder URL: drive.google.com/drive/folders/<id>.");
            }

            return folder?.GetString("name") ?? folderId;
        }

        private async Task<List<DriveFile>> ListAsync(GoogleSheetsSource helper, HttpClient client, string folderId, string? apiKey, string? bearer, CancellationToken cancellationToken)
        {
            string query = $"'{folderId.Replace("\\", "\\\\").Replace("'", "\\'")}' in parents and trashed=false and (mimeType='{SpreadsheetMimeType}' or mimeType='{XlsxMimeType}')";
            var files = new List<DriveFile>();
            string? pageToken = null;
            do
            {
                string url = $"{_endpoints.DriveApi}/drive/v3/files?q={Uri.EscapeDataString(query)}&fields={Uri.EscapeDataString("nextPageToken,files(id,name,mimeType,version)")}"
                    + "&pageSize=1000&supportsAllDrives=true&includeItemsFromAllDrives=true"
                    + (pageToken == null ? "" : "&pageToken=" + Uri.EscapeDataString(pageToken))
                    + (apiKey == null ? "" : "&key=" + Uri.EscapeDataString(apiKey));
                byte[] bytes = await helper.GetAsync(client, url, bearer, expectHtml: false, cancellationToken, DescribeFolderFailure).ConfigureAwait(false);
                JsonObject root;
                try
                {
                    root = JsonValue.Parse(Encoding.UTF8.GetString(bytes)).AsObject ?? throw new JsonFormatException("not an object");
                }
                catch (JsonFormatException exception)
                {
                    throw new GoogleSourceException($"The Drive API answer could not be read: {exception.Message}", "Try again later.");
                }

                foreach (JsonValue item in root.GetArray("files") ?? Array.Empty<JsonValue>())
                {
                    JsonObject? file = item.AsObject;
                    if (file?.GetString("id") is { } id && file.GetString("name") is { } name && file.GetString("mimeType") is { } mimeType)
                    {
                        if (!name.StartsWith("#", StringComparison.Ordinal) && !name.StartsWith("~", StringComparison.Ordinal))
                        {
                            files.Add(new DriveFile(id, name, mimeType, GoogleSheetsSource.ParseVersion(file["version"])));
                        }
                    }
                }

                pageToken = root.GetString("nextPageToken");
            }
            while (!string.IsNullOrEmpty(pageToken));

            return files;
        }

        private async Task<IReadOnlyList<Workbook>> LoadFileAsync(GoogleSheetsSource helper, HttpClient client, DriveFile file, GoogleSheetsSource.ResolvedAuth auth, DiagnosticBag diagnostics, CancellationToken cancellationToken)
        {
            (string? apiKey, string? bearer, _) = auth;
            string? workbookSnapshot = snapshotDirectory == null ? null : Path.Combine(snapshotDirectory, GoogleSheetsSource.SafeFileName(file.Name));
            if (file.MimeType == SpreadsheetMimeType)
            {
                var spreadsheet = new SourceConfig { Type = SourceType.Google, SpreadsheetId = file.Id, Name = file.Name, Auth = source.Auth };
                var reader = new GoogleSheetsSource(spreadsheet, baseDirectory, parser, workbookSnapshot, httpHandler, _endpoints, only, remoteCacheDirectory, credentialProviders)
                {
                    PresetAuth = auth,
                    RefreshGoogle = RefreshGoogle,
                    KnownVersion = file.Version
                };
                return await reader.LoadAsync(diagnostics, cancellationToken).ConfigureAwait(false);
            }

            var origin = new WorkbookOrigin(SourceType.GoogleFolder, source.FolderId ?? "", Link(file));
            if (!RefreshGoogle && file.Version is { } known && RemoteCopy.TryLoad(remoteCacheDirectory, file.Id, known) is { } copy)
            {
                if (workbookSnapshot != null)
                {
                    GoogleSheetsSource.WriteSnapshot(workbookSnapshot, file.Name, copy.Tabs.Select(tab => tab.Name).ToList(), copy.Tabs.Select(tab => tab.Text).ToList(), complete: true);
                }

                var cachedSheets = new List<Sheet>();
                foreach ((string name, string text) in copy.Tabs)
                {
                    cachedSheets.Add(Sheet.FromGrid(file.Name, name, parser.Parse(text)));
                }

                return new[] { new Workbook(file.Name, cachedSheets, origin) };
            }

            string url = $"{_endpoints.DriveApi}/drive/v3/files/{Uri.EscapeDataString(file.Id)}?alt=media&supportsAllDrives=true" + (apiKey == null ? "" : "&key=" + Uri.EscapeDataString(apiKey));
            try
            {
                byte[] bytes = await helper.GetAsync(client, url, bearer, expectHtml: false, cancellationToken, DescribeFolderFailure).ConfigureAwait(false);
                Workbook workbook;
                using (var stream = new MemoryStream(bytes, writable: false))
                {
                    workbook = XlsxReader.Read(stream, file.Name);
                }

                List<(string Name, string Text)> tabs = workbook.Sheets.Select(sheet => (sheet.Name, CsvWriter.Write(sheet.ToGrid()))).ToList();
                if (workbookSnapshot != null)
                {
                    GoogleSheetsSource.WriteSnapshot(workbookSnapshot, file.Name, tabs.Select(tab => tab.Name).ToList(), tabs.Select(tab => tab.Text).ToList(), complete: true);
                }

                if (file.Version is { } version)
                {
                    RemoteCopy.Save(remoteCacheDirectory, file.Id, version, file.Name, tabs);
                }

                return new[] { new Workbook(file.Name, tabs.Select(tab => Sheet.FromGrid(file.Name, tab.Name, parser.Parse(tab.Text))).ToList(), origin) };
            }
            catch (XlsxFormatException exception)
            {
                diagnostics.AddError(file.Name, $"The file is not a workbook Excel could save: {exception.Message}", "Open it in Excel, save it again as .xlsx and upload it again.");
                return Array.Empty<Workbook>();
            }
        }

        private void WriteIndex(IEnumerable<(string Name, string? Link)> files, bool complete)
        {
            if (snapshotDirectory == null)
            {
                return;
            }

            Directory.CreateDirectory(snapshotDirectory);
            Dictionary<string, (string Name, string? Link)> previous = ReadIndex(snapshotDirectory);
            Dictionary<string, (string Name, string? Link)> index = complete ? new Dictionary<string, (string Name, string? Link)>(StringComparer.Ordinal) : previous;
            foreach ((string name, string? link) in files)
            {
                index[GoogleSheetsSource.SafeFileName(name)] = (name, link);
            }

            if (complete)
            {
                foreach (string folder in previous.Keys)
                {
                    string gone = Path.Combine(snapshotDirectory, folder);
                    if (!index.ContainsKey(folder) && IsOwnFolderName(folder) && Directory.Exists(gone))
                    {
                        Directory.Delete(gone, recursive: true);
                    }
                }
            }

            foreach (string folder in index.Keys.ToList())
            {
                if (!Directory.Exists(Path.Combine(snapshotDirectory, folder)))
                {
                    index.Remove(folder);
                }
            }

            var text = new StringBuilder();
            foreach (KeyValuePair<string, (string Name, string? Link)> entry in index.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                text.Append(entry.Key).Append('\t').Append(entry.Value.Name);
                if (entry.Value.Link != null)
                {
                    text.Append('\t').Append(entry.Value.Link);
                }

                text.Append('\n');
            }

            string path = Path.Combine(snapshotDirectory, IndexFileName);
            string content = text.ToString();
            if (!File.Exists(path) || File.ReadAllText(path) != content)
            {
                File.WriteAllText(path, content, new UTF8Encoding(false));
            }
        }

        private static bool IsOwnFolderName(string folder) =>
            folder.Length > 0 && folder == Path.GetFileName(folder) && folder != "." && folder != "..";

        private static Dictionary<string, (string Name, string? Link)> ReadIndex(string directory)
        {
            var index = new Dictionary<string, (string Name, string? Link)>(StringComparer.Ordinal);
            string path = Path.Combine(directory, IndexFileName);
            if (!File.Exists(path))
            {
                return index;
            }

            foreach (string line in File.ReadAllText(path).Split('\n'))
            {
                string[] fields = line.Split('\t');
                if (fields.Length >= 2 && fields[0].Length > 0)
                {
                    index[fields[0]] = (fields[1], fields.Length >= 3 && fields[2].Length > 0 ? fields[2] : null);
                }
            }

            return index;
        }

        private async Task<IReadOnlyList<Workbook>> LoadSnapshotAsync(DiagnosticBag diagnostics, CancellationToken cancellationToken)
        {
            string directory = snapshotDirectory!;
            if (!File.Exists(Path.Combine(directory, IndexFileName)))
            {
                diagnostics.AddError(directory, "The Drive folder has no saved snapshot yet.", "Build once online; that build saves the snapshot an offline build reads.");
                return Array.Empty<Workbook>();
            }

            var workbooks = new List<Workbook>();
            foreach (KeyValuePair<string, (string Name, string? Link)> entry in ReadIndex(directory).OrderBy(entry => entry.Value.Name, StringComparer.Ordinal))
            {
                var csv = new CsvDirectorySource(Path.Combine(directory, entry.Key), parser, entry.Value.Name, only);
                IReadOnlyList<Workbook> loaded = await csv.LoadAsync(diagnostics, cancellationToken).ConfigureAwait(false);
                workbooks.AddRange(loaded.Select(workbook => workbook.WithOrigin(new WorkbookOrigin(SourceType.GoogleFolder, source.FolderId ?? "", entry.Value.Link))));
            }

            return workbooks;
        }

        private GoogleSourceException DescribeFolderFailure(HttpStatusCode status)
        {
            string id = source.FolderId ?? "";
            switch (status)
            {
                case HttpStatusCode.Unauthorized:
                case HttpStatusCode.Forbidden:
                    return new GoogleSourceException($"Google refused access to Drive folder '{id}' ({(int)status}).", source.Auth.Type switch
                    {
                        GoogleAuthType.ServiceAccount => $"Share the folder with the service account {_account ?? "(client_email in the key file)"} as a viewer.",
                        GoogleAuthType.ApiKey => "An API key only lists folders shared as 'Anyone with the link can view', and the Drive API must be enabled for the key's project.",
                        GoogleAuthType.AccessToken => "The access token is expired or lacks the drive.readonly scope.",
                        GoogleAuthType.Custom or GoogleAuthType.Command => $"Share the folder with {_account ?? "the account these credentials belong to"} as a viewer, and check that the credentials are not expired.",
                        _ => "Configure an API key, a service account or an access token: a folder cannot be listed without one."
                    });
                case HttpStatusCode.NotFound:
                    return new GoogleSourceException($"Google has no Drive folder or file '{id}' that these credentials can see (404).", source.Auth.Type switch
                    {
                        GoogleAuthType.ServiceAccount => $"Share the folder with the service account {_account ?? "(client_email in the key file)"} as a viewer, and check the id: drive.google.com/drive/folders/<id>.",
                        GoogleAuthType.Custom or GoogleAuthType.Command when _account != null => $"Share the folder with {_account} as a viewer, and check the id: drive.google.com/drive/folders/<id>.",
                        GoogleAuthType.ApiKey => "Share the folder as 'Anyone with the link can view', and check the id: drive.google.com/drive/folders/<id>.",
                        _ => "Check that the folder is shared with this account, and the id: drive.google.com/drive/folders/<id>."
                    });
                case (HttpStatusCode)429:
                    return new GoogleSourceException("Google is rate limiting requests (429).", "Wait a minute and try again.");
                default:
                    return new GoogleSourceException($"Google answered with status {(int)status}.", "Try again later. Use --offline to build from the saved snapshot.");
            }
        }
    }
}
