#nullable enable
using System;
using System.Collections.Generic;

namespace HolySheet.Core
{
    public readonly struct Cell : IEquatable<Cell>
    {
        public Cell(string Text, CellOrigin Origin)
        {
            this.Text = Text;
            this.Origin = Origin;
        }

        public string Text { get; init; }

        public CellOrigin Origin { get; init; }

        public bool IsBlank => string.IsNullOrWhiteSpace(Text);

        public string Trimmed => Text.Trim();

        public bool Equals(Cell other) => string.Equals(Text, other.Text) && Origin.Equals(other.Origin);

        public override bool Equals(object? obj) => obj is Cell other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Text, Origin);

        public static bool operator ==(Cell left, Cell right) => left.Equals(right);

        public static bool operator !=(Cell left, Cell right) => !left.Equals(right);

        public void Deconstruct(out string Text, out CellOrigin Origin)
        {
            Text = this.Text;
            Origin = this.Origin;
        }

        public override string ToString() => $"Cell {{ Text = {Text}, Origin = {Origin}, IsBlank = {IsBlank}, Trimmed = {Trimmed} }}";
    }

    public sealed class Sheet
    {
        private readonly IReadOnlyList<string[]> _grid;
        private readonly int _gridColumnCount;
        private readonly bool _transposed;

        public string Workbook { get; }

        public string Name { get; }

        public int RowCount => _transposed ? _gridColumnCount : _grid.Count;

        public int ColumnCount => _transposed ? _grid.Count : _gridColumnCount;

        public string Location => $"{Workbook} / {Name}";

        private Sheet(string workbook, string name, IReadOnlyList<string[]> grid, int gridColumnCount, bool transposed)
        {
            Workbook = workbook;
            Name = name;
            _grid = grid;
            _gridColumnCount = gridColumnCount;
            _transposed = transposed;
        }

        public static Sheet FromGrid(string workbook, string name, IReadOnlyList<string[]> grid)
        {
            int columnCount = 0;
            for (int rowIndex = 0; rowIndex < grid.Count; rowIndex++)
            {
                columnCount = Math.Max(columnCount, grid[rowIndex].Length);
            }

            return new Sheet(workbook, name, grid, columnCount, transposed: false);
        }

        public Cell GetCell(int row, int column)
        {
            int gridRow = _transposed ? column : row;
            int gridColumn = _transposed ? row : column;
            string text = "";
            if ((uint)gridRow < (uint)_grid.Count)
            {
                string[] cells = _grid[gridRow];
                if ((uint)gridColumn < (uint)cells.Length)
                {
                    text = cells[gridColumn] ?? "";
                }
            }

            return new Cell(text, new CellOrigin(Workbook, Name, gridRow, gridColumn));
        }

        public Sheet Transpose() => new(Workbook, Name, _grid, _gridColumnCount, !_transposed);

        public string[][] ToGrid()
        {
            var grid = new string[RowCount][];
            for (int rowIndex = 0; rowIndex < RowCount; rowIndex++)
            {
                var row = new string[ColumnCount];
                for (int column = 0; column < ColumnCount; column++)
                {
                    row[column] = GetCell(rowIndex, column).Text;
                }

                grid[rowIndex] = row;
            }

            return grid;
        }
    }

    public sealed class Workbook
    {
        public Workbook(string name, IReadOnlyList<Sheet> sheets, WorkbookOrigin? origin = null)
        {
            Name = name;
            Sheets = sheets;
            Origin = origin;
        }

        public string Name { get; }

        public IReadOnlyList<Sheet> Sheets { get; }

        public WorkbookOrigin? Origin { get; }

        public Workbook WithOrigin(WorkbookOrigin? origin) => new(Name, Sheets, origin);
    }

    public sealed record WorkbookOrigin(Config.SourceType Type, string Source, string? Link = null);
}
