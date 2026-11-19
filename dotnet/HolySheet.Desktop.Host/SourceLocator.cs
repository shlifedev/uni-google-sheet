using System.Text.Json.Nodes;
using HolySheet.Core;
using HolySheet.Core.Config;
using HolySheet.Core.Sources;

namespace HolySheet.Desktop.Host;

/// <summary>
/// Where a workbook can be opened: the .xlsx or .csv file on disk, or the address of a Google spreadsheet or Drive folder.
/// The same rules as the Unity window's "Open" (HolySheetWindow.FindSourceFile); the app opens what this returns.
/// </summary>
public static class SourceLocator
{
    public static JsonObject Result(string? target) => new()
    {
        ["target"] = target,
        ["isUrl"] = target != null && target.StartsWith("https://", StringComparison.Ordinal),
        ["isFolder"] = target != null && Directory.Exists(target)
    };

    /// <summary>A CSV workbook opens at the given sheet's file. Null when no configured source has the workbook.</summary>
    public static string? FindSourceFile(HolySheetConfig? config, IReadOnlyDictionary<string, WorkbookOrigin>? origins, string workbook, string? sheet)
    {
        if (config == null)
        {
            return null;
        }

        WorkbookOrigin? origin = null;
        origins?.TryGetValue(workbook, out origin);
        if (origin?.Link != null)
        {
            return origin.Link;
        }

        if (origin != null)
        {
            return origin.Type switch
            {
                SourceType.Google => GoogleSheetsSource.SpreadsheetLink(origin.Source),
                SourceType.GoogleFolder => GoogleFolderSource.FolderLink(origin.Source),
                SourceType.Csv => CsvFile(config.Resolve(origin.Source), sheet),
                _ => XlsxFile(config.Resolve(origin.Source), workbook)
            };
        }

        // Not read (for example the file could not be parsed): look for it among the configured sources.
        foreach (SourceConfig source in config.Sources)
        {
            switch (source.Type)
            {
                case SourceType.Xlsx:
                    if (XlsxFile(config.Resolve(source.Path ?? ""), workbook) is { } file)
                    {
                        return file;
                    }

                    break;
                case SourceType.Csv:
                    string folder = config.Resolve(source.Path ?? "");
                    if (Path.GetFileName(folder.TrimEnd('/', '\\')) == workbook)
                    {
                        return CsvFile(folder, sheet);
                    }

                    break;
                case SourceType.Google:
                    if (source.Name == workbook || source.SpreadsheetId == workbook)
                    {
                        return GoogleSheetsSource.SpreadsheetLink(source.SpreadsheetId ?? "");
                    }

                    break;
            }
        }

        return null;
    }

    /// <summary>
    /// A problem names a cell ("Book / Sheet!C8"), a sheet ("Book / Sheet") or a workbook. With the cell's origin known,
    /// that wins; otherwise the location text is split.
    /// </summary>
    public static (string Workbook, string? Sheet) SplitProblem(string location, string? workbook, string? sheet)
    {
        if (workbook != null)
        {
            return (workbook, sheet);
        }

        int separator = location.IndexOf(" / ", StringComparison.Ordinal);
        if (separator < 0)
        {
            return (location, null);
        }

        string page = location[(separator + 3)..];
        int cell = page.IndexOf('!');
        return (location[..separator], cell >= 0 ? page[..cell] : page);
    }

    private static string? XlsxFile(string path, string workbook)
    {
        if (File.Exists(path))
        {
            return Path.GetFileName(path) == workbook ? path : null;
        }

        string inFolder = Path.Combine(path, workbook);
        return File.Exists(inFolder) ? inFolder : null;
    }

    private static string? CsvFile(string folder, string? sheet)
    {
        string file = Path.Combine(folder, (sheet ?? "") + ".csv");
        return sheet != null && File.Exists(file) ? file : Directory.Exists(folder) ? folder : null;
    }
}
