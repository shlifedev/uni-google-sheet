#nullable enable
using System;
using System.Collections.Generic;

namespace UGS
{
    /// <summary>A loaded table. Generated table classes implement this.</summary>
    public interface ITable
    {
        string Name { get; }

        /// <summary>Number of rows.</summary>
        int Count { get; }

        /// <summary>The manifest line this table was loaded from. Its hash identifies the exact data in memory.</summary>
        TableManifestEntry Entry { get; }
    }

    /// <summary>
    /// A complete, immutable snapshot of every table. Generated "tables" classes implement this.
    /// A reload never changes a snapshot; it produces a new one, so readers holding the old one stay consistent.
    /// </summary>
    public interface ITableSet
    {
        TableManifest Manifest { get; }

        /// <summary>Every table, in the order of the generated definition.</summary>
        IReadOnlyList<ITable> Tables { get; }
    }

    /// <summary>Turns the bytes of one data file into a table. Generated code supplies one per table.</summary>
    public delegate ITable TableDecoder(byte[] data, TableManifestEntry entry);

    /// <summary>What the generated code knows about one table.</summary>
    public sealed class TableDefinition
    {
        public string Name { get; }

        /// <summary>Layout hash the generated row class was built from.</summary>
        public string SchemaHash { get; }

        public TableDecoder Decode { get; }

        public TableDefinition(string name, string schemaHash, TableDecoder decode)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            SchemaHash = schemaHash ?? throw new ArgumentNullException(nameof(schemaHash));
            Decode = decode ?? throw new ArgumentNullException(nameof(decode));
        }
    }

    /// <summary>What the generated code knows about the whole set. The loader checks the manifest against it.</summary>
    public sealed class TableSetDefinition
    {
        private readonly TableDefinition[] _tables;

        public string Serializer { get; }

        public string SchemaHash { get; }

        public string ManifestFileName { get; }

        /// <summary>Public key baked in at build time, or null when the tables are not signed.</summary>
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

    /// <summary>The loader's result, handed to the generated constructor.</summary>
    public sealed class TableSetData
    {
        private readonly ITable[] _tables;
        private readonly bool[] _reused;

        public TableManifest Manifest { get; }

        /// <summary>Tables in the order of <see cref="TableSetDefinition.Tables"/>.</summary>
        public ReadOnlyArray<ITable> Tables => new ReadOnlyArray<ITable>(_tables);

        /// <summary>Names of the tables that were decoded for this load. The rest were carried over from the previous snapshot.</summary>
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

    /// <summary>How to load. The defaults are the safe ones: sequential, every hash checked.</summary>
    public sealed class TableLoadOptions
    {
        public static TableLoadOptions Default { get; } = new TableLoadOptions();

        /// <summary>
        /// Read and decode tables on several threads. Off by default because not every platform has threads (WebGL)
        /// and because a single frame-time budget may matter more than total time.
        /// </summary>
        public bool Parallel { get; set; }

        /// <summary>Upper bound on threads when <see cref="Parallel"/> is on. Zero or less lets the runtime decide.</summary>
        public int MaxDegreeOfParallelism { get; set; }

        /// <summary>Compare each data file with the hash in the manifest. Turning it off skips tamper and corruption detection.</summary>
        public bool VerifyHashes { get; set; } = true;

        /// <summary>
        /// Public key to check the manifest signature with. Null means "use the key baked into the generated code, if any".
        /// When a key is known, an unsigned or wrongly signed manifest is rejected.
        /// </summary>
        public string? PublicKey { get; set; }

        /// <summary>Skip the signature check even when a public key is known. Meant for local development builds.</summary>
        public bool SkipSignature { get; set; }

        /// <summary>Overrides the manifest file name from the generated code.</summary>
        public string? ManifestFileName { get; set; }
    }
}
