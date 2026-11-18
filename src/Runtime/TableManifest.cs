#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace UGS
{
    /// <summary>One table line of a manifest.</summary>
    public sealed class TableManifestEntry
    {
        public string Name { get; }

        /// <summary>The data file, relative to the manifest. For example "Item.bytes".</summary>
        public string File { get; }

        public int Rows { get; }

        /// <summary>Size of the data file in bytes.</summary>
        public long Bytes { get; }

        /// <summary>SHA-256 of the data file, lowercase hex.</summary>
        public string Hash { get; }

        /// <summary>Hash of this table's layout. It only changes when the generated row class would change.</summary>
        public string Schema { get; }

        /// <summary>Attributes this runtime does not know. Kept so newer tools can add metadata without breaking older clients.</summary>
        public IReadOnlyDictionary<string, string> Extra { get; }

        public TableManifestEntry(string name, string file, int rows, long bytes, string hash, string schema, IReadOnlyDictionary<string, string>? extra = null)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            File = file ?? throw new ArgumentNullException(nameof(file));
            Rows = rows;
            Bytes = bytes;
            Hash = hash ?? throw new ArgumentNullException(nameof(hash));
            Schema = schema ?? throw new ArgumentNullException(nameof(schema));
            Extra = extra ?? EmptyExtra;
        }

        private static readonly IReadOnlyDictionary<string, string> EmptyExtra = new Dictionary<string, string>(0);
    }

    /// <summary>What differs between two manifests. This is the input a patch system needs: which files to fetch, which to drop.</summary>
    public sealed class TableManifestDiff
    {
        /// <summary>Tables only in the newer manifest.</summary>
        public IReadOnlyList<string> Added { get; }

        /// <summary>Tables only in the older manifest.</summary>
        public IReadOnlyList<string> Removed { get; }

        /// <summary>Tables in both whose data file differs.</summary>
        public IReadOnlyList<string> Changed { get; }

        /// <summary>The subset of <see cref="Changed"/> whose layout differs too. These need new generated code, not just new data.</summary>
        public IReadOnlyList<string> SchemaChanged { get; }

        public IReadOnlyList<string> Unchanged { get; }

        /// <summary>True when the set-level schema differs: the newer data cannot be read by code generated for the older one.</summary>
        public bool RequiresNewCode { get; }

        public bool HasChanges => Added.Count > 0 || Removed.Count > 0 || Changed.Count > 0;

        internal TableManifestDiff(List<string> added, List<string> removed, List<string> changed, List<string> schemaChanged, List<string> unchanged, bool requiresNewCode)
        {
            Added = added;
            Removed = removed;
            Changed = changed;
            SchemaChanged = schemaChanged;
            Unchanged = unchanged;
            RequiresNewCode = requiresNewCode;
        }
    }

    /// <summary>
    /// The list of data files in one build of the tables, written next to them by the tool.
    /// It is plain text, one fact per line, so it diffs cleanly in version control:
    /// <code>
    /// ugs-manifest 1
    /// serializer memorypack
    /// schema 9d92…                 hash of every table layout and enum; baked into the generated code
    /// content 5c1e…                hash of every data file hash; identifies this exact build of the data
    /// table Item file=Item.bytes rows=12 bytes=345 hash=… schema=…
    /// sign rsa-sha256 …            optional; covers every line above it
    /// </code>
    /// Unknown keywords and unknown table attributes are preserved, not rejected.
    /// </summary>
    public sealed class TableManifest
    {
        public const int FormatVersion = 1;
        public const string DefaultFileName = "tables.manifest.txt";
        public const string DataExtension = ".bytes";
        public const string SignatureAlgorithm = "rsa-sha256";

        private const string HeaderKeyword = "ugs-manifest";
        private const string SerializerKeyword = "serializer";
        private const string SchemaKeyword = "schema";
        private const string ContentKeyword = "content";
        private const string TableKeyword = "table";
        private const string SignKeyword = "sign";

        private readonly List<TableManifestEntry> _tables;
        private readonly Dictionary<string, TableManifestEntry> _tablesByName;
        private readonly Dictionary<string, string> _properties;

        /// <summary>"memorypack" or "messagepack".</summary>
        public string Serializer { get; }

        /// <summary>Hash of all table layouts and enums. Must equal the constant in the generated code.</summary>
        public string Schema { get; }

        /// <summary>Hash over every table's name and data hash. Two manifests with the same value describe identical data.</summary>
        public string Content { get; }

        /// <summary>Tables in ordinal name order.</summary>
        public IReadOnlyList<TableManifestEntry> Tables => _tables;

        /// <summary>Top-level lines this runtime has no dedicated property for, keyed by their keyword. For example "build".</summary>
        public IReadOnlyDictionary<string, string> Properties => _properties;

        /// <summary>Base64 signature, or null when the manifest is not signed.</summary>
        public string? Signature { get; }

        /// <summary>The exact text the signature covers: every line before the sign line.</summary>
        public string SignedText { get; }

        public TableManifest(string serializer, string schema, IEnumerable<TableManifestEntry> tables, IEnumerable<KeyValuePair<string, string>>? properties = null, string? signature = null)
        {
            if (string.IsNullOrEmpty(serializer))
            {
                throw new ArgumentException("A manifest needs a serializer name.", nameof(serializer));
            }

            if (string.IsNullOrEmpty(schema))
            {
                throw new ArgumentException("A manifest needs a schema hash.", nameof(schema));
            }

            if (tables == null)
            {
                throw new ArgumentNullException(nameof(tables));
            }

            Serializer = serializer;
            Schema = schema;
            _tables = new List<TableManifestEntry>(tables);
            _tables.Sort((left, right) => string.CompareOrdinal(left.Name, right.Name));
            _tablesByName = new Dictionary<string, TableManifestEntry>(_tables.Count, StringComparer.Ordinal);
            for (int index = 0; index < _tables.Count; index++)
            {
                _tablesByName.Add(_tables[index].Name, _tables[index]);
            }

            _properties = new Dictionary<string, string>(StringComparer.Ordinal);
            if (properties != null)
            {
                foreach (KeyValuePair<string, string> property in properties)
                {
                    _properties[property.Key] = property.Value;
                }
            }

            Content = ComputeContent(_tables);
            Signature = signature;
            SignedText = BuildSignedText();
        }

        public bool IsSigned => Signature != null;

        public TableManifestEntry? FindTable(string name)
        {
            return _tablesByName.TryGetValue(name, out TableManifestEntry? entry) ? entry : null;
        }

        /// <summary>Returns the same manifest with a signature line attached.</summary>
        public TableManifest WithSignature(string signature)
        {
            return new TableManifest(Serializer, Schema, _tables, _properties, signature);
        }

        public string ToText()
        {
            if (Signature == null)
            {
                return SignedText;
            }

            return SignedText + SignKeyword + " " + SignatureAlgorithm + " " + Signature + "\n";
        }

        /// <summary>Compares an older manifest (or null for "nothing installed") with a newer one.</summary>
        public static TableManifestDiff Diff(TableManifest? older, TableManifest newer)
        {
            if (newer == null)
            {
                throw new ArgumentNullException(nameof(newer));
            }

            List<string> added = new List<string>();
            List<string> removed = new List<string>();
            List<string> changed = new List<string>();
            List<string> schemaChanged = new List<string>();
            List<string> unchanged = new List<string>();
            for (int index = 0; index < newer._tables.Count; index++)
            {
                TableManifestEntry entry = newer._tables[index];
                TableManifestEntry? previous = older?.FindTable(entry.Name);
                if (previous == null)
                {
                    added.Add(entry.Name);
                }
                else if (previous.Hash != entry.Hash || previous.Schema != entry.Schema)
                {
                    changed.Add(entry.Name);
                    if (previous.Schema != entry.Schema)
                    {
                        schemaChanged.Add(entry.Name);
                    }
                }
                else
                {
                    unchanged.Add(entry.Name);
                }
            }

            if (older != null)
            {
                for (int index = 0; index < older._tables.Count; index++)
                {
                    if (newer.FindTable(older._tables[index].Name) == null)
                    {
                        removed.Add(older._tables[index].Name);
                    }
                }
            }

            bool requiresNewCode = older != null && (older.Schema != newer.Schema || older.Serializer != newer.Serializer);
            return new TableManifestDiff(added, removed, changed, schemaChanged, unchanged, requiresNewCode);
        }

        public static TableManifest Parse(string text)
        {
            if (text == null)
            {
                throw new ArgumentNullException(nameof(text));
            }

            bool headerRead = false;
            string? serializer = null;
            string? schema = null;
            string? content = null;
            string? signature = null;
            List<TableManifestEntry> tables = new List<TableManifestEntry>();
            Dictionary<string, string> properties = new Dictionary<string, string>(StringComparer.Ordinal);

            int position = 0;
            int lineNumber = 0;
            while (position < text.Length)
            {
                int lineEnd = text.IndexOf('\n', position);
                if (lineEnd < 0)
                {
                    lineEnd = text.Length;
                }

                string line = text.Substring(position, lineEnd - position).Trim();
                position = lineEnd + 1;
                lineNumber++;
                if (line.Length == 0)
                {
                    continue;
                }

                if (signature != null)
                {
                    throw Invalid("line " + lineNumber + " comes after the signature. Nothing may follow the sign line.");
                }

                int space = line.IndexOf(' ');
                string keyword = space < 0 ? line : line.Substring(0, space);
                string rest = space < 0 ? "" : line.Substring(space + 1).Trim();
                if (!headerRead)
                {
                    if (keyword != HeaderKeyword || !int.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out int version))
                    {
                        throw Invalid("the first line is '" + line + "', expected '" + HeaderKeyword + " " + FormatVersion + "'.");
                    }

                    if (version != FormatVersion)
                    {
                        throw Invalid("it is format version " + version + ", but this runtime reads version " + FormatVersion + ". Use the tool that matches this runtime.");
                    }

                    headerRead = true;
                    continue;
                }

                switch (keyword)
                {
                    case SerializerKeyword:
                        serializer = rest;
                        break;
                    case SchemaKeyword:
                        schema = rest;
                        break;
                    case ContentKeyword:
                        content = rest;
                        break;
                    case TableKeyword:
                        tables.Add(ParseTable(rest, lineNumber));
                        break;
                    case SignKeyword:
                        signature = ParseSignature(rest, lineNumber);
                        break;
                    default:
                        properties[keyword] = rest;
                        break;
                }
            }

            if (!headerRead)
            {
                throw Invalid("it is empty.");
            }

            if (string.IsNullOrEmpty(serializer) || string.IsNullOrEmpty(schema))
            {
                throw Invalid("the '" + SerializerKeyword + "' or '" + SchemaKeyword + "' line is missing.");
            }

            TableManifest manifest;
            try
            {
                manifest = new TableManifest(serializer!, schema!, tables, properties, signature);
            }
            catch (ArgumentException exception)
            {
                throw Invalid("a table is listed twice (" + exception.Message + ")");
            }

            // The content line is derived data. If it disagrees with the table lines, someone edited one and not the other.
            if (content != null && content != manifest.Content)
            {
                throw new TableLoadException(TableLoadError.HashMismatch,
                    "The manifest's content hash does not match its table lines (manifest says " + content + ", tables give " + manifest.Content + "). The manifest was edited by hand or is corrupted.");
            }

            return manifest;
        }

        private static TableManifestEntry ParseTable(string rest, int lineNumber)
        {
            string[] parts = rest.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                throw Invalid("line " + lineNumber + " names no table.");
            }

            string name = parts[0];
            string? file = null;
            string? hash = null;
            string schema = "";
            int rows = -1;
            long bytes = -1;
            Dictionary<string, string>? extra = null;
            for (int index = 1; index < parts.Length; index++)
            {
                int equals = parts[index].IndexOf('=');
                if (equals <= 0)
                {
                    throw Invalid("line " + lineNumber + " has '" + parts[index] + "', expected key=value.");
                }

                string key = parts[index].Substring(0, equals);
                string value = parts[index].Substring(equals + 1);
                switch (key)
                {
                    case "file":
                        file = value;
                        break;
                    case "hash":
                        hash = value;
                        break;
                    case "schema":
                        schema = value;
                        break;
                    case "rows":
                        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out rows))
                        {
                            throw Invalid("line " + lineNumber + " has rows=" + value + ", expected a number.");
                        }

                        break;
                    case "bytes":
                        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out bytes))
                        {
                            throw Invalid("line " + lineNumber + " has bytes=" + value + ", expected a number.");
                        }

                        break;
                    default:
                        extra ??= new Dictionary<string, string>(StringComparer.Ordinal);
                        extra[key] = value;
                        break;
                }
            }

            if (hash == null || rows < 0)
            {
                throw Invalid("line " + lineNumber + " (table " + name + ") is missing hash= or rows=.");
            }

            return new TableManifestEntry(name, file ?? name + DataExtension, rows, bytes, hash, schema, extra);
        }

        private static string ParseSignature(string rest, int lineNumber)
        {
            int space = rest.IndexOf(' ');
            if (space <= 0 || rest.Substring(0, space) != SignatureAlgorithm)
            {
                throw Invalid("line " + lineNumber + " uses a signature algorithm this runtime does not know. Expected '" + SignatureAlgorithm + "'.");
            }

            return rest.Substring(space + 1).Trim();
        }

        private string BuildSignedText()
        {
            StringBuilder builder = new StringBuilder(256 + _tables.Count * 160);
            builder.Append(HeaderKeyword).Append(' ').Append(FormatVersion.ToString(CultureInfo.InvariantCulture)).Append('\n');
            builder.Append(SerializerKeyword).Append(' ').Append(Serializer).Append('\n');
            builder.Append(SchemaKeyword).Append(' ').Append(Schema).Append('\n');
            builder.Append(ContentKeyword).Append(' ').Append(Content).Append('\n');

            List<string> keys = new List<string>(_properties.Keys);
            keys.Sort(StringComparer.Ordinal);
            for (int index = 0; index < keys.Count; index++)
            {
                builder.Append(keys[index]).Append(' ').Append(_properties[keys[index]]).Append('\n');
            }

            for (int index = 0; index < _tables.Count; index++)
            {
                TableManifestEntry table = _tables[index];
                builder.Append(TableKeyword).Append(' ').Append(table.Name)
                    .Append(" file=").Append(table.File)
                    .Append(" rows=").Append(table.Rows.ToString(CultureInfo.InvariantCulture))
                    .Append(" bytes=").Append(table.Bytes.ToString(CultureInfo.InvariantCulture))
                    .Append(" hash=").Append(table.Hash)
                    .Append(" schema=").Append(table.Schema);
                if (table.Extra.Count > 0)
                {
                    List<string> extraKeys = new List<string>(table.Extra.Keys);
                    extraKeys.Sort(StringComparer.Ordinal);
                    for (int extraIndex = 0; extraIndex < extraKeys.Count; extraIndex++)
                    {
                        builder.Append(' ').Append(extraKeys[extraIndex]).Append('=').Append(table.Extra[extraKeys[extraIndex]]);
                    }
                }

                builder.Append('\n');
            }

            return builder.ToString();
        }

        private static string ComputeContent(List<TableManifestEntry> tables)
        {
            StringBuilder builder = new StringBuilder(tables.Count * 80);
            for (int index = 0; index < tables.Count; index++)
            {
                builder.Append(tables[index].Name).Append(' ').Append(tables[index].Hash).Append('\n');
            }

            return TableHash.Compute(builder.ToString());
        }

        private static TableLoadException Invalid(string detail)
        {
            return new TableLoadException(TableLoadError.ManifestInvalid, "The table manifest could not be read: " + detail);
        }
    }
}
