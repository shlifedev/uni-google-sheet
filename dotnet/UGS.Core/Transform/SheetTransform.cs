using System.Globalization;
using UGS.Core.Config;
using UGS.Core.Csv;
using UGS.Core.Schema;

namespace UGS.Core.Transform;

public enum SheetKind
{
    /// <summary>The name starts with # or ~. Not read.</summary>
    Ignored,

    /// <summary>"Item": defines the table Item.</summary>
    Table,

    /// <summary>"Game.Config": the table Game, written with one field per row instead of per column.</summary>
    Config,

    /// <summary>"Item.Weapons": more rows for the table Item, with the same columns.</summary>
    ChildRows,

    /// <summary>"Item.Col.Balance": more columns for the table Item, matched to its rows by key.</summary>
    ColumnExtension,

    /// <summary>"Enums" or "Battle.Enums": enum definitions.</summary>
    Enums,

    /// <summary>The name does not follow any of the forms above.</summary>
    Invalid
}

/// <summary>What a sheet is for, decided by its name alone. The naming rules live here and nowhere else.</summary>
public readonly record struct SheetRole(SheetKind Kind, string TableName)
{
    public const string NameRule = "A sheet name is one of: 'Table', 'Table.Config', 'Table.ChildName', 'Table.Col.GroupName', 'Enums', 'Something.Enums'. Start the name with # or ~ to have the sheet ignored.";

    private const string EnumsName = "Enums";
    private const string ConfigName = "Config";
    private const string ColumnExtensionName = "Col";

    public static SheetRole FromName(string sheetName)
    {
        string name = sheetName.Trim();
        if (name.Length == 0)
        {
            return new SheetRole(SheetKind.Invalid, "");
        }

        if (IsIgnoreMark(name))
        {
            return new SheetRole(SheetKind.Ignored, "");
        }

        string[] parts = name.Split('.');
        string last = parts[^1];
        if (last == EnumsName)
        {
            return new SheetRole(SheetKind.Enums, "");
        }

        return parts.Length switch
        {
            1 => new SheetRole(SheetKind.Table, name),
            2 when last == ConfigName => new SheetRole(SheetKind.Config, parts[0]),
            2 => new SheetRole(SheetKind.ChildRows, parts[0]),
            3 when parts[1] == ColumnExtensionName => new SheetRole(SheetKind.ColumnExtension, parts[0]),
            _ => new SheetRole(SheetKind.Invalid, "")
        };
    }

    /// <summary>
    /// Does the text start with an ignore mark (# or ~)? The same rule applies to a sheet name,
    /// to any header cell of a column, and to the first cell of a row.
    /// </summary>
    public static bool IsIgnoreMark(string text)
    {
        ReadOnlySpan<char> trimmed = text.AsSpan().TrimStart();
        return trimmed.Length > 0 && (trimmed[0] == '#' || trimmed[0] == '~');
    }
}

/// <summary>A column as written: its three header cells, still text.</summary>
public sealed class RawColumn(Cell typeCell, Cell nameCell, Cell commentCell)
{
    public Cell TypeCell { get; } = typeCell;

    public Cell NameCell { get; } = nameCell;

    public Cell CommentCell { get; } = commentCell;

    public string TypeText => TypeCell.Trimmed;

    public string Name => NameCell.Trimmed;

    public string Comment => CommentCell.Trimmed;
}

/// <summary>
/// A table after the transform rules and before types are resolved: the columns that will be read and the rows that hold data.
/// Ignored columns, ignored rows and blank rows are already gone; child sheets and column extensions are already merged in.
/// </summary>
public sealed class RawTable(string name, TableKind kind, string location)
{
    public string Name { get; } = name;

    public TableKind Kind { get; } = kind;

    public string Location { get; } = location;

    public List<RawColumn> Columns { get; } = [];

    /// <summary>Each row has one cell per entry of <see cref="Columns"/>.</summary>
    public List<Cell[]> Rows { get; } = [];

    /// <summary>Index of the first column whose type is Key&lt;...&gt;, or -1.</summary>
    public int FindKeyColumn()
    {
        for (int index = 0; index < Columns.Count; index++)
        {
            if (Columns[index].TypeText.StartsWith("Key<", StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>The table as rows of text in the configured header layout, for writing it out as CSV.</summary>
    public List<string[]> ToGrid(SheetConfig layout)
    {
        var grid = new List<string[]>(layout.FirstDataRow + Rows.Count);
        for (int headerRow = 0; headerRow < layout.FirstDataRow; headerRow++)
        {
            var header = new string[Columns.Count];
            for (int index = 0; index < Columns.Count; index++)
            {
                header[index] = headerRow == layout.TypeRow ? Columns[index].TypeText
                    : headerRow == layout.NameRow ? Columns[index].Name
                    : Columns[index].CommentCell.Text;
            }

            grid.Add(header);
        }

        foreach (Cell[] row in Rows)
        {
            var line = new string[row.Length];
            for (int index = 0; index < row.Length; index++)
            {
                line[index] = row[index].Text;
            }

            grid.Add(line);
        }

        return grid;
    }

    public string ToCsv(SheetConfig layout) => CsvWriter.Write(ToGrid(layout));
}

/// <summary>What the transform steps work on. Each step reads what earlier steps left and adds its own results.</summary>
public sealed class TransformContext(IReadOnlyList<Workbook> workbooks, SheetConfig layout, DiagnosticBag diagnostics)
{
    public IReadOnlyList<Workbook> Workbooks { get; } = workbooks;

    public SheetConfig Layout { get; } = layout;

    public DiagnosticBag Diagnostics { get; } = diagnostics;

    /// <summary>Every sheet that will be read, with its role. Filled by the classify step.</summary>
    public List<(Sheet Sheet, SheetRole Role)> Sheets { get; } = [];

    /// <summary>The tables found so far, in the order their sheets appear.</summary>
    public List<RawTable> Tables { get; } = [];

    public List<Sheet> EnumSheets { get; } = [];

    public RawTable? FindTable(string name) => Tables.FirstOrDefault(table => table.Name == name);
}

/// <summary>One rule that reshapes sheets before they are read as typed tables.</summary>
public interface ITransformStep
{
    /// <summary>A stable name, used to insert, replace or remove steps.</summary>
    string Name { get; }

    void Run(TransformContext context);
}

/// <summary>
/// The ordered rules that turn sheets as written into tables ready to be typed. The default order is:
/// <list type="number">
/// <item><c>classify</c>: decide each sheet's role from its name; drop ignored sheets.</item>
/// <item><c>transpose-config</c>: flip "Table.Config" sheets so they read like any other table.</item>
/// <item><c>read-tables</c>: read headers and rows; drop ignored columns, ignored rows and blank rows.</item>
/// <item><c>merge-child-rows</c>: append the rows of "Table.Child" sheets to their table.</item>
/// <item><c>join-column-extensions</c>: add the columns of "Table.Col.Group" sheets, matched by key.</item>
/// </list>
/// Repeated field names are merged into one array column later, when types are known.
/// Steps can be added, replaced or removed to support project-specific sheet conventions.
/// </summary>
public sealed class TransformPipeline
{
    public List<ITransformStep> Steps { get; } = [];

    public static TransformPipeline CreateDefault()
    {
        var pipeline = new TransformPipeline();
        pipeline.Steps.Add(new ClassifySheetsStep());
        pipeline.Steps.Add(new TransposeConfigStep());
        pipeline.Steps.Add(new ReadTablesStep());
        pipeline.Steps.Add(new MergeChildRowsStep());
        pipeline.Steps.Add(new JoinColumnExtensionsStep());
        return pipeline;
    }

    public TransformPipeline InsertBefore(string stepName, ITransformStep step)
    {
        Steps.Insert(IndexOf(stepName), step);
        return this;
    }

    public TransformPipeline InsertAfter(string stepName, ITransformStep step)
    {
        Steps.Insert(IndexOf(stepName) + 1, step);
        return this;
    }

    public TransformPipeline Replace(string stepName, ITransformStep step)
    {
        Steps[IndexOf(stepName)] = step;
        return this;
    }

    public TransformPipeline Remove(string stepName)
    {
        Steps.RemoveAt(IndexOf(stepName));
        return this;
    }

    public TransformContext Run(IReadOnlyList<Workbook> workbooks, SheetConfig layout, DiagnosticBag diagnostics)
    {
        var context = new TransformContext(workbooks, layout, diagnostics);
        foreach (ITransformStep step in Steps)
        {
            step.Run(context);
        }

        return context;
    }

    private int IndexOf(string stepName)
    {
        int index = Steps.FindIndex(step => step.Name == stepName);
        return index >= 0 ? index : throw new ArgumentException($"There is no transform step named '{stepName}'. Steps: {string.Join(", ", Steps.Select(step => step.Name))}.");
    }
}

public sealed class ClassifySheetsStep : ITransformStep
{
    public const string StepName = "classify";

    public string Name => StepName;

    public void Run(TransformContext context)
    {
        foreach (Workbook workbook in context.Workbooks)
        {
            foreach (Sheet sheet in workbook.Sheets)
            {
                SheetRole role = SheetRole.FromName(sheet.Name);
                switch (role.Kind)
                {
                    case SheetKind.Ignored:
                        break;
                    case SheetKind.Invalid:
                        context.Diagnostics.AddError(sheet.Location, "The sheet name is not understood.", SheetRole.NameRule);
                        break;
                    case SheetKind.Enums:
                        context.EnumSheets.Add(sheet);
                        break;
                    default:
                        if (!Identifier.IsValid(role.TableName))
                        {
                            context.Diagnostics.AddError(sheet.Location, $"'{role.TableName}' cannot be used as a table name.", Identifier.Rule + " Start the sheet name with # to have it ignored.");
                            break;
                        }

                        context.Sheets.Add((sheet, role));
                        break;
                }
            }
        }
    }
}

public sealed class TransposeConfigStep : ITransformStep
{
    public const string StepName = "transpose-config";

    public string Name => StepName;

    public void Run(TransformContext context)
    {
        for (int index = 0; index < context.Sheets.Count; index++)
        {
            (Sheet sheet, SheetRole role) = context.Sheets[index];
            if (role.Kind == SheetKind.Config)
            {
                context.Sheets[index] = (sheet.Transpose(), role);
            }
        }
    }
}

public sealed class ReadTablesStep : ITransformStep
{
    public const string StepName = "read-tables";

    public string Name => StepName;

    public void Run(TransformContext context)
    {
        foreach ((Sheet sheet, SheetRole role) in context.Sheets)
        {
            if (role.Kind is not (SheetKind.Table or SheetKind.Config))
            {
                continue;
            }

            if (context.FindTable(role.TableName) is { } existing)
            {
                context.Diagnostics.AddError(sheet.Location, $"The table '{role.TableName}' is already defined by '{existing.Location}'.",
                    $"Rename one of the sheets. To split one table's rows over several sheets, name the others '{role.TableName}.Something'.");
                continue;
            }

            context.Tables.Add(Read(sheet, role.TableName, role.Kind == SheetKind.Config ? TableKind.Config : TableKind.Table, context));
        }
    }

    /// <summary>
    /// Reads one sheet with the configured header rows. A column is skipped when any of its header cells starts with # or ~.
    /// A row is skipped when its first cell (column A, read or not) starts with # or ~, or when every read cell is blank.
    /// </summary>
    public static RawTable Read(Sheet sheet, string tableName, TableKind kind, TransformContext context)
    {
        SheetConfig layout = context.Layout;
        var table = new RawTable(tableName, kind, sheet.Location);
        var columnIndexes = new List<int>();
        var headerlessIndexes = new List<int>();
        for (int column = 0; column < sheet.ColumnCount; column++)
        {
            Cell typeCell = sheet.GetCell(layout.TypeRow, column);
            Cell nameCell = sheet.GetCell(layout.NameRow, column);
            Cell commentCell = layout.CommentRow >= 0 ? sheet.GetCell(layout.CommentRow, column) : new Cell("", nameCell.Origin);
            if (SheetRole.IsIgnoreMark(typeCell.Text) || SheetRole.IsIgnoreMark(nameCell.Text) || SheetRole.IsIgnoreMark(commentCell.Text))
            {
                continue;
            }

            if (typeCell.IsBlank && nameCell.IsBlank)
            {
                headerlessIndexes.Add(column);
                continue;
            }

            if (typeCell.IsBlank)
            {
                context.Diagnostics.AddError(typeCell, $"The field '{nameCell.Trimmed}' has no type.", "Write a type in the type row, for example int or string.");
                continue;
            }

            if (nameCell.IsBlank)
            {
                context.Diagnostics.AddError(nameCell, $"The column of type '{typeCell.Trimmed}' has no field name.", "Write a field name in the name row.");
                continue;
            }

            columnIndexes.Add(column);
            table.Columns.Add(new RawColumn(typeCell, nameCell, commentCell));
        }

        if (table.Columns.Count == 0)
        {
            context.Diagnostics.AddError(sheet.Location, "The sheet has no column to read.",
                $"The header rows are, top to bottom: {string.Join(", ", layout.Header)}. Start the sheet name with # to have it ignored.");
            return table;
        }

        bool warned = false;
        for (int rowIndex = layout.FirstDataRow; rowIndex < sheet.RowCount; rowIndex++)
        {
            if (SheetRole.IsIgnoreMark(sheet.GetCell(rowIndex, 0).Text))
            {
                continue;
            }

            if (!warned)
            {
                // Values under a column with no header usually mean the header was forgotten. Say so once per sheet.
                foreach (int column in headerlessIndexes)
                {
                    Cell cell = sheet.GetCell(rowIndex, column);
                    if (!cell.IsBlank)
                    {
                        context.Diagnostics.AddWarning(cell, "This column has values but no type and no field name, so it is not read.",
                            "Fill in the header if it should be read. If it is a note, start its header cell with #.");
                        warned = true;
                        break;
                    }
                }
            }

            var cells = new Cell[columnIndexes.Count];
            bool allBlank = true;
            for (int index = 0; index < cells.Length; index++)
            {
                cells[index] = sheet.GetCell(rowIndex, columnIndexes[index]);
                allBlank &= cells[index].IsBlank;
            }

            if (!allBlank)
            {
                table.Rows.Add(cells);
            }
        }

        return table;
    }
}

public sealed class MergeChildRowsStep : ITransformStep
{
    public const string StepName = "merge-child-rows";

    public string Name => StepName;

    public void Run(TransformContext context)
    {
        // Child sheets are merged in name order, not in the order the source lists them. An xlsx file lists its tabs in the
        // order the author arranged them, a CSV folder in file name order, and a Google spreadsheet in tab order online but
        // in file name order from its offline snapshot. Name order gives the same rows, and the same bytes, from all of them.
        foreach ((Sheet sheet, SheetRole role) in OrderedByName(context.Sheets, SheetKind.ChildRows))
        {
            RawTable? parent = context.FindTable(role.TableName);
            if (parent == null)
            {
                context.Diagnostics.AddError(sheet.Location, $"There is no table '{role.TableName}' for this sheet to add rows to.",
                    $"Add a sheet named '{role.TableName}', or fix this sheet's name.");
                continue;
            }

            Append(parent, ReadTablesStep.Read(sheet, parent.Name, parent.Kind, context), sheet, context.Diagnostics);
        }
    }

    internal static IEnumerable<(Sheet Sheet, SheetRole Role)> OrderedByName(IEnumerable<(Sheet Sheet, SheetRole Role)> sheets, SheetKind kind)
    {
        return sheets.Where(entry => entry.Role.Kind == kind)
            .OrderBy(entry => entry.Sheet.Name, StringComparer.Ordinal)
            .ThenBy(entry => entry.Sheet.Workbook, StringComparer.Ordinal);
    }

    // A child sheet must have the parent's columns, by field name and type. Their order may differ.
    // When a field name repeats, the repeats are paired in the order they appear.
    private static void Append(RawTable parent, RawTable child, Sheet childSheet, DiagnosticBag diagnostics)
    {
        if (child.Columns.Count == 0)
        {
            return;
        }

        var used = new bool[child.Columns.Count];
        var map = new int[parent.Columns.Count];
        bool ok = true;
        for (int parentIndex = 0; parentIndex < parent.Columns.Count; parentIndex++)
        {
            RawColumn column = parent.Columns[parentIndex];
            map[parentIndex] = -1;
            for (int childIndex = 0; childIndex < child.Columns.Count; childIndex++)
            {
                if (!used[childIndex] && child.Columns[childIndex].Name == column.Name && child.Columns[childIndex].TypeText == column.TypeText)
                {
                    used[childIndex] = true;
                    map[parentIndex] = childIndex;
                    break;
                }
            }

            if (map[parentIndex] < 0)
            {
                diagnostics.AddError(childSheet.Location, $"The column '{column.Name}' ({column.TypeText}) of table '{parent.Name}' is missing here.",
                    $"A sheet named '{parent.Name}.Something' adds rows to '{parent.Name}' and needs the same columns, with the same types.");
                ok = false;
            }
        }

        for (int childIndex = 0; childIndex < child.Columns.Count; childIndex++)
        {
            if (!used[childIndex])
            {
                diagnostics.AddError(child.Columns[childIndex].NameCell, $"The table '{parent.Name}' has no column '{child.Columns[childIndex].Name}' ({child.Columns[childIndex].TypeText}).",
                    $"Add the column to '{parent.Name}' too, or start this column's header with # to have it ignored.");
                ok = false;
            }
        }

        if (!ok)
        {
            return;
        }

        foreach (Cell[] row in child.Rows)
        {
            var cells = new Cell[map.Length];
            for (int index = 0; index < map.Length; index++)
            {
                cells[index] = row[map[index]];
            }

            parent.Rows.Add(cells);
        }
    }
}

public sealed class JoinColumnExtensionsStep : ITransformStep
{
    public const string StepName = "join-column-extensions";

    public string Name => StepName;

    public void Run(TransformContext context)
    {
        // Name order, for the same reason as child sheets: the added columns, and so the layout, must not depend on the source.
        foreach ((Sheet sheet, SheetRole role) in MergeChildRowsStep.OrderedByName(context.Sheets, SheetKind.ColumnExtension))
        {
            RawTable? parent = context.FindTable(role.TableName);
            if (parent == null)
            {
                context.Diagnostics.AddError(sheet.Location, $"There is no table '{role.TableName}' for this sheet to add columns to.",
                    $"Add a sheet named '{role.TableName}', or fix this sheet's name.");
                continue;
            }

            Join(parent, ReadTablesStep.Read(sheet, parent.Name, parent.Kind, context), sheet, context.Diagnostics);
        }
    }

    // Left join on the first Key column of each side. The extension may list only some of the parent's keys;
    // rows it does not list get blank cells in the added columns. A key the parent does not have is an error.
    private static void Join(RawTable parent, RawTable extension, Sheet extensionSheet, DiagnosticBag diagnostics)
    {
        if (extension.Columns.Count == 0)
        {
            return;
        }

        int parentKey = parent.FindKeyColumn();
        int extensionKey = extension.FindKeyColumn();
        if (parentKey < 0)
        {
            diagnostics.AddError(extensionSheet.Location, $"The table '{parent.Name}' has no Key<...> column, so columns cannot be matched to its rows.",
                $"Mark a column of '{parent.Name}' as Key<...>.");
            return;
        }

        if (extensionKey < 0)
        {
            diagnostics.AddError(extensionSheet.Location, "This sheet has no Key<...> column.",
                $"Repeat the key column of '{parent.Name}' here ({parent.Columns[parentKey].TypeText} {parent.Columns[parentKey].Name}) so rows can be matched.");
            return;
        }

        if (extension.Columns[extensionKey].TypeText != parent.Columns[parentKey].TypeText)
        {
            diagnostics.AddError(extension.Columns[extensionKey].TypeCell,
                $"The key type here is '{extension.Columns[extensionKey].TypeText}' but '{parent.Name}' uses '{parent.Columns[parentKey].TypeText}'.", "Use the same key type on both sheets.");
            return;
        }

        bool numeric = IsIntegerKey(parent.Columns[parentKey].TypeText);
        var extensionRows = new Dictionary<string, Cell[]>(StringComparer.Ordinal);
        bool ok = true;
        foreach (Cell[] row in extension.Rows)
        {
            string key = NormalizeKey(row[extensionKey].Text, numeric);
            if (key.Length == 0)
            {
                diagnostics.AddError(row[extensionKey], "The key is empty.", $"Write the key of the '{parent.Name}' row these values belong to.");
                ok = false;
            }
            else if (!extensionRows.TryAdd(key, row))
            {
                diagnostics.AddError(row[extensionKey], $"The key '{row[extensionKey].Trimmed}' appears twice on this sheet.", "Keep one row per key.");
                ok = false;
            }
        }

        var parentKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (Cell[] row in parent.Rows)
        {
            parentKeys.Add(NormalizeKey(row[parentKey].Text, numeric));
        }

        foreach (KeyValuePair<string, Cell[]> entry in extensionRows)
        {
            if (!parentKeys.Contains(entry.Key))
            {
                diagnostics.AddError(entry.Value[extensionKey], $"The table '{parent.Name}' has no row with key '{entry.Value[extensionKey].Trimmed}'.",
                    $"Add the row to '{parent.Name}', or remove it here.");
                ok = false;
            }
        }

        if (!ok)
        {
            return;
        }

        var added = new List<int>();
        for (int index = 0; index < extension.Columns.Count; index++)
        {
            if (index != extensionKey)
            {
                added.Add(index);
                parent.Columns.Add(extension.Columns[index]);
            }
        }

        for (int rowIndex = 0; rowIndex < parent.Rows.Count; rowIndex++)
        {
            Cell[] row = parent.Rows[rowIndex];
            var cells = new Cell[row.Length + added.Count];
            Array.Copy(row, cells, row.Length);
            extensionRows.TryGetValue(NormalizeKey(row[parentKey].Text, numeric), out Cell[]? match);
            for (int index = 0; index < added.Count; index++)
            {
                // A blank cell for a row the extension does not list is attributed to the parent's key cell,
                // so a problem with it still points at the row concerned.
                cells[row.Length + index] = match != null ? match[added[index]] : new Cell("", row[parentKey].Origin);
            }

            parent.Rows[rowIndex] = cells;
        }
    }

    private static bool IsIntegerKey(string typeText)
    {
        return TypeExpression.TryParse(typeText, out TypeExpression? expression, out _) && expression!.Kind == TypeExpressionKind.Named
            && Scalars.TryFind(expression.Name, out ScalarKind kind) && Scalars.IsInteger(kind);
    }

    // Keys are compared as values, not as text: 1 and 01 are the same integer.
    private static string NormalizeKey(string text, bool numeric)
    {
        string trimmed = text.Trim();
        if (numeric && decimal.TryParse(trimmed, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out decimal value))
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        return trimmed;
    }
}
