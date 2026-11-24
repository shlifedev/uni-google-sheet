#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace HolySheet.Core.Sources
{
    public sealed class XlsxFormatException : Exception
    {
        public XlsxFormatException(string message, Exception? inner = null) : base(message, inner)
        {
        }
    }

    public static class XlsxReader
    {
        private static readonly HashSet<int> BuiltinDateFormats = new HashSet<int>
        {
            14, 15, 16, 17, 18, 19, 20, 21, 22, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 45, 46, 47, 50, 51, 52, 53, 54, 55, 56, 57, 58
        };

        private const double MaxExactInteger = 9007199254740992.0;

        public static Workbook Read(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Read(stream, Path.GetFileName(path));
        }

        public static Workbook Read(Stream stream, string workbookName)
        {
            try
            {
                using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
                List<(string Name, string RelationId)> sheetList = ReadSheetList(zip);
                Dictionary<string, string> targets = ReadRelationships(zip);
                string[] sharedStrings = ReadSharedStrings(zip);
                bool[] dateStyles = ReadDateStyles(zip, out bool date1904);

                var sheets = new List<Sheet>(sheetList.Count);
                foreach ((string name, string relationId) in sheetList)
                {
                    if (!targets.TryGetValue(relationId, out string? target))
                    {
                        continue;
                    }

                    // Chart sheets and macro sheets are listed like worksheets but hold no cells.
                    ZipArchiveEntry? entry = FindEntry(zip, ResolveTarget(target));
                    if (entry == null || !ResolveTarget(target).Contains("worksheets/", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    sheets.Add(Sheet.FromGrid(workbookName, name, ReadSheet(entry, sharedStrings, dateStyles, date1904)));
                }

                return new Workbook(workbookName, sheets);
            }
            catch (Exception exception) when (exception is InvalidDataException or XmlException or FormatException)
            {
                throw new XlsxFormatException($"'{workbookName}' could not be read as an xlsx workbook: {exception.Message}", exception);
            }
        }

        private static List<(string Name, string RelationId)> ReadSheetList(ZipArchive zip)
        {
            ZipArchiveEntry entry = FindEntry(zip, "xl/workbook.xml") ?? throw new InvalidDataException("xl/workbook.xml is missing; this is not an xlsx file");
            var result = new List<(string, string)>();
            using XmlReader reader = OpenXml(entry);
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "sheet")
                {
                    continue;
                }

                string? name = reader.GetAttribute("name");
                string? relationId = null;
                for (bool has = reader.MoveToFirstAttribute(); has; has = reader.MoveToNextAttribute())
                {
                    if (reader.LocalName == "id" && reader.Prefix.Length > 0)
                    {
                        relationId = reader.Value;
                    }
                }

                reader.MoveToElement();
                if (name != null && relationId != null)
                {
                    result.Add((name, relationId));
                }
            }

            return result;
        }

        private static Dictionary<string, string> ReadRelationships(ZipArchive zip)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            ZipArchiveEntry? entry = FindEntry(zip, "xl/_rels/workbook.xml.rels");
            if (entry == null)
            {
                return result;
            }

            using XmlReader reader = OpenXml(entry);
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Relationship"
                    && reader.GetAttribute("Id") is { } id && reader.GetAttribute("Target") is { } target)
                {
                    result[id] = target;
                }
            }

            return result;
        }

        private static string ResolveTarget(string target)
        {
            return target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
        }

        private static string[] ReadSharedStrings(ZipArchive zip)
        {
            ZipArchiveEntry? entry = FindEntry(zip, "xl/sharedStrings.xml");
            if (entry == null)
            {
                return Array.Empty<string>();
            }

            var result = new List<string>();
            using XmlReader reader = OpenXml(entry);
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "si")
                {
                    result.Add(ReadRichText(reader));
                }
            }

            return result.ToArray();
        }

        private static string ReadRichText(XmlReader reader)
        {
            if (reader.IsEmptyElement)
            {
                return "";
            }

            var builder = new StringBuilder();
            int depth = reader.Depth;
            int phoneticDepth = -1;
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.EndElement)
                {
                    if (reader.Depth == depth)
                    {
                        break;
                    }

                    if (reader.Depth == phoneticDepth)
                    {
                        phoneticDepth = -1;
                    }
                }
                else if (reader.NodeType == XmlNodeType.Element)
                {
                    if (reader.LocalName == "rPh" && phoneticDepth < 0 && !reader.IsEmptyElement)
                    {
                        phoneticDepth = reader.Depth;
                    }
                    else if (reader.LocalName == "t" && phoneticDepth < 0)
                    {
                        builder.Append(ReadInnerText(reader));
                    }
                }
            }

            return builder.ToString();
        }

        private static string ReadInnerText(XmlReader reader)
        {
            if (reader.IsEmptyElement)
            {
                return "";
            }

            string? text = null;
            while (reader.Read())
            {
                if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace)
                {
                    text = text == null ? reader.Value : text + reader.Value;
                }
                else if (reader.NodeType == XmlNodeType.EndElement)
                {
                    break;
                }
            }

            return text ?? "";
        }

        private static bool[] ReadDateStyles(ZipArchive zip, out bool date1904)
        {
            date1904 = false;
            if (FindEntry(zip, "xl/workbook.xml") is { } workbookEntry)
            {
                using XmlReader workbook = OpenXml(workbookEntry);
                while (workbook.Read())
                {
                    if (workbook.NodeType == XmlNodeType.Element && workbook.LocalName == "workbookPr")
                    {
                        date1904 = workbook.GetAttribute("date1904") is "1" or "true";
                        break;
                    }
                }
            }

            ZipArchiveEntry? entry = FindEntry(zip, "xl/styles.xml");
            if (entry == null)
            {
                return Array.Empty<bool>();
            }

            var customFormats = new Dictionary<int, bool>();
            var styles = new List<bool>();
            bool inCellFormats = false;
            using XmlReader reader = OpenXml(entry);
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "cellXfs")
                {
                    inCellFormats = false;
                }

                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                switch (reader.LocalName)
                {
                    case "numFmt":
                        if (int.TryParse(reader.GetAttribute("numFmtId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int formatId))
                        {
                            customFormats[formatId] = IsDateFormatCode(reader.GetAttribute("formatCode") ?? "");
                        }

                        break;
                    case "cellXfs":
                        inCellFormats = !reader.IsEmptyElement;
                        break;
                    case "xf" when inCellFormats:
                        int.TryParse(reader.GetAttribute("numFmtId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id);
                        styles.Add(customFormats.TryGetValue(id, out bool isDate) ? isDate : BuiltinDateFormats.Contains(id));
                        break;
                }
            }

            return styles.ToArray();
        }

        internal static bool IsDateFormatCode(string code)
        {
            bool quoted = false;
            bool bracketed = false;
            for (int index = 0; index < code.Length; index++)
            {
                char c = code[index];
                if (c == '"')
                {
                    quoted = !quoted;
                }
                else if (quoted)
                {
                    continue;
                }
                else if (c == '\\' || c == '_' || c == '*')
                {
                    index++;
                }
                else if (c == '[')
                {
                    bracketed = true;
                }
                else if (c == ']')
                {
                    bracketed = false;
                }
                else if (!bracketed && c is 'y' or 'Y' or 'd' or 'D' or 'h' or 'H' or 's' or 'S' or 'm' or 'M')
                {
                    return true;
                }
            }

            return false;
        }

        private static List<string[]> ReadSheet(ZipArchiveEntry entry, string[] sharedStrings, bool[] dateStyles, bool date1904)
        {
            var rows = new List<List<string>>();
            int rowIndex = -1;
            int columnIndex = -1;
            using XmlReader reader = OpenXml(entry);
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                if (reader.LocalName == "row")
                {
                    rowIndex = int.TryParse(reader.GetAttribute("r"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int rowNumber) ? rowNumber - 1 : rowIndex + 1;
                    columnIndex = -1;
                }
                else if (reader.LocalName == "c")
                {
                    string? reference = reader.GetAttribute("r");
                    columnIndex = reference != null ? ColumnIndex(reference) : columnIndex + 1;
                    string? type = reader.GetAttribute("t");
                    int.TryParse(reader.GetAttribute("s"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int style);
                    string text = ReadCell(reader, type, sharedStrings, style < dateStyles.Length && dateStyles[style], date1904);
                    if (text.Length > 0 && rowIndex >= 0 && columnIndex >= 0)
                    {
                        while (rows.Count <= rowIndex)
                        {
                            rows.Add(new List<string>());
                        }

                        List<string> row = rows[rowIndex];
                        while (row.Count <= columnIndex)
                        {
                            row.Add("");
                        }

                        row[columnIndex] = text;
                    }
                }
            }

            var grid = new List<string[]>(rows.Count);
            foreach (List<string> row in rows)
            {
                grid.Add(row.ToArray());
            }

            return grid;
        }

        private static string ReadCell(XmlReader reader, string? type, string[] sharedStrings, bool isDate, bool date1904)
        {
            if (reader.IsEmptyElement)
            {
                return "";
            }

            string? value = null;
            string? inline = null;
            int depth = reader.Depth;
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
                {
                    break;
                }

                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                if (reader.LocalName == "v")
                {
                    value = ReadInnerText(reader);
                }
                else if (reader.LocalName == "is")
                {
                    inline = ReadRichText(reader);
                }
                else if (reader.LocalName == "f" && !reader.IsEmptyElement)
                {
                    ReadInnerText(reader);
                }
            }

            switch (type)
            {
                case "s":
                    return value != null && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) && (uint)index < (uint)sharedStrings.Length
                        ? NormalizeLineBreaks(sharedStrings[index])
                        : "";
                case "inlineStr":
                    return NormalizeLineBreaks(inline ?? value ?? "");
                case "str":
                case "e":
                    return NormalizeLineBreaks(value ?? "");
                case "b":
                    return value == "1" ? "true" : "false";
                case "d":
                    return value ?? "";
                default:
                    if (string.IsNullOrEmpty(value))
                    {
                        return "";
                    }

                    if (!isDate && IsPlainInteger(value!))
                    {
                        return value!;
                    }

                    if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
                    {
                        return value!;
                    }

                    return isDate ? FormatDate(number, date1904) : FormatNumber(number);
            }
        }

        private static bool IsPlainInteger(string text)
        {
            int start = text[0] == '-' ? 1 : 0;
            if (text.Length == start || (text.Length > start + 1 && text[start] == '0'))
            {
                return false;
            }

            for (int index = start; index < text.Length; index++)
            {
                if (text[index] is < '0' or > '9')
                {
                    return false;
                }
            }

            return true;
        }

        public static string FormatNumber(double number)
        {
            if (Math.Floor(number) == number && Math.Abs(number) < MaxExactInteger)
            {
                return ((long)number).ToString(CultureInfo.InvariantCulture);
            }

            return number.ToString("R", CultureInfo.InvariantCulture);
        }

        private static string FormatDate(double serial, bool date1904)
        {
            DateTime value;
            try
            {
                value = DateTime.FromOADate(date1904 ? serial + 1462 : serial);
            }
            catch (ArgumentException)
            {
                return FormatNumber(serial);
            }

            // Round to the second: a time typed as 13:30 is stored as a fraction that lands a few ticks off.
            value = new DateTime((value.Ticks + TimeSpan.TicksPerSecond / 2) / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond);
            if (serial is >= 0 and < 1)
            {
                return value.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            }

            return value.TimeOfDay == TimeSpan.Zero
                ? value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        private static string NormalizeLineBreaks(string value)
        {
            return value.Contains('\r') ? value.Replace("\r\n", "\n").Replace('\r', '\n') : value;
        }

        private static int ColumnIndex(string reference)
        {
            int column = 0;
            foreach (char c in reference)
            {
                if (c is >= 'A' and <= 'Z')
                {
                    column = column * 26 + (c - 'A' + 1);
                }
                else if (c is >= 'a' and <= 'z')
                {
                    column = column * 26 + (c - 'a' + 1);
                }
                else
                {
                    break;
                }
            }

            return column - 1;
        }

        private static ZipArchiveEntry? FindEntry(ZipArchive zip, string name)
        {
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                if (string.Equals(entry.FullName.Replace('\\', '/'), name, StringComparison.OrdinalIgnoreCase))
                {
                    return entry;
                }
            }

            return null;
        }

        private static XmlReader OpenXml(ZipArchiveEntry entry)
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
                CloseInput = true
            };
            return XmlReader.Create(entry.Open(), settings);
        }
    }
}
