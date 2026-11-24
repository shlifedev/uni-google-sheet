#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using HolySheet.Core.Config;
using HolySheet.Core.Schema;
using HolySheet.Core.Transform;

namespace HolySheet.Core.Building
{
    public static class EnumSheetReader
    {
        public static List<EnumDefinition> Read(Sheet sheet, SheetConfig layout, DiagnosticBag diagnostics)
        {
            var definitions = new List<EnumDefinition>();
            int column = 0;
            while (column < sheet.ColumnCount)
            {
                Cell nameCell = sheet.GetCell(layout.NameRow, column);
                Cell typeCell = sheet.GetCell(layout.TypeRow, column);
                Cell commentCell = layout.CommentRow >= 0 ? sheet.GetCell(layout.CommentRow, column) : new Cell("", nameCell.Origin);
                bool ignored = SheetRole.IsIgnoreMark(nameCell.Text) || SheetRole.IsIgnoreMark(typeCell.Text) || SheetRole.IsIgnoreMark(commentCell.Text);
                if (ignored || nameCell.IsBlank)
                {
                    column++;
                    continue;
                }

                bool hasValueColumn = column + 1 < sheet.ColumnCount && sheet.GetCell(layout.NameRow, column + 1).IsBlank
                    && !SheetRole.IsIgnoreMark(sheet.GetCell(layout.TypeRow, column + 1).Text)
                    && (layout.CommentRow < 0 || !SheetRole.IsIgnoreMark(sheet.GetCell(layout.CommentRow, column + 1).Text));
                EnumDefinition? definition = ReadEnum(sheet, layout, column, hasValueColumn ? column + 1 : -1, diagnostics);
                if (definition != null)
                {
                    definitions.Add(definition);
                }

                column += hasValueColumn ? 2 : 1;
            }

            return definitions;
        }

        private static EnumDefinition? ReadEnum(Sheet sheet, SheetConfig layout, int nameColumn, int valueColumn, DiagnosticBag diagnostics)
        {
            Cell nameCell = sheet.GetCell(layout.NameRow, nameColumn);
            Cell typeCell = sheet.GetCell(layout.TypeRow, nameColumn);
            string name = nameCell.Trimmed;
            bool valid = true;
            if (!Identifier.IsValid(name))
            {
                diagnostics.AddError(nameCell, $"'{name}' cannot be used as an enum name.", Identifier.Rule);
                valid = false;
            }

            string typeText = typeCell.Trimmed;
            bool isFlags = false;
            if (typeText == "Flags")
            {
                isFlags = true;
                typeText = "";
            }
            else if (typeText.StartsWith("Flags<", StringComparison.Ordinal) && typeText.EndsWith('>'))
            {
                isFlags = true;
                typeText = typeText[6..^1].Trim();
            }

            ScalarKind underlying = ScalarKind.Int;
            if (typeText.Length > 0 && !(Scalars.TryFind(typeText, out underlying) && Scalars.IsInteger(underlying)))
            {
                diagnostics.AddError(typeCell, $"'{typeCell.Trimmed}' is not an integer type for an enum.", "Use byte, sbyte, short, ushort, int, uint, long or ulong, optionally as Flags<type>. Blank means int.");
                valid = false;
                underlying = ScalarKind.Int;
            }

            var members = new List<EnumMember>();
            var byName = new Dictionary<string, long>(StringComparer.Ordinal);
            long next = 0;
            for (int row = layout.FirstDataRow; row < sheet.RowCount; row++)
            {
                Cell memberCell = sheet.GetCell(row, nameColumn);
                if (memberCell.IsBlank || SheetRole.IsIgnoreMark(memberCell.Text))
                {
                    continue;
                }

                string memberName = memberCell.Trimmed;
                if (!Identifier.IsValid(memberName))
                {
                    diagnostics.AddError(memberCell, $"'{memberName}' cannot be used as an enum member name.", Identifier.Rule + " Write a value in the column to its right, not as Name=Value.");
                    valid = false;
                    continue;
                }

                if (byName.ContainsKey(memberName))
                {
                    diagnostics.AddError(memberCell, $"The enum '{name}' already has a member '{memberName}'.", "Member names must be unique within an enum.");
                    valid = false;
                    continue;
                }

                long value = next;
                Cell valueCell = valueColumn >= 0 ? sheet.GetCell(row, valueColumn) : default;
                if (valueColumn >= 0 && !valueCell.IsBlank && !TryEvaluate(valueCell.Trimmed, byName, out value, out string reason))
                {
                    diagnostics.AddError(valueCell, $"The value '{valueCell.Trimmed}' {reason}.", "Write a whole number, a shift such as 1 << 3, or earlier members joined with |.");
                    valid = false;
                    continue;
                }

                if (!Scalars.TryFromInt64(underlying, value, out _))
                {
                    diagnostics.AddError(valueColumn >= 0 && !valueCell.IsBlank ? valueCell : memberCell, $"The value {value} of '{memberName}' does not fit in {Scalars.CSharpName(underlying)}.",
                        "Use a wider integer type in the type row, or a smaller value.");
                    valid = false;
                    continue;
                }

                byName.Add(memberName, value);
                members.Add(new EnumMember(memberName, value));
                next = value + 1;
            }

            if (valid && members.Count == 0)
            {
                diagnostics.AddError(nameCell, $"The enum '{name}' has no members.", "List member names under the enum name, one per row.");
                valid = false;
            }

            string comment = layout.CommentRow >= 0 ? sheet.GetCell(layout.CommentRow, nameColumn).Trimmed : "";
            return valid ? new EnumDefinition(name, underlying, isFlags, members, comment, nameCell.Origin.ToString()) : null;
        }

        internal static bool TryEvaluate(string text, IReadOnlyDictionary<string, long> members, out long value, out string reason)
        {
            value = 0;
            reason = "";
            foreach (string term in text.Split('|'))
            {
                string trimmed = term.Trim();
                int shift = trimmed.IndexOf("<<", StringComparison.Ordinal);
                if (shift >= 0)
                {
                    if (!TryAtom(trimmed[..shift].Trim(), members, out long left, out reason) || !TryAtom(trimmed[(shift + 2)..].Trim(), members, out long right, out reason))
                    {
                        return false;
                    }

                    if (right is < 0 or > 63)
                    {
                        reason = "shifts by an amount outside 0 to 63";
                        return false;
                    }

                    value |= left << (int)right;
                }
                else
                {
                    if (!TryAtom(trimmed, members, out long atom, out reason))
                    {
                        return false;
                    }

                    value |= atom;
                }
            }

            return true;
        }

        private static bool TryAtom(string text, IReadOnlyDictionary<string, long> members, out long value, out string reason)
        {
            reason = "";
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && long.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value))
            {
                return true;
            }

            if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value))
            {
                return true;
            }

            if (members.TryGetValue(text, out value))
            {
                return true;
            }

            reason = text.Length == 0 ? "has an empty part" : $"uses '{text}', which is neither a number nor an earlier member of this enum";
            return false;
        }
    }

    public static class MappingBuilder
    {
        public static List<EnumDefinition> Build(RawTable table, DiagnosticBag diagnostics)
        {
            var definitions = new List<EnumDefinition>();
            int keyIndex = table.FindKeyColumn();
            ScalarKind underlying = ScalarKind.Int;
            bool keyIsInteger = keyIndex >= 0 && TypeExpression.TryParse(table.Columns[keyIndex].TypeText, out TypeExpression? keyExpression, out _)
                && keyExpression!.Kind == TypeExpressionKind.Named && Scalars.TryFind(keyExpression.Name, out underlying) && Scalars.IsInteger(underlying);

            for (int index = 0; index < table.Columns.Count; index++)
            {
                RawColumn column = table.Columns[index];
                if (!TypeExpression.TryParse(column.TypeText, out TypeExpression? expression, out _) || expression!.Kind != TypeExpressionKind.Mapping)
                {
                    continue;
                }

                if (!Identifier.IsValid(expression.Name))
                {
                    diagnostics.AddError(column.TypeCell, $"'{expression.Name}' cannot be used as an enum name.", Identifier.Rule);
                    continue;
                }

                if (!keyIsInteger)
                {
                    diagnostics.AddError(column.TypeCell, $"Mapping<{expression.Name}> needs the table to have an integer key, which gives the members their values.",
                        "Make the first Key<...> column an integer type such as Key<int>.");
                    continue;
                }

                var members = new List<EnumMember>();
                var names = new HashSet<string>(StringComparer.Ordinal);
                bool valid = true;
                foreach (Cell[] row in table.Rows)
                {
                    Cell cell = row[index];
                    if (cell.IsBlank)
                    {
                        continue;
                    }

                    string memberName = cell.Trimmed;
                    if (!Identifier.IsValid(memberName))
                    {
                        diagnostics.AddError(cell, $"'{memberName}' cannot be used as an enum member name.", Identifier.Rule);
                        valid = false;
                        continue;
                    }

                    if (!names.Add(memberName))
                    {
                        diagnostics.AddError(cell, $"'{memberName}' appears twice in this Mapping column.", "Each row needs its own name, or a blank cell for rows that need no name.");
                        valid = false;
                        continue;
                    }

                    if (Scalars.TryParse(underlying, row[keyIndex].Text, out object key, out _) && !row[keyIndex].IsBlank)
                    {
                        members.Add(new EnumMember(memberName, Scalars.ToInt64(key)));
                    }
                }

                if (valid && members.Count > 0)
                {
                    definitions.Add(new EnumDefinition(expression.Name, underlying, false, members, column.Comment, column.NameCell.Origin.ToString()));
                }
                else if (valid)
                {
                    diagnostics.AddError(column.TypeCell, $"Mapping<{expression.Name}> has no names, so the enum would be empty.", "Fill in at least one name, or remove the column.");
                }
            }

            return definitions;
        }
    }
}
