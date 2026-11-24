#nullable enable
using System;
using System.Collections.Generic;

namespace HolySheet
{
    public interface ITable
    {
        string Name { get; }

        int Count { get; }

        TableManifestEntry Entry { get; }
    }

    public interface ITableSet
    {
        TableManifest Manifest { get; }

        IReadOnlyList<ITable> Tables { get; }
    }

    public delegate ITable TableDecoder(byte[] data, TableManifestEntry entry);

    public sealed class TableDefinition
    {
        public string Name { get; }

        public string SchemaHash { get; }

        public TableDecoder Decode { get; }

        public TableDefinition(string name, string schemaHash, TableDecoder decode)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            SchemaHash = schemaHash ?? throw new ArgumentNullException(nameof(schemaHash));
            Decode = decode ?? throw new ArgumentNullException(nameof(decode));
        }
    }

    public sealed class TableSetDefinition
    {
        private readonly TableDefinition[] _tables;

        public string Serializer { get; }

        public string SchemaHash { get; }

        public string ManifestFileName { get; }

        public string? PublicKey { get; }

        public ReadOnlyArray<TableDefinition> Tables => new ReadOnlyArray<TableDefinition>(_tables);

        public TableSetDefinition(string serializer, string schemaHash, string manifestFileName, string? publicKey, TableDefinition[] tables)
        {
            Serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
            SchemaHash = schemaHash ?? throw new ArgumentNullException(nameof(schemaHash));
            ManifestFileName = manifestFileName ?? throw new ArgumentNullException(nameof(manifestFileName));
            PublicKey = string.IsNullOrEmpty(publicKey) ? null : publicKey;
            _tables = (TableDefinition[])(tables ?? throw new ArgumentNullException(nameof(tables))).Clone();
        }
    }

    public sealed class TableSetData
    {
        private readonly ITable[] _tables;
        private readonly bool[] _reused;

        public TableManifest Manifest { get; }

        public ReadOnlyArray<ITable> Tables => new ReadOnlyArray<ITable>(_tables);

        public IReadOnlyList<string> LoadedTables { get; }

        internal TableSetData(TableManifest manifest, ITable[] tables, bool[] reused)
        {
            Manifest = manifest;
            _tables = tables;
            _reused = reused;
            List<string> loaded = new List<string>();
            for (int index = 0; index < tables.Length; index++)
            {
                if (!reused[index])
                {
                    loaded.Add(tables[index].Name);
                }
            }

            LoadedTables = loaded;
        }

        public bool WasReused(int tableIndex)
        {
            return _reused[tableIndex];
        }
    }

    public sealed class TableLoadOptions
    {
        public static TableLoadOptions Default { get; } = new TableLoadOptions();

        public bool Parallel { get; set; }

        public int MaxDegreeOfParallelism { get; set; }

        public bool VerifyHashes { get; set; } = true;

        public string? PublicKey { get; set; }

        public bool SkipSignature { get; set; }

        public string? ManifestFileName { get; set; }
    }
}
