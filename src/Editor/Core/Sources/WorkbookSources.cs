#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HolySheet.Core.Config;
using HolySheet.Core.Csv;

namespace HolySheet.Core.Sources
{
    /// <summary>Where sheets come from. A source turns files or a remote spreadsheet into workbooks of plain text cells.</summary>
    public interface IWorkbookSource
    {
        /// <summary>Short text for logs, such as the path or the spreadsheet id.</summary>
        string Description { get; }

        /// <summary>Files or folders whose changes mean this source should be read again. Empty for remote sources.</summary>
        IReadOnlyList<string> WatchPaths { get; }

        /// <summary>Reads everything. Problems that leave the rest readable are reported as diagnostics instead of thrown.</summary>
        Task<IReadOnlyList<Workbook>> LoadAsync(DiagnosticBag diagnostics, CancellationToken cancellationToken);
    }

    /// <summary>A folder of .csv files. Each file is one sheet, named after the file; the folder is the workbook.</summary>
    /// <remarks>With a selection, only the selected files are parsed.</remarks>
    public sealed class CsvDirectorySource : IWorkbookSource
    {
        private readonly string directory;
        private readonly ICsvParser parser;
        private readonly string? workbookName;
        private readonly SheetSelection? only;

        public CsvDirectorySource(string directory, ICsvParser parser, string? workbookName = null, SheetSelection? only = null)
        {
            this.directory = directory;
            this.parser = parser;
            this.workbookName = workbookName;
            this.only = only;
        }

        public string Description => directory;

        public IReadOnlyList<string> WatchPaths => new[] { directory };

        public Task<IReadOnlyList<Workbook>> LoadAsync(DiagnosticBag diagnostics, CancellationToken cancellationToken)
        {
            string name = workbookName ?? Path.GetFileName(directory.TrimEnd('/', '\\'));
            if (!Directory.Exists(directory))
            {
                diagnostics.AddError(directory, "The CSV folder does not exist.", "Create it, or fix the source path in the config.");
                return Task.FromResult<IReadOnlyList<Workbook>>(Array.Empty<Workbook>());
            }

            if (only != null && !only.Touches(name))
            {
                return Task.FromResult<IReadOnlyList<Workbook>>(Array.Empty<Workbook>());
            }

            string[] files = Directory.GetFiles(directory, "*.csv", SearchOption.TopDirectoryOnly)
                .Where(file => only == null || only.Includes(name, Path.GetFileNameWithoutExtension(file))).ToArray();
            Array.Sort(files, StringComparer.Ordinal);

            // Files are independent, so they are read and parsed at the same time. Results and problems are then
            // collected in file order, which keeps the outcome the same however the work was scheduled.
            var grids = new List<string[]>?[files.Length];
            var failures = new Exception?[files.Length];
            Parallel.For(0, files.Length, new ParallelOptions { CancellationToken = cancellationToken }, index =>
            {
                try
                {
                    grids[index] = parser.Parse(SourceFiles.ReadAllText(files[index]));
                }
                catch (Exception exception) when (exception is CsvFormatException or IOException)
                {
                    failures[index] = exception;
                }
            });

            var sheets = new List<Sheet>(files.Length);
            for (int index = 0; index < files.Length; index++)
            {
                string sheetName = Path.GetFileNameWithoutExtension(files[index]);
                if (grids[index] is { } grid)
                {
                    sheets.Add(Sheet.FromGrid(name, sheetName, grid));
                }
                else if (failures[index] is CsvFormatException format)
                {
                    diagnostics.AddError($"{name} / {sheetName}", format.Message, "Check the quotes around that line; a cell that contains a quote must double it.");
                }
                else if (failures[index] is { } failure)
                {
                    diagnostics.AddError($"{name} / {sheetName}", $"The file could not be read: {failure.Message}", "Close the program that has it open and try again.");
                }
            }

            return Task.FromResult<IReadOnlyList<Workbook>>(new[] { new Workbook(name, sheets) });
        }
    }

    /// <summary>One .xlsx file, or every .xlsx file directly inside a folder. Each file is a workbook.</summary>
    /// <remarks>With a selection, files with nothing selected are not opened.</remarks>
    public sealed class XlsxSource : IWorkbookSource
    {
        private readonly string path;
        private readonly SheetSelection? only;

        public XlsxSource(string path, SheetSelection? only = null)
        {
            this.path = path;
            this.only = only;
        }

        // Excel writes "~$Book.xlsx" next to a workbook while it is open. It is a lock file, not a workbook.
        private const string LockFilePrefix = "~$";

        public string Description => path;

        public IReadOnlyList<string> WatchPaths => new[] { path };

        public Task<IReadOnlyList<Workbook>> LoadAsync(DiagnosticBag diagnostics, CancellationToken cancellationToken)
        {
            var files = new List<string>();
            if (File.Exists(path))
            {
                files.Add(path);
            }
            else if (Directory.Exists(path))
            {
                foreach (string file in Directory.GetFiles(path, "*.xlsx", SearchOption.TopDirectoryOnly))
                {
                    if (!Path.GetFileName(file).StartsWith(LockFilePrefix, StringComparison.Ordinal) && (only == null || only.Touches(Path.GetFileName(file))))
                    {
                        files.Add(file);
                    }
                }

                files.Sort(StringComparer.Ordinal);
            }
            else
            {
                diagnostics.AddError(path, "The xlsx file or folder does not exist.", "Create it, or fix the source path in the config.");
            }

            var workbooks = new List<Workbook>(files.Count);
            foreach (string file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    workbooks.Add(XlsxReader.Read(file));
                }
                catch (XlsxFormatException exception)
                {
                    diagnostics.AddError(Path.GetFileName(file), exception.Message, "Open the file in Excel and save it again as .xlsx.");
                }
                catch (IOException exception)
                {
                    diagnostics.AddError(Path.GetFileName(file), $"The file could not be read: {exception.Message}", "Close the program that has it open and try again.");
                }
            }

            return Task.FromResult<IReadOnlyList<Workbook>>(workbooks);
        }
    }

    /// <summary>Workbooks that are already in memory.</summary>
    public sealed class MemoryWorkbookSource : IWorkbookSource
    {
        private readonly Workbook[] workbooks;

        public MemoryWorkbookSource(params Workbook[] workbooks)
        {
            this.workbooks = workbooks;
        }

        public string Description => "memory";

        public IReadOnlyList<string> WatchPaths => Array.Empty<string>();

        public Task<IReadOnlyList<Workbook>> LoadAsync(DiagnosticBag diagnostics, CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<Workbook>>(workbooks);
        }
    }

    internal static class SourceFiles
    {
        private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

        // Opens with sharing so a file that an editor or a sync client still has open can be read.
        public static string ReadAllText(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Utf8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
    }

    /// <summary>Creates the sources a config lists.</summary>
    public static class WorkbookSources
    {
        public static IReadOnlyList<IWorkbookSource> Create(HolySheetConfig config, ICsvParser parser, bool offline = false, HttpMessageHandler? httpHandler = null, GoogleEndpoints? endpoints = null, SheetSelection? only = null, IReadOnlyList<IGoogleCredentialProvider>? credentialProviders = null)
        {
            var sources = new List<IWorkbookSource>(config.Sources.Count);
            // Copies of Google files by Drive version, so unchanged files are not downloaded again.
            string remoteCache = Path.Combine(config.Resolve(config.CacheDirectory), "google");
            foreach (SourceConfig source in config.Sources)
            {
                switch (source.Type)
                {
                    case SourceType.Csv:
                        sources.Add(new CsvDirectorySource(config.Resolve(source.Path ?? ""), parser, only: only));
                        break;
                    case SourceType.Xlsx:
                        sources.Add(new XlsxSource(config.Resolve(source.Path ?? ""), only));
                        break;
                    case SourceType.GoogleFolder:
                    {
                        string? folderSnapshot = source.Snapshot == null ? null : config.Resolve(source.Snapshot);
                        sources.Add(new GoogleFolderSource(source, config.BaseDirectory, parser, folderSnapshot, httpHandler, endpoints, only, offline && folderSnapshot != null, remoteCache, credentialProviders));
                        break;
                    }
                    default:
                        string? snapshot = source.Snapshot == null ? null : config.Resolve(source.Snapshot);
                        if (offline && snapshot != null)
                        {
                            // Offline builds read the CSV that the last online build saved, under the name that build used:
                            // without a configured name it is the spreadsheet's title, which only the snapshot remembers.
                            // A different name would make a partial build miss the cached workbook.
                            string? savedName = Directory.Exists(snapshot) ? SnapshotIndex.Read(snapshot).Workbook : null;
                            sources.Add(new CsvDirectorySource(snapshot, parser, source.Name ?? savedName ?? source.SpreadsheetId, only));
                        }
                        else
                        {
                            sources.Add(new GoogleSheetsSource(source, config.BaseDirectory, parser, snapshot, httpHandler, endpoints, only, remoteCache, credentialProviders));
                        }

                        break;
                }
            }

            return sources;
        }
    }
}
