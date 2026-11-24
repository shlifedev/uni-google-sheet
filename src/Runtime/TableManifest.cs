#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace HolySheet
{
    public sealed class TableManifestEntry
    {
        public string Name { get; }

        public string File { get; }

        public int Rows { get; }

        public long Bytes { get; }

        public string Hash { get; }

        public string Schema { get; }

        public IReadOnlyDictionary<string, string> Extra { get; }

        internal IReadOnlyDictionary<string, string> RawExtra { get; }

        public TableManifestEntry(string name, string file, int rows, long bytes, string hash, string schema, IReadOnlyDictionary<string, string>? extra = null)
            : this(name, file, rows, bytes, hash, schema, Quote(extra), raw: true)
        {
        }

        internal TableManifestEntry(string name, string file, int rows, long bytes, string hash, string schema, Dictionary<string, string>? rawExtra, bool raw)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            File = file ?? throw new ArgumentNullException(nameof(file));
            Rows = rows;
            Bytes = bytes;
            Hash = hash ?? throw new ArgumentNullException(nameof(hash));
            Schema = schema ?? throw new ArgumentNullException(nameof(schema));
            if (rawExtra == null || rawExtra.Count == 0)
            {
                RawExtra = EmptyExtra;
                Extra = EmptyExtra;
                return;
            }

            var decoded = new Dictionary<string, string>(rawExtra.Count, StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> member in rawExtra)
            {
                if (Array.IndexOf(TableManifest.TableMembers, member.Key) >= 0)
                {
                    throw new ArgumentException("\"" + member.Key + "\" is a table member of its own and cannot be an extra one.", nameof(rawExtra));
                }

                decoded[member.Key] = ManifestJson.Describe(member.Value);
            }

            RawExtra = rawExtra;
            Extra = decoded;
        }

        private static Dictionary<string, string>? Quote(IReadOnlyDictionary<string, string>? extra)
        {
            if (extra == null || extra.Count == 0)
            {
                return null;
            }

            var raw = new Dictionary<string, string>(extra.Count, StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> member in extra)
            {
                raw[member.Key] = ManifestJson.Quote(member.Value ?? throw new ArgumentException("An extra member has no value.", nameof(extra)));
            }

            return raw;
        }

        private static readonly IReadOnlyDictionary<string, string> EmptyExtra = new Dictionary<string, string>(0);
    }

    public sealed class TableManifestDiff
    {
        public IReadOnlyList<string> Added { get; }

        public IReadOnlyList<string> Removed { get; }

        public IReadOnlyList<string> Changed { get; }

        public IReadOnlyList<string> SchemaChanged { get; }

        public IReadOnlyList<string> Unchanged { get; }

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

    public sealed class TableManifest
    {
        public const int FormatVersion = 1;
        public const string DefaultFileName = "tables.manifest.json";
        public const string DataExtension = ".bytes";
        public const string SignatureAlgorithm = "rsa-sha256";

        private const string FormatMember = "holysheet";
        private const string SerializerMember = "serializer";
        private const string SchemaMember = "schema";
        private const string ContentMember = "content";
        private const string TablesMember = "tables";
        private const string SignatureMember = "signature";

        internal static readonly string[] TopMembers = { FormatMember, SerializerMember, SchemaMember, ContentMember, TablesMember, SignatureMember };

        internal static readonly string[] TableMembers = { "name", "file", "rows", "bytes", "hash", "schema" };

        private readonly List<TableManifestEntry> _tables;
        private readonly Dictionary<string, TableManifestEntry> _tablesByName;
        private readonly Dictionary<string, string> _rawProperties;
        private readonly Dictionary<string, string> _properties;

        public string Serializer { get; }

        public string Schema { get; }

        public string Content { get; }

        public IReadOnlyList<TableManifestEntry> Tables => _tables;

        public IReadOnlyDictionary<string, string> Properties => _properties;

        public string? Signature { get; }

        public string SignedText { get; }

        public TableManifest(string serializer, string schema, IEnumerable<TableManifestEntry> tables, IEnumerable<KeyValuePair<string, string>>? properties = null, string? signature = null)
            : this(serializer, schema, tables, QuoteProperties(properties), signature)
        {
        }

        private TableManifest(string serializer, string schema, IEnumerable<TableManifestEntry> tables, Dictionary<string, string> rawProperties, string? signature)
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
                if (_tablesByName.ContainsKey(_tables[index].Name))
                {
                    throw new ArgumentException("The table " + _tables[index].Name + " is listed twice.", nameof(tables));
                }

                _tablesByName.Add(_tables[index].Name, _tables[index]);
            }

            _rawProperties = rawProperties;
            _properties = new Dictionary<string, string>(rawProperties.Count, StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> property in rawProperties)
            {
                if (Array.IndexOf(TopMembers, property.Key) >= 0)
                {
                    throw new ArgumentException("\"" + property.Key + "\" is a manifest member of its own and cannot be a property.", nameof(rawProperties));
                }

                _properties[property.Key] = ManifestJson.Describe(property.Value);
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

        public TableManifest WithSignature(string signature)
        {
            return new TableManifest(Serializer, Schema, _tables, _rawProperties, signature);
        }

        public string ToText()
        {
            if (Signature == null)
            {
                return SignedText;
            }

            var builder = new StringBuilder(SignedText.Length + Signature.Length + 64);
            builder.Append(SignedText, 0, SignedText.Length - 3);
            builder.Append(",\n  ").Append('"').Append(SignatureMember).Append("\": { \"algorithm\": ");
            ManifestJson.WriteString(builder, SignatureAlgorithm);
            builder.Append(", \"value\": ");
            ManifestJson.WriteString(builder, Signature);
            builder.Append(" }\n}\n");
            return builder.ToString();
        }

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

            if (text.TrimStart('\uFEFF', ' ', '\t', '\r', '\n').StartsWith("holysheet-manifest", StringComparison.Ordinal))
            {
                throw Invalid("it is in the old text format (tables.manifest.txt). Build the data again with the tool that matches this runtime.");
            }

            if (text.Trim().Length == 0)
            {
                throw Invalid("it is empty.");
            }

            object? document;
            try
            {
                document = ManifestJson.Parse(text);
            }
            catch (FormatException exception)
            {
                throw Invalid("it is not valid JSON: " + exception.Message);
            }

            if (!(document is ManifestJsonObject root))
            {
                throw Invalid("it is not a JSON object.");
            }

            object? version = null;
            bool versionSeen = false;
            string? serializer = null;
            string? schema = null;
            string? content = null;
            string? signature = null;
            List<TableManifestEntry>? tables = null;
            var properties = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, object?> member in root.Members)
            {
                switch (member.Key)
                {
                    case FormatMember:
                        version = member.Value;
                        versionSeen = true;
                        break;
                    case SerializerMember:
                        serializer = RequireString(member.Value, SerializerMember);
                        break;
                    case SchemaMember:
                        schema = RequireString(member.Value, SchemaMember);
                        break;
                    case ContentMember:
                        content = RequireString(member.Value, ContentMember);
                        break;
                    case TablesMember:
                        tables = ParseTables(member.Value);
                        break;
                    case SignatureMember:
                        signature = ParseSignature(member.Value);
                        break;
                    default:
                        properties[member.Key] = ManifestJson.Minify(member.Value);
                        break;
                }
            }

            if (!versionSeen)
            {
                throw Invalid("it has no \"" + FormatMember + "\" member. Expected \"" + FormatMember + "\": " + FormatVersion + ".");
            }

            if (!(version is ManifestJsonNumber number) || !int.TryParse(number.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int formatVersion))
            {
                throw Invalid("\"" + FormatMember + "\" is " + ManifestJson.Minify(version) + ", expected a format version number.");
            }

            if (formatVersion != FormatVersion)
            {
                throw Invalid("it is format version " + formatVersion + ", but this runtime reads version " + FormatVersion + ". Use the tool that matches this runtime.");
            }

            if (string.IsNullOrEmpty(serializer) || string.IsNullOrEmpty(schema))
            {
                throw Invalid("the \"" + SerializerMember + "\" or \"" + SchemaMember + "\" member is missing.");
            }

            if (tables == null)
            {
                throw Invalid("the \"" + TablesMember + "\" member is missing.");
            }

            TableManifest manifest;
            try
            {
                manifest = new TableManifest(serializer!, schema!, tables, properties, signature);
            }
            catch (ArgumentException exception)
            {
                throw Invalid(exception.Message);
            }

            if (content != null && content != manifest.Content)
            {
                throw new TableLoadException(TableLoadError.HashMismatch,
                    "The manifest's content hash does not match its tables (manifest says " + content + ", tables give " + manifest.Content + "). The manifest was edited by hand or is corrupted.");
            }

            return manifest;
        }

        private static List<TableManifestEntry> ParseTables(object? value)
        {
            if (!(value is List<object?> items))
            {
                throw Invalid("\"" + TablesMember + "\" is not an array.");
            }

            var tables = new List<TableManifestEntry>(items.Count);
            var names = new HashSet<string>(StringComparer.Ordinal);
            for (int index = 0; index < items.Count; index++)
            {
                TableManifestEntry table = ParseTable(items[index], index);
                if (!names.Add(table.Name))
                {
                    throw Invalid("the table " + table.Name + " is listed twice.");
                }

                tables.Add(table);
            }

            return tables;
        }

        private static TableManifestEntry ParseTable(object? value, int index)
        {
            string where = "table " + (index + 1);
            if (!(value is ManifestJsonObject table))
            {
                throw Invalid(where + " is not an object.");
            }

            string? name = null;
            string? file = null;
            string? hash = null;
            string schema = "";
            int rows = -1;
            long bytes = -1;
            Dictionary<string, string>? extra = null;
            foreach (KeyValuePair<string, object?> member in table.Members)
            {
                switch (member.Key)
                {
                    case "name":
                        name = RequireString(member.Value, where + " name");
                        where = "table " + name;
                        break;
                    case "file":
                        file = RequireString(member.Value, where + " file");
                        break;
                    case "hash":
                        hash = RequireString(member.Value, where + " hash");
                        break;
                    case "schema":
                        schema = RequireString(member.Value, where + " schema");
                        break;
                    case "rows":
                        if (!(member.Value is ManifestJsonNumber rowsNumber) || !int.TryParse(rowsNumber.Text, NumberStyles.None, CultureInfo.InvariantCulture, out rows))
                        {
                            throw Invalid(where + " has rows " + ManifestJson.Minify(member.Value) + ", expected a whole number.");
                        }

                        break;
                    case "bytes":
                        if (!(member.Value is ManifestJsonNumber bytesNumber) || !long.TryParse(bytesNumber.Text, NumberStyles.None, CultureInfo.InvariantCulture, out bytes))
                        {
                            throw Invalid(where + " has bytes " + ManifestJson.Minify(member.Value) + ", expected a whole number.");
                        }

                        break;
                    default:
                        extra ??= new Dictionary<string, string>(StringComparer.Ordinal);
                        extra[member.Key] = ManifestJson.Minify(member.Value);
                        break;
                }
            }

            if (string.IsNullOrEmpty(name))
            {
                throw Invalid(where + " has no name.");
            }

            if (hash == null || rows < 0)
            {
                throw Invalid(where + " is missing \"hash\" or \"rows\".");
            }

            return new TableManifestEntry(name!, file ?? name + DataExtension, rows, bytes, hash, schema, extra, raw: true);
        }

        private static string ParseSignature(object? value)
        {
            if (!(value is ManifestJsonObject signature))
            {
                throw Invalid("\"" + SignatureMember + "\" is not an object.");
            }

            string? algorithm = null;
            string? text = null;
            foreach (KeyValuePair<string, object?> member in signature.Members)
            {
                if (member.Key == "algorithm")
                {
                    algorithm = RequireString(member.Value, "signature algorithm");
                }
                else if (member.Key == "value")
                {
                    text = RequireString(member.Value, "signature value");
                }
            }

            if (algorithm != SignatureAlgorithm)
            {
                throw Invalid("the signature uses an algorithm this runtime does not know (" + (algorithm ?? "none") + "). Expected '" + SignatureAlgorithm + "'.");
            }

            if (string.IsNullOrEmpty(text))
            {
                throw Invalid("the signature has no value.");
            }

            return text!;
        }

        private static string RequireString(object? value, string what)
        {
            if (!(value is string text))
            {
                throw Invalid(what + " is " + ManifestJson.Minify(value) + ", expected a string.");
            }

            return text;
        }

        private static Dictionary<string, string> QuoteProperties(IEnumerable<KeyValuePair<string, string>>? properties)
        {
            var raw = new Dictionary<string, string>(StringComparer.Ordinal);
            if (properties != null)
            {
                foreach (KeyValuePair<string, string> property in properties)
                {
                    raw[property.Key] = ManifestJson.Quote(property.Value ?? throw new ArgumentException("The property " + property.Key + " has no value.", nameof(properties)));
                }
            }

            return raw;
        }

        private string BuildSignedText()
        {
            var builder = new StringBuilder(256 + _tables.Count * 220);
            builder.Append("{\n");
            builder.Append("  \"").Append(FormatMember).Append("\": ").Append(FormatVersion.ToString(CultureInfo.InvariantCulture)).Append(",\n");
            AppendMember(builder, SerializerMember, ManifestJson.Quote(Serializer));
            AppendMember(builder, SchemaMember, ManifestJson.Quote(Schema));
            AppendMember(builder, ContentMember, ManifestJson.Quote(Content));

            var keys = new List<string>(_rawProperties.Keys);
            keys.Sort(StringComparer.Ordinal);
            for (int index = 0; index < keys.Count; index++)
            {
                AppendMember(builder, keys[index], _rawProperties[keys[index]]);
            }

            builder.Append("  \"").Append(TablesMember).Append("\": [");
            for (int index = 0; index < _tables.Count; index++)
            {
                TableManifestEntry table = _tables[index];
                builder.Append(index == 0 ? "\n" : ",\n");
                builder.Append("    { \"name\": ");
                ManifestJson.WriteString(builder, table.Name);
                builder.Append(", \"file\": ");
                ManifestJson.WriteString(builder, table.File);
                builder.Append(", \"rows\": ").Append(table.Rows.ToString(CultureInfo.InvariantCulture));
                builder.Append(", \"bytes\": ").Append(table.Bytes.ToString(CultureInfo.InvariantCulture));
                builder.Append(", \"hash\": ");
                ManifestJson.WriteString(builder, table.Hash);
                builder.Append(", \"schema\": ");
                ManifestJson.WriteString(builder, table.Schema);
                if (table.RawExtra.Count > 0)
                {
                    var extraKeys = new List<string>(table.RawExtra.Keys);
                    extraKeys.Sort(StringComparer.Ordinal);
                    for (int extraIndex = 0; extraIndex < extraKeys.Count; extraIndex++)
                    {
                        builder.Append(", ");
                        ManifestJson.WriteString(builder, extraKeys[extraIndex]);
                        builder.Append(": ").Append(table.RawExtra[extraKeys[extraIndex]]);
                    }
                }

                builder.Append(" }");
            }

            builder.Append(_tables.Count == 0 ? "]\n" : "\n  ]\n");
            builder.Append("}\n");
            return builder.ToString();
        }

        private static void AppendMember(StringBuilder builder, string name, string rawValue)
        {
            builder.Append("  ");
            ManifestJson.WriteString(builder, name);
            builder.Append(": ").Append(rawValue).Append(",\n");
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
