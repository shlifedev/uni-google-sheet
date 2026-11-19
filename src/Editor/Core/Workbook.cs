#nullable enable
using System;
using System.Collections.Generic;

namespace HolySheet.Core
{
    /// <summary>A cell's text and where it came from. Every later stage works on this text only.</summary>
    // A record struct by intent; written out by hand because Unity compiles C# 9. Equality, ToString and Deconstruct match what the compiler would generate.
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

    /// <summary>
    /// One sheet (tab) as a rectangular grid of text. Reading past the edge gives a blank cell, never an exception.
    /// The sheet keeps the rows of text it was given and makes a <see cref="Cell"/> only when one is asked for,
    /// so a large sheet costs its text and nothing more.
    /// </summary>
    public sealed class Sheet
    {
        private readonly IReadOnlyList<string[]> _grid;
        private readonly int _gridColumnCount;
        private readonly bool _transposed;

        public string Workbook { get; }

        public string Name { get; }

        public int RowCount => _transposed ? _gridColumnCount : _grid.Count;

        public int ColumnCount => _transposed ? _grid.Count : _gridColumnCount;

        /// <summary>"Workbook / Sheet", used as the location of problems that concern the whole sheet.</summary>
        public string Location => $"{Workbook} / {Name}";

        private Sheet(string workbook, string name, IReadOnlyList<string[]> grid, int gridColumnCount, bool transposed)
        {
            Workbook = workbook;
            Name = name;
            _grid = grid;
            _gridColumnCount = gridColumnCount;
            _transposed = transposed;
        }

        /// <summary>Builds a sheet from rows of text. Rows may have different lengths. The rows are used as they are, not copied.</summary>
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
            // A cell's origin is always its position in the sheet as the author sees it, transposed or not.
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

        /// <summary>
        /// Swaps rows and columns. Cells keep their original origin, so a problem in a transposed sheet
        /// still points at the cell the author typed into.
        /// </summary>
        public Sheet Transpose() => new(Workbook, Name, _grid, _gridColumnCount, !_transposed);

        /// <summary>The grid as plain text, padded to a rectangle.</summary>
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

    /// <summary>A spreadsheet file or a Google spreadsheet: a named list of sheets.</summary>
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

        /// <summary>The kind of source the workbook was read from. Null for workbooks a host supplied itself.</summary>
        public WorkbookOrigin? Origin { get; }

        public Workbook WithOrigin(WorkbookOrigin? origin) => new(Name, Sheets, origin);
    }

    /// <summary>
    /// Where a workbook came from: the kind of source, the path, spreadsheet id or folder id in the config, and for files read
    /// from Google, the address that opens that file in a browser.
    /// </summary>
    public sealed record WorkbookOrigin(Config.SourceType Type, string Source, string? Link = null);
}
