using System.Text;
using System.Text.Json.Nodes;
using HolySheet.Core;
using HolySheet.Core.Schema;
using HolySheet.Core.Transform;

namespace HolySheet.Desktop.Host;

/// <summary>
/// The tables as read by the last run, shaped for the app's tree and data view. The rules follow the Unity window
/// (HolySheetWindow: SheetRefsOf, DescribeSheet, ShowPreview, TypeClass) so both tools show a sheet the same way.
/// </summary>
public static class TableView
{
    public static JsonObject Empty() => new()
    {
        ["workbooks"] = new JsonArray(),
        ["tables"] = new JsonArray(),
        ["enums"] = new JsonArray()
    };

    /// <summary>Workbooks with where they came from, tables with the sheets they were built from, and every enum.</summary>
    public static JsonObject Model(BuildResult build)
    {
        var workbookNames = new List<string>();
        void AddWorkbook(string name)
        {
            if (!workbookNames.Contains(name))
            {
                workbookNames.Add(name);
            }
        }

        var tables = new JsonArray();
        foreach (RawTable table in build.RawTables)
        {
            string workbook = WorkbookOf(table.Location);
            AddWorkbook(workbook);
            tables.Add(new JsonObject
            {
                ["name"] = table.Name,
                ["kind"] = table.Kind == TableKind.Config ? "config" : "table",
                ["location"] = table.Location,
                ["workbook"] = workbook,
                ["rows"] = table.Rows.Count,
                ["columns"] = table.Columns.Count,
                ["sheets"] = Json.Array(SheetRefsOf(table).Select(entry => (JsonNode?)new JsonObject
                {
                    ["workbook"] = entry.Workbook,
                    ["sheet"] = entry.Sheet,
                    ["role"] = SheetRole(table, entry.Sheet)
                }))
            });
        }

        var enums = new JsonArray();
        foreach (EnumDefinition definition in build.Enums)
        {
            (string workbook, string sheet) = SplitLocation(definition.Location);
            bool mapping = !IsEnumsSheet(sheet);
            if (!mapping)
            {
                AddWorkbook(workbook);
            }

            enums.Add(new JsonObject
            {
                ["name"] = definition.Name,
                ["underlying"] = definition.Underlying.ToString().ToLowerInvariant(),
                ["isFlags"] = definition.IsFlags,
                ["comment"] = definition.Comment,
                ["workbook"] = workbook,
                ["sheet"] = sheet,
                ["mapping"] = mapping,
                ["members"] = Json.Array(definition.Members.Select(member => (JsonNode?)new JsonObject
                {
                    ["name"] = member.Name,
                    ["value"] = member.Value,
                    ["display"] = FormatEnumValue(definition, member.Value),
                    ["comment"] = member.Comment
                }))
            });
        }

        return new JsonObject
        {
            ["workbooks"] = Json.Array(workbookNames.Select(name =>
            {
                build.WorkbookOrigins.TryGetValue(name, out WorkbookOrigin? origin);
                return (JsonNode?)new JsonObject
                {
                    ["name"] = name,
                    ["type"] = origin == null ? null : Json.SourceTypeName(origin.Type),
                    ["source"] = origin?.Source ?? "",
                    ["link"] = origin?.Link
                };
            })),
            ["tables"] = tables,
            ["enums"] = enums
        };
    }

    /// <summary>
    /// A table, or the part of it that came from one sheet: that sheet's rows, and its own columns when it is a column
    /// extension (plus the key they are matched by). Rows carry the sheet's own row number, so they match the problems.
    /// </summary>
    public static JsonObject Preview(BuildResult build, string tableName, string? sheet)
    {
        RawTable table = build.RawTables.FirstOrDefault(candidate => candidate.Name == tableName)
            ?? throw new RpcException(RpcException.ToolError, $"There is no table '{tableName}' in the last run.");
        List<Cell[]> rows = table.Rows;
        List<int> columns = Enumerable.Range(0, table.Columns.Count).ToList();
        string location = table.Location;
        if (sheet != null)
        {
            List<Cell[]> fromSheet = table.Rows.Where(row => row.Length > 0 && row[0].Origin.Sheet == sheet).ToList();
            List<int> ownColumns = columns.Where(index => table.Columns[index].NameCell.Origin.Sheet == sheet).ToList();
            bool columnExtension = fromSheet.Count == 0 && ownColumns.Count > 0;
            if (columnExtension)
            {
                int key = table.FindKeyColumn();
                if (key >= 0 && !ownColumns.Contains(key))
                {
                    ownColumns.Insert(0, key);
                }

                columns = ownColumns;
            }
            else
            {
                rows = fromSheet;
                if (ownColumns.Count > 0 && ownColumns.Count < columns.Count && fromSheet.Count > 0)
                {
                    // The defining sheet: hide the columns that other sheets added.
                    columns = ownColumns;
                }
            }

            location = WorkbookOf(table.Location) + " / " + sheet;
        }

        // Rows another sheet added (Item.Rare) carry that sheet's short name, so "Rare 4" is not mistaken for row 4 of Item.
        string definingSheet = table.Columns.Count > 0 ? table.Columns[0].NameCell.Origin.Sheet : "";
        string RowLabel(Cell[] row)
        {
            if (row.Length == 0)
            {
                return "";
            }

            string number = (row[0].Origin.Row + 1).ToString();
            string from = row[0].Origin.Sheet;
            if (sheet != null || from == definingSheet)
            {
                return number;
            }

            int dot = from.LastIndexOf('.');
            return (dot >= 0 ? from[(dot + 1)..] : from) + " " + number;
        }

        int keyColumn = table.FindKeyColumn();
        var columnArray = new JsonArray();
        foreach (int index in columns)
        {
            RawColumn raw = table.Columns[index];
            string typeClass = TypeClass(raw.TypeText);
            bool isArray = IsArray(raw.TypeText);
            columnArray.Add(new JsonObject
            {
                ["name"] = raw.Name,
                ["type"] = raw.TypeText,
                ["comment"] = raw.Comment,
                ["sheet"] = raw.NameCell.Origin.Sheet,
                ["letter"] = ColumnLetters(raw.NameCell.Origin.Column),
                ["typeClass"] = typeClass,
                ["isArray"] = isArray,
                ["sortKind"] = isArray ? "text" : typeClass is "int" or "float" ? "number" : typeClass == "bool" ? "bool" : "text",
                ["number"] = typeClass is "int" or "float" && !isArray,
                ["key"] = index == keyColumn
            });
        }

        var rowArray = new JsonArray();
        foreach (Cell[] row in rows)
        {
            var cells = new JsonArray();
            foreach (int index in columns)
            {
                cells.Add(index < row.Length ? row[index].Text : "");
            }

            rowArray.Add(new JsonObject
            {
                ["label"] = RowLabel(row),
                ["sheet"] = row.Length > 0 ? row[0].Origin.Sheet : "",
                ["row"] = row.Length > 0 ? row[0].Origin.Row + 1 : 0,
                ["cells"] = cells
            });
        }

        return new JsonObject
        {
            ["table"] = table.Name,
            ["sheet"] = sheet,
            ["kind"] = table.Kind == TableKind.Config ? "config" : "table",
            ["workbook"] = WorkbookOf(table.Location),
            ["location"] = location,
            ["definingSheet"] = definingSheet,
            ["keyColumn"] = columns.IndexOf(keyColumn),
            ["columns"] = columnArray,
            ["rows"] = rowArray
        };
    }

    /// <summary>The sheets a table was assembled from, in order: its columns' sheets, then its rows' sheets.</summary>
    public static List<(string Workbook, string Sheet)> SheetRefsOf(RawTable table)
    {
        var found = new List<(string Workbook, string Sheet)>();
        void Add(CellOrigin origin)
        {
            if (!found.Contains((origin.Workbook, origin.Sheet)))
            {
                found.Add((origin.Workbook, origin.Sheet));
            }
        }

        // Extra columns come from Table.Col.* sheets, extra rows from Table.Child sheets.
        foreach (RawColumn column in table.Columns)
        {
            Add(column.NameCell.Origin);
        }

        foreach (Cell[] row in table.Rows)
        {
            if (row.Length > 0)
            {
                Add(row[0].Origin);
            }
        }

        return found;
    }

    /// <summary>"defines", "rows" or "columns": what a sheet adds to the table it belongs to.</summary>
    public static string SheetRole(RawTable table, string sheet)
    {
        bool addsColumns = table.Columns.Any(column => column.NameCell.Origin.Sheet == sheet) && table.Rows.Count > 0 && table.Rows[0].Length > 0 && table.Rows[0][0].Origin.Sheet != sheet
            && !table.Rows.Any(row => row.Length > 0 && row[0].Origin.Sheet == sheet);
        if (addsColumns)
        {
            return "columns";
        }

        return sheet == table.Name || sheet.StartsWith(table.Name + ".", StringComparison.Ordinal) && table.Columns.Count > 0 && table.Columns[0].NameCell.Origin.Sheet == sheet
            ? "defines"
            : "rows";
    }

    /// <summary>The color group of a column type: int, float, bool, date, enum, ref, mapping, or "" for text.</summary>
    public static string TypeClass(string typeText)
    {
        if (!TypeExpression.TryParse(typeText, out TypeExpression? expression, out _) || expression == null)
        {
            return "";
        }

        switch (expression.Kind)
        {
            case TypeExpressionKind.Enum:
                return "enum";
            case TypeExpressionKind.Reference:
                return "ref";
            case TypeExpressionKind.Mapping:
                return "mapping";
        }

        if (!Scalars.TryFind(expression.Name, out ScalarKind kind))
        {
            // A bare name that is not a value type is an enum.
            return "enum";
        }

        return kind switch
        {
            ScalarKind.Bool => "bool",
            ScalarKind.Float or ScalarKind.Double => "float",
            ScalarKind.String => "",
            ScalarKind.DateTime => "date",
            _ => "int"
        };
    }

    private static bool IsArray(string typeText) => TypeExpression.TryParse(typeText, out TypeExpression? expression, out _) && expression != null && expression.IsArray;

    // Flags read better as bits: "8  (1 << 3)".
    private static string FormatEnumValue(EnumDefinition definition, long value)
    {
        if (definition.IsFlags && value > 0 && (value & (value - 1)) == 0)
        {
            int bit = 0;
            while ((1L << bit) != value)
            {
                bit++;
            }

            return value + "  (1 << " + bit + ")";
        }

        return value.ToString();
    }

    // A sheet's location is "Workbook / Sheet". A workbook is a file or a spreadsheet title, which cannot contain '/'.
    public static string WorkbookOf(string location)
    {
        int separator = location.IndexOf(" / ", StringComparison.Ordinal);
        return separator < 0 ? location : location[..separator];
    }

    // "Book / Sheet!B2" or "Book / Sheet" into its workbook and sheet.
    public static (string Workbook, string Sheet) SplitLocation(string location)
    {
        int separator = location.IndexOf(" / ", StringComparison.Ordinal);
        if (separator < 0)
        {
            return (location, "");
        }

        string sheet = location[(separator + 3)..];
        int cell = sheet.LastIndexOf('!');
        return (location[..separator], cell >= 0 ? sheet[..cell] : sheet);
    }

    // The sheet rule: "Enums", or a name ending in ".Enums" (Battle.Enums).
    public static bool IsEnumsSheet(string sheet) => sheet == "Enums" || sheet.EndsWith(".Enums", StringComparison.Ordinal);

    private static string ColumnLetters(int column)
    {
        var builder = new StringBuilder();
        for (int value = column + 1; value > 0; value = (value - 1) / 26)
        {
            builder.Insert(0, (char)('A' + (value - 1) % 26));
        }

        return builder.ToString();
    }
}
