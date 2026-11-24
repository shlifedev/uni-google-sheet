#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace HolySheet.Core.Schema
{
    public sealed class ColumnSchema
    {
        public ColumnSchema(string field, ColumnType type, ColumnRole role, string comment, Cell nameCell, IReadOnlyList<int> sourceIndexes)
        {
            Field = field;
            Type = type;
            Role = role;
            Comment = comment;
            NameCell = nameCell;
            SourceIndexes = sourceIndexes;
        }

        public string Field { get; }

        public ColumnType Type { get; }

        public ColumnRole Role { get; }

        public string Comment { get; }

        public Cell NameCell { get; }

        public IReadOnlyList<int> SourceIndexes { get; }

        public int StoredMemberCount => Type is { Kind: ColumnKind.Reference, IsOptional: true } ? 2 : 1;
    }

    public enum TableKind
    {
        Table,

        Config
    }

    public sealed class TableSchema
    {
        public string Name { get; }

        public TableKind Kind { get; }

        public string Location { get; }

        public string Comment { get; }

        public IReadOnlyList<ColumnSchema> Columns { get; }

        public IReadOnlyList<ColumnSchema> Keys { get; }

        public IReadOnlyList<ColumnSchema> Indexes { get; }

        public ColumnSchema? PrimaryKey => Keys.Count > 0 ? Keys[0] : null;

        public string Hash { get; internal set; } = "";

        public TableSchema(string name, TableKind kind, string location, IReadOnlyList<ColumnSchema> columns, string comment = "")
        {
            Name = name;
            Kind = kind;
            Location = location;
            Comment = comment;
            Columns = columns;
            Keys = columns.Where(column => column.Role == ColumnRole.Key).ToList();
            Indexes = columns.Where(column => column.Role == ColumnRole.Index).ToList();
        }

        public string RowTypeName => Name + "Row";

        public string TableTypeName => Name + "Table";

        public string RefTypeName => Name + "Ref";

        public int StoredMemberCount => Columns.Sum(column => column.StoredMemberCount);
    }

    public sealed class RowData
    {
        public RowData(object[] values, Cell[] cells)
        {
            Values = values;
            Cells = cells;
        }

        public object[] Values { get; }

        public Cell[] Cells { get; }
    }

    public sealed class TableData
    {
        public TableData(TableSchema schema, List<RowData> rows)
        {
            Schema = schema;
            Rows = rows;
        }

        public TableSchema Schema { get; }

        public List<RowData> Rows { get; }
    }

    public static class SchemaHasher
    {
        public const int LayoutVersion = 1;

        public static string HashTable(TableSchema table, string serializer)
        {
            var builder = new StringBuilder();
            builder.Append("layout ").Append(LayoutVersion.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(serializer).Append('\n');
            AppendTable(builder, table);

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (ColumnSchema column in table.Columns)
            {
                if (column.Type.Enum is { } definition && seen.Add(definition.Name))
                {
                    AppendEnum(builder, definition);
                }
            }

            return Sha256Hex(builder.ToString());
        }

        public static string HashSet(IReadOnlyList<TableSchema> tables, IReadOnlyList<EnumDefinition> enums, string serializer)
        {
            var builder = new StringBuilder();
            builder.Append("layout ").Append(LayoutVersion.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(serializer).Append('\n');
            foreach (TableSchema table in tables)
            {
                AppendTable(builder, table);
            }

            foreach (EnumDefinition definition in enums)
            {
                AppendEnum(builder, definition);
            }

            return Sha256Hex(builder.ToString());
        }

        private static void AppendTable(StringBuilder builder, TableSchema table)
        {
            builder.Append("table ").Append(table.Name).Append(table.Kind == TableKind.Config ? " config" : "").Append('\n');
            foreach (ColumnSchema column in table.Columns)
            {
                builder.Append("  column ").Append(column.Field).Append(' ').Append(column.Type.CanonicalName);
                builder.Append(column.Role switch { ColumnRole.Key => " key", ColumnRole.Index => " index", _ => "" });
                if (column.Type.Kind == ColumnKind.Reference)
                {
                    builder.Append(" -> ").Append(column.Type.KeyType!.CanonicalName);
                }

                builder.Append('\n');
            }
        }

        private static void AppendEnum(StringBuilder builder, EnumDefinition definition)
        {
            builder.Append("enum ").Append(definition.Name).Append(' ').Append(Scalars.CSharpName(definition.Underlying)).Append(definition.IsFlags ? " flags" : "").Append('\n');
            foreach (EnumMember member in definition.Members)
            {
                builder.Append("  member ").Append(member.Name).Append(' ').Append(member.Value.ToString(CultureInfo.InvariantCulture)).Append('\n');
            }
        }

        public static string Sha256Hex(byte[] data)
        {
            using SHA256 sha256 = SHA256.Create();
            byte[] hash = sha256.ComputeHash(data);
            var builder = new StringBuilder(hash.Length * 2);
            foreach (byte b in hash)
            {
                builder.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }

        public static string Sha256Hex(string text) => Sha256Hex(Encoding.UTF8.GetBytes(text));
    }
}
