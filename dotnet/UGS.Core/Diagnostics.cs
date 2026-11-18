using System.Text;

namespace UGS.Core;

public enum Severity
{
    Warning,
    Error
}

/// <summary>Where a cell came from: zero-based row and column in the sheet as the author sees it.</summary>
public readonly record struct CellOrigin(string Workbook, string Sheet, int Row, int Column)
{
    /// <summary>Spreadsheet notation for the cell, such as "B4".</summary>
    public string A1 => ColumnName(Column) + (Row + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

    public override string ToString() => $"{Workbook} / {Sheet}!{A1}";

    public static string ColumnName(int column)
    {
        var builder = new StringBuilder();
        for (int value = column + 1; value > 0; value = (value - 1) / 26)
        {
            builder.Insert(0, (char)('A' + (value - 1) % 26));
        }

        return builder.ToString();
    }
}

/// <summary>One problem found while building. Every problem says where it is, what is wrong and how to fix it.</summary>
public sealed record Diagnostic(Severity Severity, string Location, CellOrigin? Origin, string Message, string Hint)
{
    public override string ToString()
    {
        string label = Severity == Severity.Error ? "error" : "warning";
        return Hint.Length == 0 ? $"{label}: {Location}: {Message}" : $"{label}: {Location}: {Message}\n  fix: {Hint}";
    }
}

/// <summary>Collects diagnostics. Safe to add to from several threads; the order of items added by one thread is kept.</summary>
public sealed class DiagnosticBag
{
    private readonly List<Diagnostic> _items = [];
    private readonly object _gate = new();
    private int _errorCount;

    public IReadOnlyList<Diagnostic> Items
    {
        get
        {
            lock (_gate)
            {
                return _items.ToArray();
            }
        }
    }

    public bool HasErrors => Volatile.Read(ref _errorCount) > 0;

    public int ErrorCount => Volatile.Read(ref _errorCount);

    public int WarningCount
    {
        get
        {
            lock (_gate)
            {
                return _items.Count - _errorCount;
            }
        }
    }

    public void Add(Diagnostic diagnostic)
    {
        lock (_gate)
        {
            _items.Add(diagnostic);
            if (diagnostic.Severity == Severity.Error)
            {
                _errorCount++;
            }
        }
    }

    public void AddRange(DiagnosticBag other)
    {
        foreach (Diagnostic diagnostic in other.Items)
        {
            Add(diagnostic);
        }
    }

    public void AddError(Cell cell, string message, string hint = "") =>
        Add(new Diagnostic(Severity.Error, cell.Origin.ToString(), cell.Origin, message, hint));

    public void AddWarning(Cell cell, string message, string hint = "") =>
        Add(new Diagnostic(Severity.Warning, cell.Origin.ToString(), cell.Origin, message, hint));

    public void AddError(string location, string message, string hint = "") =>
        Add(new Diagnostic(Severity.Error, location, null, message, hint));

    public void AddWarning(string location, string message, string hint = "") =>
        Add(new Diagnostic(Severity.Warning, location, null, message, hint));
}
