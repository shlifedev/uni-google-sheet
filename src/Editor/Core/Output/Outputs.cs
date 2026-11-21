#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using HolySheet.Core.Config;
using HolySheet.Core.Generation;
using HolySheet.Core.Schema;

namespace HolySheet.Core.Output
{
    /// <summary>Everything one build produces, held in memory. Nothing is on disk until <see cref="OutputCommitter"/> writes it.</summary>
    public sealed class GeneratedOutput
    {
        /// <summary>File name to C# source.</summary>
        public SortedDictionary<string, string> CodeFiles { get; } = new(StringComparer.Ordinal);

        /// <summary>File name to content, for the data folder: one file per table, plus the schema description.</summary>
        public SortedDictionary<string, byte[]> DataFiles { get; } = new(StringComparer.Ordinal);

        /// <summary>File name to CSV text of each table after the transform rules.</summary>
        public SortedDictionary<string, string> CsvFiles { get; } = new(StringComparer.Ordinal);

        public string ManifestFileName { get; set; } = "tables.manifest.json";

        /// <summary>The manifest. It is written last, so its appearance on disk means every data file is already in place.</summary>
        public string ManifestText { get; set; } = "";

        public string SchemaHash { get; set; } = "";

        public string ContentHash { get; set; } = "";
    }

    /// <summary>One table of the manifest.</summary>
    public sealed record ManifestTable(string Name, string File, int Rows, long Bytes, string Hash, string Schema);

    /// <summary>
    /// Writes the manifest JSON. The format is defined by <c>HolySheet.TableManifest</c> in the runtime, which is the reader;
    /// this writer must produce exactly what that class would (its <c>SignedText</c> and <c>ToText</c>), and the tests compare the two.
    /// </summary>
    public static class ManifestWriter
    {
        public const string DataExtension = ".bytes";
        public const string SignatureAlgorithm = "rsa-sha256";

        /// <summary>The text a signature covers: the whole manifest without its signature member.</summary>
        public static string Write(string serializer, string schemaHash, IReadOnlyList<ManifestTable> tables, IReadOnlyDictionary<string, string> properties, out string contentHash)
        {
            List<ManifestTable> sorted = tables.OrderBy(table => table.Name, StringComparer.Ordinal).ToList();
            var content = new StringBuilder();
            foreach (ManifestTable table in sorted)
            {
                content.Append(table.Name).Append(' ').Append(table.Hash).Append('\n');
            }

            contentHash = SchemaHasher.Sha256Hex(content.ToString());
            var builder = new StringBuilder();
            builder.Append("{\n");
            builder.Append("  \"holysheet\": 1,\n");
            Member(builder, "serializer", serializer);
            Member(builder, "schema", schemaHash);
            Member(builder, "content", contentHash);
            foreach (KeyValuePair<string, string> property in properties.OrderBy(property => property.Key, StringComparer.Ordinal))
            {
                Member(builder, property.Key, property.Value);
            }

            builder.Append("  \"tables\": [");
            for (int index = 0; index < sorted.Count; index++)
            {
                ManifestTable table = sorted[index];
                builder.Append(index == 0 ? "\n" : ",\n");
                builder.Append("    { \"name\": ");
                Quote(builder, table.Name);
                builder.Append(", \"file\": ");
                Quote(builder, table.File);
                builder.Append(", \"rows\": ").Append(table.Rows.ToString(CultureInfo.InvariantCulture));
                builder.Append(", \"bytes\": ").Append(table.Bytes.ToString(CultureInfo.InvariantCulture));
                builder.Append(", \"hash\": ");
                Quote(builder, table.Hash);
                builder.Append(", \"schema\": ");
                Quote(builder, table.Schema);
                builder.Append(" }");
            }

            builder.Append(sorted.Count == 0 ? "]\n" : "\n  ]\n");
            builder.Append("}\n");
            return builder.ToString();
        }

        /// <summary>Adds the signature as the last member, in front of the closing brace.</summary>
        public static string AppendSignature(string signedText, string signature)
        {
            var builder = new StringBuilder(signedText.Length + signature.Length + 64);
            builder.Append(signedText, 0, signedText.Length - 3);
            builder.Append(",\n  \"signature\": { \"algorithm\": ");
            Quote(builder, SignatureAlgorithm);
            builder.Append(", \"value\": ");
            Quote(builder, signature);
            builder.Append(" }\n}\n");
            return builder.ToString();
        }

        private static void Member(StringBuilder builder, string name, string value)
        {
            builder.Append("  ");
            Quote(builder, name);
            builder.Append(": ");
            Quote(builder, value);
            builder.Append(",\n");
        }

        // The same escaping as the runtime's ManifestJson.WriteString: quotes, backslashes and control characters only.
        private static void Quote(StringBuilder builder, string value)
        {
            builder.Append('"');
            foreach (char character in value)
            {
                switch (character)
                {
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    case '\b':
                        builder.Append("\\b");
                        break;
                    case '\f':
                        builder.Append("\\f");
                        break;
                    default:
                        if (character < 0x20)
                        {
                            builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            builder.Append(character);
                        }

                        break;
                }
            }

            builder.Append('"');
        }
    }

    /// <summary>
    /// Signing keys. The private key stays on the build machine and signs the manifest; the public key is baked into the
    /// generated code and lets every client check that the data it loads came from that build machine.
    /// </summary>
    public static class SigningKeys
    {
        private const string PrivateHeader = "holysheet-private-key 1";

        /// <summary>Creates a new RSA key pair. Returns the private key file text and the public key in its config form.</summary>
        public static (string PrivateKey, string PublicKey) Generate(int bits = 2048)
        {
            using RSA rsa = RSA.Create();
            rsa.KeySize = bits;
            RSAParameters key = rsa.ExportParameters(includePrivateParameters: true);
            var builder = new StringBuilder();
            builder.Append(PrivateHeader).Append('\n');
            builder.Append("# Keep this file secret and out of version control. Anyone who has it can sign table data.\n");
            Append(builder, "modulus", key.Modulus);
            Append(builder, "exponent", key.Exponent);
            Append(builder, "d", key.D);
            Append(builder, "p", key.P);
            Append(builder, "q", key.Q);
            Append(builder, "dp", key.DP);
            Append(builder, "dq", key.DQ);
            Append(builder, "inverseq", key.InverseQ);
            return (builder.ToString(), FormatPublicKey(key));
        }

        public static RSAParameters ParsePrivateKey(string text)
        {
            var values = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            bool headerSeen = false;
            foreach (string rawLine in text.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                if (!headerSeen)
                {
                    if (line != PrivateHeader)
                    {
                        throw new FormatException("This is not a HolySheet private key file. Create one with 'holysheet keygen'.");
                    }

                    headerSeen = true;
                    continue;
                }

                int space = line.IndexOf(' ');
                if (space > 0)
                {
                    values[line[..space]] = Convert.FromBase64String(line[(space + 1)..].Trim());
                }
            }

            try
            {
                return new RSAParameters
                {
                    Modulus = values["modulus"], Exponent = values["exponent"], D = values["d"], P = values["p"],
                    Q = values["q"], DP = values["dp"], DQ = values["dq"], InverseQ = values["inverseq"]
                };
            }
            catch (KeyNotFoundException)
            {
                throw new FormatException("The private key file is incomplete. Create a new one with 'holysheet keygen'.");
            }
        }

        /// <summary>The public key in the text form the runtime reads: rsa:{base64 modulus}:{base64 exponent}.</summary>
        public static string FormatPublicKey(RSAParameters key) => "rsa:" + Convert.ToBase64String(key.Modulus!) + ":" + Convert.ToBase64String(key.Exponent!);

        public static string Sign(string signedText, RSAParameters privateKey)
        {
            using RSA rsa = RSA.Create();
            rsa.ImportParameters(privateKey);
            return Convert.ToBase64String(rsa.SignData(Encoding.UTF8.GetBytes(signedText), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        }

        /// <summary>Finds the private key a config points at: the environment variable first, then the file. Null when neither is set.</summary>
        public static string? ReadPrivateKey(HolySheetConfig config)
        {
            if (!string.IsNullOrEmpty(config.Signing.PrivateKeyEnv) && Environment.GetEnvironmentVariable(config.Signing.PrivateKeyEnv) is { Length: > 0 } fromEnvironment)
            {
                // CI secret stores often flatten line breaks; accept "\n" written out.
                return fromEnvironment.Replace("\\n", "\n");
            }

            if (!string.IsNullOrEmpty(config.Signing.PrivateKeyFile))
            {
                string path = config.Resolve(config.Signing.PrivateKeyFile!);
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }

            return null;
        }

        private static void Append(StringBuilder builder, string name, byte[]? value)
        {
            builder.Append(name).Append(' ').Append(Convert.ToBase64String(value!)).Append('\n');
        }
    }

    /// <summary>
    /// Describes every table and enum as JSON, written next to the data as tables.schema.json.
    /// It lets tools that have no generated code (a patch builder, a balance viewer, a server in another language) read the data.
    /// </summary>
    public static class SchemaJson
    {
        public const string FileName = "tables.schema.json";

        public static string Write(IReadOnlyList<TableSchema> tables, IReadOnlyList<EnumDefinition> enums, string serializer, string schemaHash)
        {
            JsonObject root = JsonValue.Object().Set("format", 1).Set("serializer", serializer).Set("schema", schemaHash);
            var tableItems = new List<JsonValue>();
            foreach (TableSchema table in tables)
            {
                JsonObject item = JsonValue.Object().Set("name", table.Name).Set("kind", table.Kind == TableKind.Config ? "config" : "table")
                    .Set("file", table.Name + ManifestWriter.DataExtension).Set("schema", table.Hash).Set("source", table.Location);
                if (table.PrimaryKey != null)
                {
                    item.Set("primaryKey", table.PrimaryKey.Field);
                }

                var columns = new List<JsonValue>();
                foreach (ColumnSchema column in table.Columns)
                {
                    JsonObject columnItem = JsonValue.Object().Set("name", column.Field).Set("type", column.Type.CanonicalName);
                    if (column.Role != ColumnRole.None)
                    {
                        columnItem.Set("role", column.Role == ColumnRole.Key ? "key" : "index");
                    }

                    if (column.Type.Kind == ColumnKind.Reference)
                    {
                        columnItem.Set("references", column.Type.TargetTable).Set("stored", column.Type.KeyType!.CanonicalName);
                    }

                    columnItem.Set("members", column.StoredMemberCount);
                    if (column.Comment.Length > 0)
                    {
                        columnItem.Set("comment", column.Comment);
                    }

                    columns.Add(columnItem.ToValue());
                }

                item.Set("columns", JsonValue.Array(columns));
                tableItems.Add(item.ToValue());
            }

            root.Set("tables", JsonValue.Array(tableItems));
            var enumItems = new List<JsonValue>();
            foreach (EnumDefinition definition in enums)
            {
                JsonObject item = JsonValue.Object().Set("name", definition.Name).Set("type", Scalars.CSharpName(definition.Underlying)).Set("flags", definition.IsFlags);
                item.Set("members", JsonValue.Array(definition.Members.Select(member => JsonValue.Object().Set("name", member.Name).Set("value", member.Value).ToValue())));
                enumItems.Add(item.ToValue());
            }

            root.Set("enums", JsonValue.Array(enumItems));
            return root.ToValue().ToJson();
        }
    }

    /// <summary>What a commit changed. Paths are relative to their output folder.</summary>
    public sealed class CommitReport
    {
        public List<string> Written { get; } = new();

        public List<string> Deleted { get; } = new();

        public int Unchanged { get; set; }

        public bool HasChanges => Written.Count > 0 || Deleted.Count > 0;
    }

    /// <summary>
    /// Puts a build's output on disk.
    /// <list type="bullet">
    /// <item>A file whose content is already right is not touched, so nothing recompiles or shows up in version control for no reason.</item>
    /// <item>Before writing, checks ownership of every existing destination using the generated code marker, the previous manifest and schema, or the CSV list.</item>
    /// <item>The manifest is written last and by rename, so a reader that sees a manifest always finds the data files it lists.</item>
    /// <item>If writing fails part way, the files already changed are put back.</item>
    /// </list>
    /// </summary>
    public static class OutputCommitter
    {
        private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

        /// <summary>The list of CSV files the tool wrote into the CSV output folder. Starts with a dot so Unity does not import it.</summary>
        public const string CsvListFileName = ".holysheet-csv";

        private const string CsvListHeader = "holysheet-csv 1";

        public static CommitReport Commit(GeneratedOutput output, string codeDirectory, string dataDirectory, string? csvDirectory = null)
        {
            var files = new FileChangeSet("HolySheet cannot identify it as its own output. Move the file or choose another output path.");

            var keptCode = new HashSet<string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> file in output.CodeFiles)
            {
                keptCode.Add(file.Key);
                string path = Path.Combine(codeDirectory, file.Key);
                files.PlanWrite(path, Utf8.GetBytes(file.Value), file.Key, canReplace: File.Exists(path) && IsGenerated(path));
            }

            if (Directory.Exists(codeDirectory))
            {
                foreach (string path in Directory.GetFiles(codeDirectory, "*.g.cs", SearchOption.TopDirectoryOnly))
                {
                    if (!ListsFile(keptCode, codeDirectory, Path.GetFileName(path)) && IsGenerated(path))
                    {
                        files.PlanDelete(path, Path.GetFileName(path));
                    }
                }
            }

            string manifestPath = Path.Combine(dataDirectory, output.ManifestFileName);
            List<string>? manifestFiles = ReadPreviousDataFiles(manifestPath) ?? ReadLegacyDataFiles(manifestPath);
            var previous = new HashSet<string>(manifestFiles ?? Enumerable.Empty<string>(), StringComparer.Ordinal);

            // A folder last built when the manifest was text: what it lists counts as the previous build, and the old manifest goes.
            string legacyPath = Path.Combine(dataDirectory, LegacyManifestFileName);
            List<string>? legacy = string.Equals(output.ManifestFileName, LegacyManifestFileName, StringComparison.OrdinalIgnoreCase) ? null : ReadLegacyDataFiles(legacyPath);
            if (legacy != null)
            {
                previous.UnionWith(legacy);
            }

            var keptData = new HashSet<string>(StringComparer.Ordinal) { output.ManifestFileName };
            foreach (KeyValuePair<string, byte[]> file in output.DataFiles)
            {
                keptData.Add(file.Key);
                string path = Path.Combine(dataDirectory, file.Key);
                bool owned = file.Key == SchemaJson.FileName
                    ? (manifestFiles != null || legacy != null) && IsGeneratedSchema(path)
                    : ListsFile(previous, dataDirectory, file.Key);
                files.PlanWrite(path, file.Value, file.Key, canReplace: owned);
            }

            foreach (string stale in previous)
            {
                if (File.Exists(Path.Combine(dataDirectory, stale)) && !ListsFile(keptData, dataDirectory, stale))
                {
                    files.PlanDelete(Path.Combine(dataDirectory, stale), stale);
                }
            }

            if (legacy != null)
            {
                files.PlanDelete(legacyPath, LegacyManifestFileName);
            }

            if (csvDirectory != null)
            {
                // CSV files carry no marker, so the folder records the ones HolySheet may replace or remove.
                string listPath = Path.Combine(csvDirectory, CsvListFileName);
                HashSet<string>? previousCsv = ReadCsvList(listPath);
                var keptCsv = new HashSet<string>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, string> file in output.CsvFiles)
                {
                    keptCsv.Add(file.Key);
                    files.PlanWrite(Path.Combine(csvDirectory, file.Key), Utf8.GetBytes(file.Value), file.Key, canReplace: ListsFile(previousCsv, csvDirectory, file.Key));
                }

                foreach (string stale in previousCsv ?? Enumerable.Empty<string>())
                {
                    if (File.Exists(Path.Combine(csvDirectory, stale)) && !ListsFile(keptCsv, csvDirectory, stale))
                    {
                        files.PlanDelete(Path.Combine(csvDirectory, stale), stale);
                    }
                }

                files.PlanWrite(listPath, Utf8.GetBytes(WriteCsvList(keptCsv)), CsvListFileName, canReplace: previousCsv != null);
            }

            // The manifest goes last: it is the signal that a complete build is in place.
            files.PlanWrite(manifestPath, Utf8.GetBytes(output.ManifestText), output.ManifestFileName, canReplace: manifestFiles != null);
            return files.Apply();
        }

        private static bool ListsFile(HashSet<string>? names, string directory, string name)
        {
            if (names == null)
            {
                return false;
            }

            if (names.Contains(name))
            {
                return true;
            }

            // A case-only table rename can address the same file. Require both spellings to resolve
            // and only one directory entry, so a separate user file on a case-sensitive volume stays safe.
            if (!File.Exists(Path.Combine(directory, name)) || !names.Any(other =>
                string.Equals(other, name, StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(directory, other))))
            {
                return false;
            }

            return Directory.EnumerateFiles(directory).Count(path => string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase)) == 1;
        }

        private static bool IsGenerated(string path)
        {
            try
            {
                using var reader = new StreamReader(path);
                for (int line = 0; line < 3; line++)
                {
                    if (reader.ReadLine() is { } text && text.Contains(Code.Marker, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }
            catch (IOException)
            {
            }

            return false;
        }

        private static string WriteCsvList(IEnumerable<string> files)
        {
            var text = new StringBuilder();
            text.Append(CsvListHeader).Append('\n');
            text.Append("# Written by HolySheet: the CSV files it made here. Only these are ever replaced or removed by a build.\n");
            foreach (string file in files.OrderBy(file => file, StringComparer.Ordinal))
            {
                text.Append(file).Append('\n');
            }

            return text.ToString();
        }

        private static HashSet<string>? ReadCsvList(string path)
        {
            if (!File.Exists(path))
            {
                return null;
            }

            string[] lines = File.ReadAllLines(path, Utf8);
            if (lines.Length == 0 || lines[0] != CsvListHeader)
            {
                return null;
            }

            var files = new HashSet<string>(StringComparer.Ordinal);
            foreach (string file in lines.Skip(1))
            {
                // Only plain .csv names in this folder: a hand-edited list must never make the tool delete anything else.
                if (!file.StartsWith("#", StringComparison.Ordinal) && file == Path.GetFileName(file)
                    && file.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                {
                    files.Add(file);
                }
            }

            return files;
        }

        private static JsonObject? ReadJsonObject(string path)
        {
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                return JsonValue.Parse(File.ReadAllText(path)).AsObject;
            }
            catch (JsonFormatException)
            {
                return null;
            }
        }

        private static bool IsGeneratedSchema(string path)
        {
            JsonObject? schema = ReadJsonObject(path);
            return schema?.GetNumber("format") == 1 && schema.GetString("serializer") != null && schema.GetString("schema") != null
                && schema.GetArray("tables") != null && schema.GetArray("enums") != null;
        }

        private static List<string>? ReadPreviousDataFiles(string manifestPath)
        {
            JsonObject? manifest = ReadJsonObject(manifestPath);
            if (manifest?.GetNumber("holysheet") != 1 || manifest.GetString("serializer") == null || manifest.GetString("schema") == null
                || manifest.GetArray("tables") is not { } tables)
            {
                return null;
            }

            var files = new List<string>();
            foreach (JsonValue table in tables)
            {
                AddDataFileName(files, table.AsObject?.GetString("file"));
            }

            return files;
        }

        /// <summary>The manifest name before it became JSON. A data folder from an older build still has one.</summary>
        public const string LegacyManifestFileName = "tables.manifest.txt";

        /// <summary>
        /// The data files an old text manifest lists, when <paramref name="path"/> is one this tool wrote (its first line says so).
        /// Null when it is not there or not ours: a file someone else named the same is left alone.
        /// </summary>
        private static List<string>? ReadLegacyDataFiles(string path)
        {
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                string[] lines = File.ReadAllLines(path);
                if (lines.Length == 0 || lines[0].Trim() != "holysheet-manifest 1")
                {
                    return null;
                }

                var files = new List<string>();
                foreach (string line in lines)
                {
                    if (!line.StartsWith("table ", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    foreach (string part in line.Split(' '))
                    {
                        if (part.StartsWith("file=", StringComparison.Ordinal))
                        {
                            AddDataFileName(files, part[5..]);
                        }
                    }
                }

                return files;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        // A table owns only a plain data file in this folder.
        private static void AddDataFileName(List<string> files, string? file)
        {
            if (file != null && file == Path.GetFileName(file) && file.EndsWith(ManifestWriter.DataExtension, StringComparison.OrdinalIgnoreCase))
            {
                files.Add(file);
            }
        }
    }

    /// <summary>
    /// Lets one build at a time write a project's output, across processes: the CLI, the Unity editor and watchers.
    /// The lock is a file created with create-new, which every runtime and file system makes atomic. It holds the owner's
    /// process id, so a lock left behind by a process that died is recognized and taken over.
    /// </summary>
    internal sealed class OutputLock : IDisposable
    {
        public const string FileName = "build.lock";

        private static readonly bool IsWindows = Path.DirectorySeparatorChar == '\\';

        private readonly string path;
        private FileStream? stream;

        private OutputLock(string path, FileStream stream)
        {
            this.path = path;
            this.stream = stream;
        }

        public static OutputLock Acquire(string directory, TimeSpan timeout)
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, FileName);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                try
                {
                    // On Windows the system deletes the file when the handle closes, even after a crash. Elsewhere it is
                    // deleted by Dispose, and a crashed owner is detected by its process id.
                    var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 64, IsWindows ? FileOptions.DeleteOnClose : FileOptions.None);
                    byte[] owner = Encoding.ASCII.GetBytes(System.Diagnostics.Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture));
                    stream.Write(owner, 0, owner.Length);
                    stream.Flush();
                    return new OutputLock(path, stream);
                }
                catch (IOException) when (File.Exists(path))
                {
                    if (IsAbandoned(path))
                    {
                        TryDelete(path);
                        continue;
                    }

                    if (watch.Elapsed > timeout)
                    {
                        throw new IOException($"Another build is still writing the output (lock file {path}). Wait for it to finish; if no build is running, delete that file.");
                    }

                    System.Threading.Thread.Sleep(50);
                }
            }
        }

        public void Dispose()
        {
            if (stream == null)
            {
                return;
            }

            if (!IsWindows)
            {
                // Removed while still open, so another process can never lose a lock it has just created to this delete.
                TryDelete(path);
            }

            stream.Dispose();
            stream = null;
        }

        private static bool IsAbandoned(string path)
        {
            string text;
            DateTime written;
            try
            {
                text = File.ReadAllText(path).Trim();
                written = File.GetLastWriteTimeUtc(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return false;
            }

            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int processId))
            {
                // Just created and not written yet, or damaged. Only an old one is abandoned.
                return DateTime.UtcNow - written > TimeSpan.FromSeconds(30);
            }

            try
            {
                using System.Diagnostics.Process process = System.Diagnostics.Process.GetProcessById(processId);
                return process.HasExited;
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException or PlatformNotSupportedException)
            {
                return true;
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
