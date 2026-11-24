#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace HolySheet.Core
{
    public sealed class SheetSelection
    {
        private readonly HashSet<string> _workbooks = new(StringComparer.Ordinal);
        private readonly HashSet<(string Workbook, string Sheet)> _sheets = new();

        public IReadOnlyCollection<string> Workbooks => _workbooks;

        public IReadOnlyCollection<(string Workbook, string Sheet)> Sheets => _sheets;

        public bool IsEmpty => _workbooks.Count == 0 && _sheets.Count == 0;

        public SheetSelection AddWorkbook(string workbook)
        {
            _workbooks.Add(Guard.NotNull(workbook, nameof(workbook)));
            _sheets.RemoveWhere(entry => entry.Workbook == workbook);
            return this;
        }

        public SheetSelection AddSheet(string workbook, string sheet)
        {
            if (!_workbooks.Contains(Guard.NotNull(workbook, nameof(workbook))))
            {
                _sheets.Add((workbook, Guard.NotNull(sheet, nameof(sheet))));
            }

            return this;
        }

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

        public bool Touches(string workbook) => _workbooks.Contains(workbook) || _sheets.Any(entry => entry.Workbook == workbook);

        public bool IsWholeWorkbook(string workbook) => _workbooks.Contains(workbook);

        public bool Includes(string workbook, string sheet) => _workbooks.Contains(workbook) || _sheets.Contains((workbook, sheet));

        public int Count => _workbooks.Count + _sheets.Count;

        public override string ToString()
        {
            return string.Join(", ", _workbooks.OrderBy(name => name, StringComparer.Ordinal)
                .Concat(_sheets.OrderBy(entry => entry.Workbook, StringComparer.Ordinal).ThenBy(entry => entry.Sheet, StringComparer.Ordinal).Select(entry => entry.Workbook + "/" + entry.Sheet)));
        }
    }
}
