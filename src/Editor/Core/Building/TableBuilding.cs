#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HolySheet.Core.Schema;
using HolySheet.Core.Transform;

namespace HolySheet.Core.Building
{
    public static class SchemaBuilder
    {
        public const int MaxStoredMembers = 249;

        private static readonly HashSet<string> ReservedFields = new(StringComparer.Ordinal)
        {
            "ToString", "Equals", "GetHashCode", "GetType", "MemberwiseClone", "Finalize", "ReferenceEquals"
        };

        public static List<(TableSchema Schema, RawTable Raw)> Build(IReadOnlyList<RawTable> tables, TypeResolver resolver, DiagnosticBag diagnostics)
        {
            var keyTypes = new Dictionary<string, ColumnType?>(StringComparer.Ordinal);
            foreach (RawTable table in tables)
            {
                int keyIndex = table.FindKeyColumn();
                ColumnType? keyType = null;
                if (keyIndex >= 0 && TypeExpression.TryParse(table.Columns[keyIndex].TypeText, out TypeExpression? expression, out _)
                    && resolver.TryResolve(expression!, NoReferences, out ColumnType? resolved, out _, out _) && resolved!.CanBeKey)
                {
                    keyType = resolved;
                }

                keyTypes[table.Name] = keyType;
            }

            ColumnType? LookUpKey(string tableName, out bool tableExists)
            {
                tableExists = keyTypes.TryGetValue(tableName, out ColumnType? keyType);
                return keyType;
            }

            var result = new List<(TableSchema, RawTable)>(tables.Count);
            foreach (RawTable table in tables)
            {
                TableSchema? schema = BuildTable(table, resolver, LookUpKey, diagnostics);
                if (schema != null)
                {
                    result.Add((schema, table));
                }
            }

            return result;
        }

        private static ColumnType? NoReferences(string tableName, out bool tableExists)
        {
            tableExists = false;
            return null;
        }

        private static TableSchema? BuildTable(RawTable table, TypeResolver resolver, TypeResolver.KeyLookup keyLookup, DiagnosticBag diagnostics)
        {
            if (table.Columns.Count == 0)
            {
                return null;
            }

            var columns = new List<ColumnSchema>();
            var byField = new Dictionary<string, int>(StringComparer.Ordinal);
            var byFieldIgnoringCase = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var mergedSources = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            bool valid = true;
            for (int index = 0; index < table.Columns.Count; index++)
            {
                RawColumn raw = table.Columns[index];
                if (!TypeExpression.TryParse(raw.TypeText, out TypeExpression? expression, out string reason))
                {
                    diagnostics.AddError(raw.TypeCell, reason);
                    valid = false;
                    continue;
                }

                if (expression!.Kind == TypeExpressionKind.Mapping)
                {
                    continue;
                }

                string field = raw.Name;
                if (!Identifier.IsValid(field))
                {
                    diagnostics.AddError(raw.NameCell, $"'{field}' cannot be used as a field name.", Identifier.Rule);
                    valid = false;
                    continue;
                }

                if (ReservedFields.Contains(field) || field == table.Name + "Row")
                {
                    diagnostics.AddError(raw.NameCell, $"'{field}' is a name the generated row class already uses.", "Choose another field name.");
                    valid = false;
                    continue;
                }

                if (!resolver.TryResolve(expression, keyLookup, out ColumnType? type, out reason, out string hint))
                {
                    diagnostics.AddError(raw.TypeCell, reason, hint);
                    valid = false;
                    continue;
                }

                if (expression.Role != ColumnRole.None && !(expression.Role == ColumnRole.Key ? type!.CanBeKey : type!.Kind != ColumnKind.Reference))
                {
                    diagnostics.AddError(raw.TypeCell, $"A {type.CanonicalName} column cannot be a key.", "Keys are whole numbers, text or enums. Decimal numbers and dates do not compare exactly.");
                    valid = false;
                    continue;
                }

                if (byField.TryGetValue(field, out int existingIndex))
                {
                    ColumnSchema existing = columns[existingIndex];
                    if (existing.Role != ColumnRole.None || expression.Role != ColumnRole.None)
                    {
                        diagnostics.AddError(raw.NameCell, $"The field '{field}' appears more than once, but a key or index column must be a single column.", "Rename one of the columns.");
                        valid = false;
                    }
                    else if (existing.Type.ElementType.CanonicalName != type!.ElementType.CanonicalName)
                    {
                        diagnostics.AddError(raw.TypeCell, $"The field '{field}' appears more than once with different types ({existing.Type.ElementType.CanonicalName} and {type.ElementType.CanonicalName}).",
                            "Columns that share a field name are merged into one array, so they need the same type.");
                        valid = false;
                    }
                    else
                    {
                        mergedSources[field].Add(index);
                        columns[existingIndex] = new ColumnSchema(field, existing.Type.IsArray ? existing.Type : existing.Type.AsArray(), ColumnRole.None, existing.Comment, existing.NameCell, mergedSources[field].ToArray());
                    }

                    continue;
                }

                if (byFieldIgnoringCase.TryGetValue(field, out string? other))
                {
                    diagnostics.AddError(raw.NameCell, $"The fields '{other}' and '{field}' differ only by letter case.", "Rename one of them.");
                    valid = false;
                    continue;
                }

                byField.Add(field, columns.Count);
                byFieldIgnoringCase.Add(field, field);
                mergedSources[field] = new List<int> { index };
                columns.Add(new ColumnSchema(field, type!, expression.Role, raw.Comment, raw.NameCell, new[] { index }));
            }

            if (!valid)
            {
                return null;
            }

            if (columns.Count == 0)
            {
                diagnostics.AddError(table.Location, $"The table '{table.Name}' has no data column.", "A table needs at least one column besides Mapping<...> columns.");
                return null;
            }

            var schema = new TableSchema(table.Name, table.Kind, table.Location, columns);
            if (schema.StoredMemberCount > MaxStoredMembers)
            {
                diagnostics.AddError(table.Location, $"The table '{table.Name}' stores {schema.StoredMemberCount} values per row; the limit is {MaxStoredMembers}.",
                    "Split the table, or move a group of columns into arrays.");
                return null;
            }

            return schema;
        }
    }

    public static class CellParser
    {
        public static bool TryParse(ColumnType type, string text, char arraySeparator, out object value, out string reason)
        {
            if (type.IsArray)
            {
                var elements = new List<object>();
                bool ok = TryParseElements(type.ElementType, text, arraySeparator, elements, out reason);
                value = elements.ToArray();
                return ok;
            }

            return TryParseSingle(type, text, out value, out reason);
        }

        public static bool TryParseElements(ColumnType elementType, string text, char arraySeparator, List<object> elements, out string reason)
        {
            var parts = new List<ArrayElement>();
            if (!ArraySplitter.TrySplit(text, arraySeparator, parts, out char separator, out reason))
            {
                return false;
            }

            for (int index = 0; index < parts.Count; index++)
            {
                ArrayElement part = parts[index];
                if (part.Text.Length == 0 && !part.Quoted)
                {
                    reason = $"has an empty element at position {index + 1}. Check for a '{separator}' at the start or end, or two in a row";
                    return false;
                }

                if (!TryParseSingle(elementType, part.Text, out object element, out string elementReason, isElement: true))
                {
                    reason = $"has a bad element at position {index + 1}: '{part.Text}' {elementReason}";
                    return false;
                }

                elements.Add(element);
            }

            return true;
        }

        public static string? AmbiguityWarning(ColumnType elementType, string text, char arraySeparator, out string hint)
        {
            hint = "";
            string trimmed = text.Trim();
            if (trimmed.Length == 0 || ArraySplitter.IsBracketed(trimmed))
            {
                return null;
            }

            if (arraySeparator == ',' && IsNumeric(elementType) && LooksLikeGroupedNumber(trimmed))
            {
                hint = $"If it is one number, format the column as plain text or turn off the thousands separator in the sheet. If it is a list, write it in brackets: [{trimmed}].";
                return $"'{trimmed}' looks like one number with thousands separators, but it was read as {trimmed.Split(',').Length} elements.";
            }

            if (arraySeparator != '|' && elementType.Kind == ColumnKind.Scalar && elementType.Scalar == ScalarKind.String
                && ArraySplitter.ContainsOutsideQuotes(trimmed, '|') && !ArraySplitter.ContainsOutsideQuotes(trimmed, arraySeparator))
            {
                hint = $"Separate the elements with '{arraySeparator}'. If the '|' is part of the text, put the element in quotes: \"{trimmed.Replace("\"", "\\\"")}\".";
                return $"'{trimmed}' contains '|', but arrays are separated by '{arraySeparator}', so it was read as one element.";
            }

            return null;
        }

        private static bool IsNumeric(ColumnType elementType)
        {
            ColumnType value = elementType.Kind == ColumnKind.Reference ? elementType.KeyType! : elementType;
            return value.Kind == ColumnKind.Scalar && (Scalars.IsInteger(value.Scalar) || value.Scalar is ScalarKind.Float or ScalarKind.Double);
        }

        private static bool LooksLikeGroupedNumber(string text)
        {
            int start = text[0] == '-' ? 1 : 0;
            int end = text.IndexOf('.') is var dot && dot >= 0 ? dot : text.Length;
            string[] groups = text[start..end].Split(',');
            if (groups.Length < 2 || groups[0].Length is < 1 or > 3)
            {
                return false;
            }

            for (int index = 0; index < groups.Length; index++)
            {
                if ((index > 0 && groups[index].Length != 3) || !groups[index].All(c => c is >= '0' and <= '9'))
                {
                    return false;
                }
            }

            return end == text.Length || (end + 1 < text.Length && text[(end + 1)..].All(c => c is >= '0' and <= '9'));
        }

        private static bool TryParseSingle(ColumnType type, string text, out object value, out string reason, bool isElement = false)
        {
            switch (type.Kind)
            {
                case ColumnKind.Enum:
                    return TryParseEnum(type.Enum!, text, out value, out reason);
                case ColumnKind.Reference:
                    if (isElement)
                    {
                        return TryParseSingle(type.KeyType!, text, out value, out reason, isElement: true);
                    }

                    if (text.Trim().Length == 0)
                    {
                        value = RefValue.None;
                        reason = type.IsOptional ? "" : $"is empty, but a Ref<{type.TargetTable}> must point at a row. Write the row's key, or make the type Ref<{type.TargetTable}>? to allow empty";
                        return type.IsOptional;
                    }

                    bool ok = TryParseSingle(type.KeyType!, text, out object key, out reason);
                    value = ok ? new RefValue(true, key) : RefValue.None;
                    return ok;
                default:
                    return Scalars.TryParse(type.Scalar, text, out value, out reason);
            }
        }

        private static bool TryParseEnum(EnumDefinition definition, string text, out object value, out string reason)
        {
            reason = "";
            string trimmed = text.Trim();
            if (trimmed.Length == 0)
            {
                value = 0L;
                if (definition.FindByValue(0) == null)
                {
                    reason = $"is empty, but the enum {definition.Name} has no member with the value 0 to stand for 'nothing'";
                    return false;
                }

                return true;
            }

            if (definition.Find(trimmed) is { } single)
            {
                value = single.Value;
                return true;
            }

            if (definition.IsFlags && trimmed.IndexOfAny(new[] { ',', '|', ' ' }) >= 0)
            {
                long combined = 0;
                foreach (string part in trimmed.Split(new[] { ',', '|', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (definition.Find(part.Trim()) is not { } member)
                    {
                        value = 0L;
                        reason = $"uses '{part.Trim()}', which is not a member of {definition.Name}. Members: {MemberList(definition)}";
                        return false;
                    }

                    combined |= member.Value;
                }

                value = combined;
                return true;
            }

            value = 0L;
            reason = $"is not a member of {definition.Name}. Members: {MemberList(definition)}";
            return false;
        }

        private static string MemberList(EnumDefinition definition)
        {
            const int shown = 12;
            string list = string.Join(", ", definition.Members.Take(shown).Select(member => member.Name));
            return definition.Members.Count > shown ? list + ", …" : list;
        }
    }

    public static class RowConverter
    {
        public static TableData Convert(RawTable raw, TableSchema schema, char arraySeparator, DiagnosticBag diagnostics)
        {
            var rows = new List<RowData>(raw.Rows.Count);
            var elements = new List<object>();
            foreach (Cell[] source in raw.Rows)
            {
                var values = new object[schema.Columns.Count];
                var cells = new Cell[schema.Columns.Count];
                bool valid = true;
                for (int index = 0; index < schema.Columns.Count; index++)
                {
                    ColumnSchema column = schema.Columns[index];
                    Cell cell = source[column.SourceIndexes[0]];
                    cells[index] = cell;
                    if (column.SourceIndexes.Count > 1)
                    {
                        elements.Clear();
                        foreach (int sourceIndex in column.SourceIndexes)
                        {
                            Cell part = source[sourceIndex];
                            if (!CellParser.TryParseElements(column.Type.ElementType, part.Text, arraySeparator, elements, out string partReason))
                            {
                                diagnostics.AddError(part, $"'{part.Trimmed}' {partReason}.", Hint(column, arraySeparator, part.Text));
                                valid = false;
                            }
                            else
                            {
                                WarnIfAmbiguous(column, part, arraySeparator, diagnostics);
                            }
                        }

                        values[index] = elements.ToArray();
                        continue;
                    }

                    if (column.Role == ColumnRole.Key && cell.IsBlank)
                    {
                        diagnostics.AddError(cell, $"The key '{column.Field}' is empty.", "Every row needs a key. To leave the row out, start its first cell with # or ~.");
                        valid = false;
                        continue;
                    }

                    string text = column.Role == ColumnRole.Key && column.Type.Scalar == ScalarKind.String && column.Type.Kind == ColumnKind.Scalar ? cell.Trimmed : cell.Text;
                    if (CellParser.TryParse(column.Type, text, arraySeparator, out object value, out string reason))
                    {
                        values[index] = value;
                        if (column.Type.IsArray)
                        {
                            WarnIfAmbiguous(column, cell, arraySeparator, diagnostics);
                        }
                    }
                    else
                    {
                        diagnostics.AddError(cell, cell.IsBlank ? $"The cell {reason}." : $"'{cell.Trimmed}' {reason}.", Hint(column, arraySeparator, text));
                        valid = false;
                    }
                }

                if (valid)
                {
                    rows.Add(new RowData(values, cells));
                }
            }

            if (schema.Kind == TableKind.Config && rows.Count != 1 && !diagnostics.HasErrors)
            {
                diagnostics.AddError(schema.Location, $"A Config sheet holds exactly one set of values, but this one has {rows.Count}.",
                    "Keep one value column next to the header columns. Start extra columns with # to have them ignored.");
            }

            return new TableData(schema, rows);
        }

        private static void WarnIfAmbiguous(ColumnSchema column, Cell cell, char arraySeparator, DiagnosticBag diagnostics)
        {
            if (CellParser.AmbiguityWarning(column.Type.ElementType, cell.Text, arraySeparator, out string hint) is { } warning)
            {
                diagnostics.AddWarning(cell, warning, hint);
            }
        }

        private static string Hint(ColumnSchema column, char arraySeparator, string text)
        {
            string example = column.Type.Example;
            string hint = $"The column '{column.Field}' is {column.Type.CanonicalName}. Example: {(column.Type.IsArray ? $"[{example}, {example}]" : example)}";
            if (column.Type.IsArray && arraySeparator != '|' && text.Contains('|') && column.Type.Enum is not { IsFlags: true })
            {
                hint += $". Arrays are separated by '{arraySeparator}': write {example}{arraySeparator} {example}, not {example}|{example}";
            }

            return hint;
        }
    }

    public static class DataValidator
    {
        public static void Validate(IReadOnlyList<TableData> tables, DiagnosticBag diagnostics)
        {
            var primaryKeys = new Dictionary<string, HashSet<object>>(StringComparer.Ordinal);
            foreach (TableData table in tables)
            {
                for (int index = 0; index < table.Schema.Columns.Count; index++)
                {
                    ColumnSchema column = table.Schema.Columns[index];
                    if (column.Role != ColumnRole.Key)
                    {
                        continue;
                    }

                    var seen = new Dictionary<object, Cell>();
                    foreach (RowData row in table.Rows)
                    {
                        object key = Comparable(row.Values[index]);
                        if (seen.TryGetValue(key, out Cell first))
                        {
                            diagnostics.AddError(row.Cells[index], $"The key '{row.Cells[index].Trimmed}' is already used at {first.Origin.A1}.", $"Values of '{column.Field}' must be unique in the table '{table.Schema.Name}'.");
                        }
                        else
                        {
                            seen.Add(key, row.Cells[index]);
                        }
                    }

                    if (ReferenceEquals(column, table.Schema.PrimaryKey))
                    {
                        primaryKeys[table.Schema.Name] = new HashSet<object>(seen.Keys);
                    }
                }
            }

            foreach (TableData table in tables)
            {
                for (int index = 0; index < table.Schema.Columns.Count; index++)
                {
                    ColumnType type = table.Schema.Columns[index].Type;
                    if (type.Kind != ColumnKind.Reference || !primaryKeys.TryGetValue(type.TargetTable!, out HashSet<object>? keys))
                    {
                        continue;
                    }

                    foreach (RowData row in table.Rows)
                    {
                        if (type.IsArray)
                        {
                            foreach (object element in (object[])row.Values[index])
                            {
                                if (!keys.Contains(Comparable(element)))
                                {
                                    Missing(diagnostics, row.Cells[index], Display(element), type.TargetTable!);
                                }
                            }
                        }
                        else if (row.Values[index] is RefValue { HasValue: true } reference && !keys.Contains(Comparable(reference.Key!)))
                        {
                            Missing(diagnostics, row.Cells[index], Display(reference.Key!), type.TargetTable!);
                        }
                    }
                }
            }
        }

        private static void Missing(DiagnosticBag diagnostics, Cell cell, string key, string targetTable)
        {
            diagnostics.AddError(cell, $"The table '{targetTable}' has no row with key '{key}'.", $"Add the row to '{targetTable}', or fix the key here.");
        }

        private static object Comparable(object value) => value is string ? value : Scalars.ToInt64(value);

        private static string Display(object value) => System.Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
    }

    public readonly struct ArrayElement
    {
        public ArrayElement(string text, bool quoted)
        {
            Text = text;
            Quoted = quoted;
        }

        public string Text { get; }

        public bool Quoted { get; }
    }

    public static class ArraySplitter
    {
        public static bool IsBracketed(string trimmed) => trimmed.Length >= 2 && trimmed[0] == '[' && trimmed[trimmed.Length - 1] == ']';

        public static bool TrySplit(string text, char arraySeparator, List<ArrayElement> parts, out char separator, out string reason)
        {
            reason = "";
            separator = arraySeparator;
            string body = text.Trim();
            if (IsBracketed(body))
            {
                body = body.Substring(1, body.Length - 2);
                separator = arraySeparator != ',' && ContainsOutsideQuotes(body, arraySeparator) ? arraySeparator : ',';
            }

            if (body.Trim().Length == 0)
            {
                return true;
            }

            int position = 0;
            while (true)
            {
                SkipSpace(body, ref position, separator);
                if (position < body.Length && IsQuote(body[position]))
                {
                    if (!TryReadQuoted(body, ref position, out string quoted))
                    {
                        reason = $"opens a quote in element {parts.Count + 1} but never closes it";
                        return false;
                    }

                    SkipSpace(body, ref position, separator);
                    if (position < body.Length && body[position] != separator)
                    {
                        reason = $"has text after the closing quote of element {parts.Count + 1}. To use a quote inside quoted text, write \\\"";
                        return false;
                    }

                    parts.Add(new ArrayElement(quoted, true));
                }
                else
                {
                    int end = body.IndexOf(separator, position);
                    if (end < 0)
                    {
                        end = body.Length;
                    }

                    parts.Add(new ArrayElement(body.Substring(position, end - position).Trim(), false));
                    position = end;
                }

                if (position >= body.Length)
                {
                    return true;
                }

                position++;
            }
        }

        public static bool ContainsOutsideQuotes(string text, char character)
        {
            bool inQuotes = false;
            for (int index = 0; index < text.Length; index++)
            {
                char c = text[index];
                if (inQuotes && c == '\\' && index + 1 < text.Length)
                {
                    index++;
                }
                else if (IsQuote(c))
                {
                    inQuotes = !inQuotes;
                }
                else if (!inQuotes && c == character)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsQuote(char c) => c is '"' or '“' or '”';

        private static void SkipSpace(string text, ref int position, char separator)
        {
            while (position < text.Length && text[position] != separator && char.IsWhiteSpace(text[position]))
            {
                position++;
            }
        }

        private static bool TryReadQuoted(string text, ref int position, out string value)
        {
            var builder = new System.Text.StringBuilder();
            for (int index = position + 1; index < text.Length; index++)
            {
                char c = text[index];
                if (c == '\\' && index + 1 < text.Length && (text[index + 1] == '\\' || IsQuote(text[index + 1])))
                {
                    builder.Append(text[++index]);
                }
                else if (IsQuote(c))
                {
                    position = index + 1;
                    value = builder.ToString();
                    return true;
                }
                else
                {
                    builder.Append(c);
                }
            }

            value = "";
            return false;
        }
    }
}
