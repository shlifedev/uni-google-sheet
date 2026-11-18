#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace UGS.Core.Schema
{
    /// <summary>One column of a table: it becomes one property of the generated row class.</summary>
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

        /// <summary>The cell that names the field. Problems about the column as a whole point here.</summary>
        public Cell NameCell { get; }

        /// <summary>
        /// Positions of this field's columns in the raw table. More than one means the sheet repeats the field name
        /// and the columns were merged into one array.
        /// </summary>
        public IReadOnlyList<int> SourceIndexes { get; }

        /// <summary>Number of members this column takes in the stored row. An optional reference stores a flag and a key.</summary>
        public int StoredMemberCount => Type is { Kind: ColumnKind.Reference, IsOptional: true } ? 2 : 1;
    }

    public enum TableKind
    {
        Table,

        /// <summary>Written as "Name.Config" with fields down the first columns. It has exactly one row.</summary>
        Config
    }

    /// <summary>The shape of one table. Generated code and data are both derived from it and from nothing else.</summary>
    public sealed class TableSchema
    {
        public string Name { get; }

        public TableKind Kind { get; }

        /// <summary>The sheet that defines the table, such as "Game.xlsx / Item".</summary>
        public string Location { get; }

        public string Comment { get; }

        /// <summary>In sheet order. This is also the order of members in the stored row.</summary>
        public IReadOnlyList<ColumnSchema> Columns { get; }

        public IReadOnlyList<ColumnSchema> Keys { get; }

        public IReadOnlyList<ColumnSchema> Indexes { get; }

        /// <summary>The first key. References and column extension sheets find rows by it. Null for a table without keys.</summary>
        public ColumnSchema? PrimaryKey => Keys.Count > 0 ? Keys[0] : null;

        /// <summary>Hash of this table's layout, filled in once every table and enum is known.</summary>
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

        // The naming rule for generated types lives here and nowhere else.
        public string RowTypeName => Name + "Row";

        public string TableTypeName => Name + "Table";

        public string RefTypeName => Name + "Ref";

        public int StoredMemberCount => Columns.Sum(column => column.StoredMemberCount);
    }

    /// <summary>One converted row.</summary>
    public sealed class RowData
    {
        public RowData(object[] values, Cell[] cells)
        {
            Values = values;
            Cells = cells;
        }

        /// <summary>In the order of <see cref="TableSchema.Columns"/>. The runtime type of each value follows its column type.</summary>
        public object[] Values { get; }

        /// <summary>The cell each value came from (the first one, for merged columns). Later checks use it to point at problems.</summary>
        public Cell[] Cells { get; }
    }

    /// <summary>A table's shape and content.</summary>
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

    /// <summary>
    /// Hashes table layouts. Only what changes the stored bytes or the generated code goes in: names, types, roles, enum members.
    /// Comments do not. The same hash is written to the generated code and to the manifest, so the loader can refuse data
    /// that was built for a different layout instead of reading it wrong.
    /// </summary>
    public static class SchemaHasher
    {
        /// <summary>Bump when the way data is encoded changes without the schema text changing.</summary>
        public const int LayoutVersion = 1;

        public static string HashTable(TableSchema table, string serializer)
        {
            var builder = new StringBuilder();
            builder.Append("layout ").Append(LayoutVersion.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(serializer).Append('\n');
            AppendTable(builder, table);

            // The enums a table uses are part of its layout: their integer type sizes the value, their members give it meaning.
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

        /// <summary>Hash of the whole set. <paramref name="tables"/> and <paramref name="enums"/> must be sorted by name.</summary>
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
                    // A reference stores the target's key, so the key's type is part of this table's layout too.
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

        /// <summary>SHA-256 as lowercase hex. Must produce the same text as UGS.TableHash in the runtime.</summary>
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
