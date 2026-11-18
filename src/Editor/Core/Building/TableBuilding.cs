#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UGS.Core.Schema;
using UGS.Core.Transform;

namespace UGS.Core.Building
{
    /// <summary>Turns raw tables into typed schemas: resolves every type cell and merges repeated field names into arrays.</summary>
    public static class SchemaBuilder
    {
        /// <summary>
        /// The stored row starts with a one-byte member count in the MemoryPack format, and values from 250 up are reserved.
        /// The limit is applied to every serializer so that switching serializers never turns a valid sheet into an invalid one.
        /// </summary>
        public const int MaxStoredMembers = 249;

        // Names the generated row class already uses or inherits.
        private static readonly HashSet<string> ReservedFields = new(StringComparer.Ordinal)
        {
            "ToString", "Equals", "GetHashCode", "GetType", "MemberwiseClone", "Finalize", "ReferenceEquals"
        };

        public static List<(TableSchema Schema, RawTable Raw)> Build(IReadOnlyList<RawTable> tables, TypeResolver resolver, DiagnosticBag diagnostics)
        {
            // First pass: the key type of every table, so that Ref<Table> columns can be resolved whatever order the sheets are in.
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
                    // Mapping columns produce an enum and are not stored.
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
                    // The field name repeats: the columns become one array, in the order they appear.
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

    /// <summary>Converts the text of one cell into the value its column type calls for.</summary>
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

        /// <summary>Splits one cell into array elements and parses each. A blank cell adds nothing.</summary>
        public static bool TryParseElements(ColumnType elementType, string text, char arraySeparator, List<object> elements, out string reason)
        {
            reason = "";
            string trimmed = text.Trim();
            if (trimmed.Length == 0)
            {
                return true;
            }

            // "[a,b,c]" is accepted as another way to write "a|b|c".
            char separator = arraySeparator;
            if (trimmed.Length >= 2 && trimmed[0] == '[' && trimmed[^1] == ']')
            {
                trimmed = trimmed[1..^1];
                separator = trimmed.Contains(arraySeparator) ? arraySeparator : ',';
                if (trimmed.Trim().Length == 0)
                {
                    return true;
                }
            }

            string[] parts = trimmed.Split(separator);
            for (int index = 0; index < parts.Length; index++)
            {
                string part = parts[index].Trim();
                if (part.Length == 0)
                {
                    reason = $"has an empty element at position {index + 1}. Check for a '{separator}' at the start or end, or two in a row";
                    return false;
                }

                if (!TryParseSingle(elementType, part, out object element, out string elementReason, isElement: true))
                {
                    reason = $"has a bad element at position {index + 1}: '{part}' {elementReason}";
                    return false;
                }

                elements.Add(element);
            }

            return true;
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
                        // An array of references stores the keys themselves.
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
                    // Text used as an array element is trimmed; a whole text cell is kept exactly as written.
                    return Scalars.TryParse(type.Scalar, text, out value, out reason);
            }
        }

        private static bool TryParseEnum(EnumDefinition definition, string text, out object value, out string reason)
        {
            reason = "";
            string trimmed = text.Trim();
            if (trimmed.Length == 0)
            {
                // An empty cell is the member whose value is zero, the same as default(TEnum) in C#.
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

    /// <summary>Converts the rows of a raw table to values, reporting every cell that does not fit its column's type.</summary>
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
                        // Merged columns: every cell contributes its elements, left to right. Blank cells contribute none.
                        elements.Clear();
                        foreach (int sourceIndex in column.SourceIndexes)
                        {
                            Cell part = source[sourceIndex];
                            if (!CellParser.TryParseElements(column.Type.ElementType, part.Text, arraySeparator, elements, out string partReason))
                            {
                                diagnostics.AddError(part, $"'{part.Trimmed}' {partReason}.", Hint(column));
                                valid = false;
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

                    // Keys are trimmed so that a stray space cannot make two keys that look the same.
                    string text = column.Role == ColumnRole.Key && column.Type.Scalar == ScalarKind.String && column.Type.Kind == ColumnKind.Scalar ? cell.Trimmed : cell.Text;
                    if (CellParser.TryParse(column.Type, text, arraySeparator, out object value, out string reason))
                    {
                        values[index] = value;
                    }
                    else
                    {
                        diagnostics.AddError(cell, cell.IsBlank ? $"The cell {reason}." : $"'{cell.Trimmed}' {reason}.", Hint(column));
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

        private static string Hint(ColumnSchema column)
        {
            return $"The column '{column.Field}' is {column.Type.CanonicalName}. Example: {(column.Type.IsArray ? column.Type.Example + "|" + column.Type.Example : column.Type.Example)}";
        }
    }

    /// <summary>Checks that need whole tables: keys must be unique, and references must point at rows that exist.</summary>
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
                    // A target that failed to build has its own errors; adding one per reference would bury them.
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

        // Integer keys of different widths must compare equal when they hold the same number.
        private static object Comparable(object value) => value is string ? value : Scalars.ToInt64(value);

        private static string Display(object value) => System.Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
    }
}
