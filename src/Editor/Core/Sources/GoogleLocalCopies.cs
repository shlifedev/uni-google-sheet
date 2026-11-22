#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HolySheet.Core.Config;
using HolySheet.Core.Csv;

namespace HolySheet.Core.Sources
{
    /// <summary>A native Google tab the window can download individually.</summary>
    public sealed record GoogleTab(string Workbook, string Sheet, string SpreadsheetId, DateTime? LocalCopyUtc, string? LocalPath);

    /// <summary>Explicit local copies are independent of the automatic Drive cache and the configured snapshot.</summary>
    internal sealed class GoogleLocalCopies
    {
        private readonly string root;
        private static readonly UTF8Encoding Utf8 = new(false);

        public GoogleLocalCopies(string baseDirectory) => root = Path.Combine(baseDirectory, ".holysheet", "local-copies");

        private static string Hash(string value)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(Utf8.GetBytes(value))).Replace("-", "").ToLowerInvariant();
        }

        // Include the exact title in the hash. Tabs such as A/B and A:B (or two titles differing in case)
        // must remain distinct on Windows and macOS. The suffix makes the downloaded CSV recognizable.
        public string CsvPath(string id, string tab)
        {
            string name = GoogleSheetsSource.SafeFileName(tab);
            name = new string(name.Take(48).Select(c => char.IsControl(c) || char.IsSurrogate(c) ? '_' : c).ToArray());
            return Path.Combine(root, Hash(id), Hash(tab).Substring(0, 16) + "-" + name + ".csv");
        }

        public string CatalogPath(SourceConfig source)
        {
            string key = JsonValue.Array(new[] { source.Type.ToString(), source.SpreadsheetId ?? source.FolderId ?? "", source.Name ?? "" }
                .Concat(source.Sheets.SelectMany(tab => new[] { tab.Name, tab.Gid.ToString(CultureInfo.InvariantCulture) }))).ToJson(false);
            return Path.Combine(root, Hash(key) + ".json");
        }

        public static string? SpreadsheetId(SourceConfig source, Workbook workbook)
        {
            if (source.Type == SourceType.Google)
            {
                return source.SpreadsheetId;
            }

            string? link = workbook.Origin?.Link;
            return source.Type == SourceType.GoogleFolder && link != null && link.StartsWith("https://docs.google.com/spreadsheets/d/", StringComparison.Ordinal)
                ? SourceConfig.ExtractGoogleId(link) : null;
        }

        // The catalog remembers file and tab names. The CSV itself records the decision to pin a tab.
        public void SaveCatalog(SourceConfig source, IReadOnlyList<Workbook> books)
        {
            string text = JsonValue.Array(books.Select(book => JsonValue.Object().Set("name", book.Name)
                .Set("link", book.Origin?.Link).Set("tabs", JsonValue.Array(book.Sheets.Select(tab => tab.Name))).ToValue())).ToJson();
            string path = CatalogPath(source);
            if (!File.Exists(path) || SourceFiles.ReadAllText(path) != text)
            {
                Write(path, text);
            }
        }

        private IReadOnlyList<JsonValue>? Catalog(SourceConfig source)
        {
            string path = CatalogPath(source);
            if (!File.Exists(path)) return null;
            try { return JsonValue.Parse(SourceFiles.ReadAllText(path)).AsArray; }
            catch (Exception exception) when (exception is JsonFormatException or IOException or UnauthorizedAccessException) { return null; }
        }

        public bool HasCopies(string id)
        {
            string folder = Path.Combine(root, Hash(id));
            return Directory.Exists(folder) && Directory.EnumerateFiles(folder, "*.csv").Any();
        }

        // A wholly pinned selection needs no network. Otherwise let the source discover its files and tabs.
        public IReadOnlyList<Workbook>? ReadLocal(SourceConfig source, SheetSelection? only, ICsvParser? parser)
        {
            var catalog = Catalog(source);
            if (catalog == null || catalog.Count == 0) return null;
            var entries = new List<(Workbook Book, List<(string Name, string Path)> Tabs)>();
            foreach (JsonValue entry in catalog)
            {
                JsonObject? item = entry.AsObject;
                if (item?.GetString("name") is not { } name || item.GetArray("tabs") is not { } tabs) return null;
                if (only != null && !only.Touches(name)) continue;
                var origin = new WorkbookOrigin(source.Type, source.SpreadsheetId ?? source.FolderId ?? "", item.GetString("link"));
                var book = new Workbook(name, Array.Empty<Sheet>(), origin);
                string? id = SpreadsheetId(source, book);
                if (id == null || tabs.Count == 0) return null;
                var paths = new List<(string Name, string Path)>();
                foreach (JsonValue tab in tabs)
                {
                    if (tab.AsString is not { } title) return null;
                    if (only != null && !only.Includes(name, title)) continue;
                    string path = CsvPath(id, title);
                    if (!File.Exists(path)) return null;
                    paths.Add((title, path));
                }

                var names = new HashSet<string>(paths.Select(tab => tab.Name), StringComparer.Ordinal);
                if (names.Count == 0 || (only != null && only.Sheets.Any(selected => selected.Workbook == name && !names.Contains(selected.Sheet))))
                    return null;
                // A snapshot may contain fewer tabs than an explicit source list.
                if (source.Type == SourceType.Google && source.Sheets.Any(tab => (only == null || only.Includes(name, tab.Name)) && !names.Contains(tab.Name)))
                    return null;
                entries.Add((book, paths));
            }

            if (entries.Count == 0) return null;
            if (source.Type == SourceType.GoogleFolder && only != null)
            {
                var found = new HashSet<string>(entries.Select(entry => entry.Book.Name), StringComparer.Ordinal);
                if (only.Workbooks.Concat(only.Sheets.Select(tab => tab.Workbook)).Any(name => !found.Contains(name))) return null;
            }

            if (parser == null) return entries.Select(entry => entry.Book).ToList();
            var result = new List<Workbook>(entries.Count);
            foreach (var (book, tabs) in entries)
            {
                var sheets = tabs.Select(tab => Sheet.FromGrid(book.Name, tab.Name, parser.Parse(SourceFiles.ReadAllText(tab.Path)))).ToList();
                result.Add(new Workbook(book.Name, sheets, book.Origin));
            }

            return result;
        }

        public static void Write(string path, string text, bool existingOnly = false)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".holysheet-tmp";
            try
            {
                File.WriteAllText(temporary, text, Utf8);
                if (existingOnly || File.Exists(path))
                {
                    try { File.Replace(temporary, path, null); }
                    catch (FileNotFoundException) when (existingOnly) { } // Released by another window during a refresh.
                }
                else
                {
                    File.Move(temporary, path);
                }
            }
            finally
            {
                File.Delete(temporary);
            }
        }
    }

    internal sealed class GoogleSourcePreview : IWorkbookSource
    {
        public IReadOnlyList<Workbook> Books { get; }
        public DiagnosticBag Problems { get; }
        public string Description => "Google preview";
        public IReadOnlyList<string> WatchPaths => Array.Empty<string>();

        public GoogleSourcePreview(IReadOnlyList<Workbook> books, DiagnosticBag problems)
        {
            Books = books;
            Problems = problems;
        }

        public Task<IReadOnlyList<Workbook>> LoadAsync(DiagnosticBag diagnostics, CancellationToken cancellationToken)
        {
            diagnostics.AddRange(Problems);
            return Task.FromResult(Books);
        }
    }

    public static class GoogleTabCopies
    {
        /// <summary>Download one tab or release its copy, then validate against the other Google inputs already on screen.</summary>
        public static async Task<BuildResult> ChangeAsync(HolySheetConfig config, BuildResult previous, string workbook, string sheet,
            bool useLocal, TableBuildOptions options, CancellationToken cancellationToken = default)
        {
            if (!previous.GoogleTabs.TryGetValue((workbook, sheet), out GoogleTab? tab))
                throw new InvalidOperationException("Select a tab from a Google spreadsheet.");

            var copies = new GoogleLocalCopies(config.BaseDirectory);
            var sources = new Dictionary<string, GoogleSourcePreview>(previous.GoogleInputs);
            SourceConfig? source = config.Sources.FirstOrDefault(candidate => candidate.IsRemote
                && sources.TryGetValue(copies.CatalogPath(candidate), out var input)
                && input.Books.Any(book => book.Name == workbook && GoogleLocalCopies.SpreadsheetId(candidate, book) == tab.SpreadsheetId));
            if (source == null || config.Sources.Any(candidate => candidate.IsRemote && !sources.ContainsKey(copies.CatalogPath(candidate))))
                throw new InvalidOperationException("The Google sources changed. Refresh the project before downloading a tab.");

            string path = copies.CsvPath(tab.SpreadsheetId, sheet);
            if (useLocal)
            {
                ICsvParser parser = CsvBackends.Resolve(config.CsvBackend);
                var reader = new GoogleSheetsSource(source, config.BaseDirectory, parser, httpHandler: options.HttpHandler,
                    endpoints: options.GoogleEndpoints, credentialProviders: options.CredentialProviders);
                string text = await reader.DownloadTabAsync(tab.SpreadsheetId, sheet, cancellationToken).ConfigureAwait(false);
                Sheet fresh = Sheet.FromGrid(workbook, sheet, parser.Parse(text));
                string key = copies.CatalogPath(source);
                GoogleSourcePreview input = sources[key];
                if (!input.Problems.HasErrors) copies.SaveCatalog(source, input.Books);
                GoogleLocalCopies.Write(path, text, existingOnly: tab.LocalCopyUtc.HasValue);
                sources[key] = new GoogleSourcePreview(input.Books.Select(book => book.Name == workbook
                    ? new Workbook(book.Name, book.Sheets.Select(old => old.Name == sheet ? fresh : old).ToList(), book.Origin)
                    : book).ToList(), input.Problems);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }

            RemoteCopy.Invalidate(Path.Combine(config.Resolve(config.CacheDirectory), "google"), tab.SpreadsheetId);
            return await TableBuild.RunAsync(config, options.ForGooglePreview(sources), cancellationToken).ConfigureAwait(false);
        }
    }

    internal sealed class GoogleCopySource : IWorkbookSource
    {
        private readonly SourceConfig config;
        private readonly IWorkbookSource source;
        private readonly GoogleLocalCopies copies;
        private readonly ICsvParser parser;
        private readonly SheetSelection? only;
        private readonly bool refresh;

        public GoogleCopySource(SourceConfig config, IWorkbookSource source, GoogleLocalCopies copies, ICsvParser parser, SheetSelection? only, bool refresh)
        {
            this.config = config;
            this.source = source;
            this.copies = copies;
            this.parser = parser;
            this.only = only;
            this.refresh = refresh;
        }

        public string Description => source.Description;
        public IReadOnlyList<string> WatchPaths => source.WatchPaths;

        public async Task<IReadOnlyList<Workbook>> LoadAsync(DiagnosticBag diagnostics, CancellationToken cancellationToken)
        {
            try
            {
                if (!refresh && copies.ReadLocal(config, only, parser) is { } local) return local;
                var books = await source.LoadAsync(diagnostics, cancellationToken).ConfigureAwait(false);
                var result = new List<Workbook>(books.Count);
                foreach (Workbook book in books)
                {
                    string? id = GoogleLocalCopies.SpreadsheetId(config, book);
                    if (refresh || id == null || !copies.HasCopies(id))
                    {
                        result.Add(book);
                        continue;
                    }
                    var sheets = new List<Sheet>(book.Sheets.Count);
                    foreach (Sheet sheet in book.Sheets)
                    {
                        string path = copies.CsvPath(id, sheet.Name);
                        sheets.Add(File.Exists(path) ? Sheet.FromGrid(book.Name, sheet.Name, parser.Parse(SourceFiles.ReadAllText(path))) : sheet);
                    }

                    result.Add(new Workbook(book.Name, sheets, book.Origin));
                }

                if (only == null && !diagnostics.HasErrors && File.Exists(copies.CatalogPath(config)))
                    copies.SaveCatalog(config, result);
                return result;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CsvFormatException)
            {
                diagnostics.AddError(Description, "The local Google copy could not be read or written: " + exception.Message);
                return Array.Empty<Workbook>();
            }
        }
    }
}
