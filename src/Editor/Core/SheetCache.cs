#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using HolySheet.Core.Schema;

namespace HolySheet.Core
{
    /// <summary>
    /// The sheets of the last build written on this machine, exactly as they were read. A partial build reads only the
    /// selected sheets again and takes the rest from here, so tables nobody selected come out the same as last time.
    /// <para>
    /// The cache remembers the manifest that build wrote. If the manifest in the data folder is a different one, the data was
    /// produced somewhere else (a pull, another machine, a build with another tool), and the cached sheets may no longer be what
    /// that data was built from. A partial build then refuses to run: it could silently put old values back.
    /// </para>
    /// <para>
    /// Layout: an index file and one file per distinct sheet, named after its content hash, so an unchanged sheet is not
    /// written again. The folder carries its own .gitignore; it is local state, not something to commit.
    /// </para>
    /// </summary>
    public sealed class SheetCache
    {
        public const string DefaultDirectory = ".holysheet/cache";
        private const string IndexFileName = "sheets.index";
        // Version 2 added each workbook's origin. An older cache is not read: the next partial build asks for a full one.
        private const string Header = "holysheet-sheet-cache 2";
        // Accept version 3's extra index entries when reading a partial build from that version.
        private const string HeaderWithPreviews = "holysheet-sheet-cache 3";
        private static readonly UTF8Encoding Utf8 = new(false);

        private SheetCache(IReadOnlyList<Workbook> workbooks, string manifestHash)
        {
            Workbooks = workbooks;
            ManifestHash = manifestHash;
        }

        /// <summary>The workbooks in the order they were read.</summary>
        public IReadOnlyList<Workbook> Workbooks { get; }

        /// <summary>SHA-256 of the manifest the cached build wrote.</summary>
        public string ManifestHash { get; }

        public static string HashManifest(string manifestText) => SchemaHasher.Sha256Hex(Utf8.GetBytes(manifestText));

        public static string HashManifest(byte[] manifestBytes) => SchemaHasher.Sha256Hex(manifestBytes);

        /// <summary>Null when there is no cache in the folder or it cannot be read.</summary>
        public static SheetCache? Load(string directory)
        {
            string indexPath = Path.Combine(directory, IndexFileName);
            if (!File.Exists(indexPath))
            {
                return null;
            }

            try
            {
                string[] lines = ReadIndex(indexPath);
                if (lines.Length < 2 || (lines[0] != Header && lines[0] != HeaderWithPreviews) || !lines[1].StartsWith("manifest ", StringComparison.Ordinal))
                {
                    return null;
                }

                string manifestHash = lines[1].Substring("manifest ".Length);
                var workbooks = new List<Workbook>();
                string? workbookName = null;
                WorkbookOrigin? origin = null;
                var sheets = new List<Sheet>();
                foreach (string line in lines.Skip(2))
                {
                    if (line.Length == 0)
                    {
                        continue;
                    }

                    string[] fields = line.Split('\t');
                    if (fields[0] == "workbook" && fields.Length is 2 or 4 or 5)
                    {
                        if (workbookName != null)
                        {
                            workbooks.Add(new Workbook(workbookName, sheets, origin));
                        }

                        workbookName = Unescape(fields[1]);
                        origin = fields.Length >= 4 && Enum.TryParse(fields[2], out Config.SourceType type)
                            ? new WorkbookOrigin(type, Unescape(fields[3]), fields.Length == 5 ? Unescape(fields[4]) : null)
                            : null;
                        sheets = new List<Sheet>();
                    }
                    else if (fields[0] == "sheet" && fields.Length == 3 && workbookName != null)
                    {
                        Sheet? sheet = ReadSheet(directory, fields[1], workbookName, Unescape(fields[2]));
                        if (sheet == null) return null;
                        sheets.Add(sheet);
                    }
                    else if (lines[0] != HeaderWithPreviews || !(fields[0] == "preview" && fields.Length == 3 || fields[0] == "previews" && fields.Length == 2))
                    {
                        return null;
                    }
                }

                if (workbookName != null)
                {
                    workbooks.Add(new Workbook(workbookName, sheets, origin));
                }

                return new SheetCache(workbooks, manifestHash);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static string[] ReadIndex(string path)
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            using var reader = new StreamReader(file, Utf8);
            return reader.ReadToEnd().Split('\n');
        }

        private static Sheet? ReadSheet(string directory, string file, string workbook, string name)
        {
            if (file.Length != 38 || !file.EndsWith(".sheet", StringComparison.Ordinal) || !file.Take(32).All(Uri.IsHexDigit))
            {
                return null;
            }

            byte[] bytes = File.ReadAllBytes(Path.Combine(directory, file));
            return GridFileName(bytes) == file ? Sheet.FromGrid(workbook, name, ReadGrid(Utf8.GetString(bytes))) : null;
        }

        private static string GridFileName(byte[] bytes) => SchemaHasher.Sha256Hex(bytes).Substring(0, 32) + ".sheet";

        /// <summary>Removes the cache, so that the next partial build asks for a full one.</summary>
        public static void Delete(string directory)
        {
            try
            {
                File.Delete(Path.Combine(directory, IndexFileName));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        /// <summary>Replaces the cache with these workbooks, as built into the given manifest.</summary>
        public static void Save(string directory, IReadOnlyList<Workbook> workbooks, string manifestText)
        {
            Directory.CreateDirectory(directory);
            string ignore = Path.Combine(directory, ".gitignore");
            if (!File.Exists(ignore))
            {
                File.WriteAllText(ignore, "# Written by HolySheet: local build state, not for version control.\n*\n");
            }

            var index = new StringBuilder();
            index.Append(Header).Append('\n').Append("manifest ").Append(HashManifest(manifestText)).Append('\n');
            var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { IndexFileName, ".gitignore" };
            foreach (Workbook workbook in workbooks)
            {
                index.Append("workbook\t").Append(Escape(workbook.Name));
                if (workbook.Origin != null)
                {
                    index.Append('\t').Append(workbook.Origin.Type).Append('\t').Append(Escape(workbook.Origin.Source));
                    if (workbook.Origin.Link != null)
                    {
                        index.Append('\t').Append(Escape(workbook.Origin.Link));
                    }
                }

                index.Append('\n');
                foreach (Sheet sheet in workbook.Sheets)
                {
                    byte[] bytes = Utf8.GetBytes(WriteGrid(sheet));
                    string file = GridFileName(bytes);
                    if (kept.Add(file))
                    {
                        string path = Path.Combine(directory, file);
                        if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
                        {
                            File.WriteAllBytes(path, bytes);
                        }
                    }

                    index.Append("sheet\t").Append(file).Append('\t').Append(Escape(sheet.Name)).Append('\n');
                }
            }

            // The index is written last and in one piece, so an interrupted save leaves the previous cache readable.
            string temporary = Path.Combine(directory, IndexFileName + ".tmp");
            File.WriteAllText(temporary, index.ToString(), Utf8);
            string indexPath = Path.Combine(directory, IndexFileName);
            if (File.Exists(indexPath))
            {
                File.Replace(temporary, indexPath, null);
            }
            else
            {
                File.Move(temporary, indexPath);
            }

            foreach (string path in Directory.GetFiles(directory, "*.sheet", SearchOption.TopDirectoryOnly))
            {
                if (!kept.Contains(Path.GetFileName(path)))
                {
                    File.Delete(path);
                }
            }
        }

        // One row per line, cells separated by tabs. Escaping keeps every character, including tabs and line breaks in cells.
        private static string WriteGrid(Sheet sheet)
        {
            var builder = new StringBuilder();
            for (int row = 0; row < sheet.RowCount; row++)
            {
                int last = sheet.ColumnCount - 1;
                while (last >= 0 && sheet.GetCell(row, last).Text.Length == 0)
                {
                    last--;
                }

                for (int column = 0; column <= last; column++)
                {
                    if (column > 0)
                    {
                        builder.Append('\t');
                    }

                    builder.Append(Escape(sheet.GetCell(row, column).Text));
                }

                builder.Append('\n');
            }

            return builder.ToString();
        }

        private static List<string[]> ReadGrid(string text)
        {
            var rows = new List<string[]>();
            int start = 0;
            while (start < text.Length)
            {
                int end = text.IndexOf('\n', start);
                if (end < 0)
                {
                    end = text.Length;
                }

                string line = text.Substring(start, end - start);
                rows.Add(line.Length == 0 ? Array.Empty<string>() : line.Split('\t').Select(Unescape).ToArray());
                start = end + 1;
            }

            return rows;
        }

        private static string Escape(string value)
        {
            if (value.IndexOfAny(new[] { '\\', '\t', '\n', '\r' }) < 0)
            {
                return value;
            }

            var builder = new StringBuilder(value.Length + 8);
            foreach (char c in value)
            {
                builder.Append(c switch
                {
                    '\\' => "\\\\",
                    '\t' => "\\t",
                    '\n' => "\\n",
                    '\r' => "\\r",
                    _ => c.ToString(),
                });
            }

            return builder.ToString();
        }

        private static string Unescape(string value)
        {
            if (value.IndexOf('\\') < 0)
            {
                return value;
            }

            var builder = new StringBuilder(value.Length);
            for (int index = 0; index < value.Length; index++)
            {
                char c = value[index];
                if (c == '\\' && index + 1 < value.Length)
                {
                    index++;
                    builder.Append(value[index] switch
                    {
                        't' => '\t',
                        'n' => '\n',
                        'r' => '\r',
                        _ => value[index],
                    });
                }
                else
                {
                    builder.Append(c);
                }
            }

            return builder.ToString();
        }
    }
}
