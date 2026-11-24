#nullable enable
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace HolySheet.Core
{
    public enum Severity
    {
        Warning,
        Error
    }

    public readonly struct CellOrigin : IEquatable<CellOrigin>
    {
        public CellOrigin(string Workbook, string Sheet, int Row, int Column)
        {
            this.Workbook = Workbook;
            this.Sheet = Sheet;
            this.Row = Row;
            this.Column = Column;
        }

        public string Workbook { get; init; }

        public string Sheet { get; init; }

        public int Row { get; init; }

        public int Column { get; init; }

        public bool Equals(CellOrigin other) =>
            string.Equals(Workbook, other.Workbook) && string.Equals(Sheet, other.Sheet) && Row == other.Row && Column == other.Column;

        public override bool Equals(object? obj) => obj is CellOrigin other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Workbook, Sheet, Row, Column);

        public static bool operator ==(CellOrigin left, CellOrigin right) => left.Equals(right);

        public static bool operator !=(CellOrigin left, CellOrigin right) => !left.Equals(right);

        public void Deconstruct(out string Workbook, out string Sheet, out int Row, out int Column)
        {
            Workbook = this.Workbook;
            Sheet = this.Sheet;
            Row = this.Row;
            Column = this.Column;
        }

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

    public sealed record Diagnostic(Severity Severity, string Location, CellOrigin? Origin, string Message, string Hint)
    {
        public override string ToString()
        {
            string label = Severity == Severity.Error ? "error" : "warning";
            return Hint.Length == 0 ? $"{label}: {Location}: {Message}" : $"{label}: {Location}: {Message}\n  fix: {Hint}";
        }
    }

    public sealed class DiagnosticBag
    {
        private readonly List<Diagnostic> _items = new();
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
}
