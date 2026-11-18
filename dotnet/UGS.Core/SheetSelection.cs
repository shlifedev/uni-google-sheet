namespace UGS.Core;

/// <summary>
/// The workbooks and sheets a partial build reads again. Everything else comes from the sheet cache, as it was at the last
/// build on this machine. Written as "Workbook" (every sheet in it) or "Workbook/Sheet".
/// </summary>
public sealed class SheetSelection
{
    private readonly HashSet<string> _workbooks = new(StringComparer.Ordinal);
    private readonly HashSet<(string Workbook, string Sheet)> _sheets = [];

    /// <summary>Workbooks selected as a whole.</summary>
    public IReadOnlyCollection<string> Workbooks => _workbooks;

    /// <summary>Single sheets, outside the workbooks selected as a whole.</summary>
    public IReadOnlyCollection<(string Workbook, string Sheet)> Sheets => _sheets;

    public bool IsEmpty => _workbooks.Count == 0 && _sheets.Count == 0;

    public SheetSelection AddWorkbook(string workbook)
    {
        _workbooks.Add(Guard.NotNull(workbook));
        _sheets.RemoveWhere(entry => entry.Workbook == workbook);
        return this;
    }

    public SheetSelection AddSheet(string workbook, string sheet)
    {
        if (!_workbooks.Contains(Guard.NotNull(workbook)))
        {
            _sheets.Add((workbook, Guard.NotNull(sheet)));
        }

        return this;
    }

    /// <summary>Parses "Workbook" and "Workbook/Sheet" entries. The first '/' separates the two; a workbook name has none.</summary>
    public static SheetSelection Parse(IEnumerable<string> entries)
    {
        var selection = new SheetSelection();
        foreach (string entry in entries)
        {
            foreach (string part in entry.Split(','))
            {
                string text = part.Trim();
                if (text.Length == 0)
                {
                    continue;
                }

                int slash = text.IndexOf('/');
                if (slash < 0)
                {
                    selection.AddWorkbook(text);
                }
                else
                {
                    selection.AddSheet(text.Substring(0, slash).Trim(), text.Substring(slash + 1).Trim());
                }
            }
        }

        return selection;
    }

    /// <summary>True when anything in the workbook is selected, so its source has to be read.</summary>
    public bool Touches(string workbook) => _workbooks.Contains(workbook) || _sheets.Any(entry => entry.Workbook == workbook);

    public bool IsWholeWorkbook(string workbook) => _workbooks.Contains(workbook);

    public bool Includes(string workbook, string sheet) => _workbooks.Contains(workbook) || _sheets.Contains((workbook, sheet));

    /// <summary>The number of entries: whole workbooks plus single sheets.</summary>
    public int Count => _workbooks.Count + _sheets.Count;

    public override string ToString()
    {
        return string.Join(", ", _workbooks.OrderBy(name => name, StringComparer.Ordinal)
            .Concat(_sheets.OrderBy(entry => entry.Workbook, StringComparer.Ordinal).ThenBy(entry => entry.Sheet, StringComparer.Ordinal).Select(entry => entry.Workbook + "/" + entry.Sheet)));
    }
}
