#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UGS.Core.Config;
using UGS.Core.Generation;
using UGS.Core.Schema;

namespace UGS.Core.Output
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

        public string ManifestFileName { get; set; } = "tables.manifest.txt";

        /// <summary>The manifest. It is written last, so its appearance on disk means every data file is already in place.</summary>
        public string ManifestText { get; set; } = "";

        public string SchemaHash { get; set; } = "";

        public string ContentHash { get; set; } = "";
    }

    /// <summary>One table line of the manifest.</summary>
    /// <summary>One table line of the manifest.</summary>
    public sealed record ManifestTable(string Name, string File, int Rows, long Bytes, string Hash, string Schema);

    /// <summary>
    /// Writes the manifest text. The format is defined by <c>UGS.TableManifest</c> in the runtime, which is the reader;
    /// this writer must produce exactly what that class would, and the tests compare the two.
    /// </summary>
    public static class ManifestWriter
    {
        public const string DataExtension = ".bytes";
        public const string SignatureAlgorithm = "rsa-sha256";

        /// <summary>The text a signature covers: every line except the sign line.</summary>
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
            builder.Append("ugs-manifest 1\n");
            builder.Append("serializer ").Append(serializer).Append('\n');
            builder.Append("schema ").Append(schemaHash).Append('\n');
            builder.Append("content ").Append(contentHash).Append('\n');
            foreach (KeyValuePair<string, string> property in properties.OrderBy(property => property.Key, StringComparer.Ordinal))
            {
                builder.Append(property.Key).Append(' ').Append(property.Value).Append('\n');
            }

            foreach (ManifestTable table in sorted)
            {
                builder.Append("table ").Append(table.Name)
                    .Append(" file=").Append(table.File)
                    .Append(" rows=").Append(table.Rows.ToString(CultureInfo.InvariantCulture))
                    .Append(" bytes=").Append(table.Bytes.ToString(CultureInfo.InvariantCulture))
                    .Append(" hash=").Append(table.Hash)
                    .Append(" schema=").Append(table.Schema)
                    .Append('\n');
            }

            return builder.ToString();
        }

        public static string AppendSignature(string signedText, string signature) => signedText + "sign " + SignatureAlgorithm + " " + signature + "\n";
    }

    /// <summary>
    /// Signing keys. The private key stays on the build machine and signs the manifest; the public key is baked into the
    /// generated code and lets every client check that the data it loads came from that build machine.
    /// </summary>
    public static class SigningKeys
    {
        private const string PrivateHeader = "ugs-private-key 1";

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
                        throw new FormatException("This is not a UGS private key file. Create one with 'ugs keygen'.");
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
                throw new FormatException("The private key file is incomplete. Create a new one with 'ugs keygen'.");
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
        public static string? ReadPrivateKey(UgsConfig config)
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
    /// <item>Only files this tool made are overwritten or deleted: .g.cs files that carry its marker, the data files the previous manifest lists, and the manifest. Anything else in the folders is left alone.</item>
    /// <item>The manifest is written last and by rename, so a reader that sees a manifest always finds the data files it lists.</item>
    /// <item>If writing fails part way, the files already changed are put back.</item>
    /// </list>
    /// </summary>
    public static class OutputCommitter
    {
        private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

        /// <summary>The list of CSV files the tool wrote into the CSV output folder. Starts with a dot so Unity does not import it.</summary>
        public const string CsvListFileName = ".ugs-csv";

        private const string CsvListHeader = "ugs-csv 1";

        public static CommitReport Commit(GeneratedOutput output, string codeDirectory, string dataDirectory, string? csvDirectory = null)
        {
            var report = new CommitReport();
            var changes = new List<Change>();

            var keptCode = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> file in output.CodeFiles)
            {
                keptCode.Add(file.Key);
                PlanWrite(changes, report, Path.Combine(codeDirectory, file.Key), Utf8.GetBytes(file.Value), file.Key);
            }

            if (Directory.Exists(codeDirectory))
            {
                foreach (string path in Directory.GetFiles(codeDirectory, "*.g.cs", SearchOption.TopDirectoryOnly))
                {
                    if (!keptCode.Contains(Path.GetFileName(path)) && IsGenerated(path))
                    {
                        PlanDelete(changes, path, Path.GetFileName(path));
                    }
                }
            }

            var keptData = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { output.ManifestFileName };
            foreach (KeyValuePair<string, byte[]> file in output.DataFiles)
            {
                keptData.Add(file.Key);
                PlanWrite(changes, report, Path.Combine(dataDirectory, file.Key), file.Value, file.Key);
            }

            // Data files of tables that no longer exist are the ones the previous manifest lists and this build does not produce.
            string manifestPath = Path.Combine(dataDirectory, output.ManifestFileName);
            foreach (string stale in ReadPreviousDataFiles(manifestPath))
            {
                if (!keptData.Contains(stale) && File.Exists(Path.Combine(dataDirectory, stale)))
                {
                    PlanDelete(changes, Path.Combine(dataDirectory, stale), stale);
                }
            }

            if (csvDirectory != null)
            {
                var keptCsv = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, string> file in output.CsvFiles)
                {
                    keptCsv.Add(file.Key);
                    PlanWrite(changes, report, Path.Combine(csvDirectory, file.Key), Utf8.GetBytes(file.Value), file.Key);
                }

                // CSV files carry no marker, so the folder keeps a list of the ones this tool wrote. Only those are removed
                // when their table goes away; any other CSV in the folder, such as sheets people keep there, is left alone.
                string listPath = Path.Combine(csvDirectory, CsvListFileName);
                foreach (string stale in ReadCsvList(listPath))
                {
                    if (!keptCsv.Contains(stale) && File.Exists(Path.Combine(csvDirectory, stale)))
                    {
                        PlanDelete(changes, Path.Combine(csvDirectory, stale), stale);
                    }
                }

                PlanWrite(changes, report, listPath, Utf8.GetBytes(WriteCsvList(keptCsv)), CsvListFileName);
            }

            // The manifest goes last: it is the signal that a complete build is in place.
            PlanWrite(changes, report, manifestPath, Utf8.GetBytes(output.ManifestText), output.ManifestFileName);
            if (changes.Count == 0)
            {
                return report;
            }

            int applied = 0;
            try
            {
                foreach (Change change in changes)
                {
                    change.Original = File.Exists(change.Path) ? File.ReadAllBytes(change.Path) : null;
                    applied++;
                    if (change.Content == null)
                    {
                        File.Delete(change.Path);
                        // Unity keeps a .meta file beside every asset; a deleted asset's .meta would otherwise linger and warn.
                        if (File.Exists(change.Path + ".meta"))
                        {
                            change.OriginalMeta = File.ReadAllBytes(change.Path + ".meta");
                            File.Delete(change.Path + ".meta");
                        }

                        report.Deleted.Add(change.Name);
                    }
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(change.Path)!);
                        WriteAtomically(change.Path, change.Content);
                        report.Written.Add(change.Name);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                for (int index = applied - 1; index >= 0; index--)
                {
                    Change change = changes[index];
                    try
                    {
                        if (change.Original == null)
                        {
                            File.Delete(change.Path);
                        }
                        else
                        {
                            File.WriteAllBytes(change.Path, change.Original);
                        }

                        if (change.OriginalMeta != null)
                        {
                            File.WriteAllBytes(change.Path + ".meta", change.OriginalMeta);
                        }
                    }
                    catch (Exception rollback) when (rollback is IOException or UnauthorizedAccessException)
                    {
                        // Keep restoring the rest; the original failure is what gets reported.
                    }
                }

                throw new IOException($"The output could not be written ({exception.Message}). The files already changed were restored.", exception);
            }

            return report;
        }

        private static void PlanWrite(List<Change> changes, CommitReport report, string path, byte[] content, string name)
        {
            if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(content))
            {
                report.Unchanged++;
                return;
            }

            changes.Add(new Change(path, name, content));
        }

        private static void PlanDelete(List<Change> changes, string path, string name)
        {
            changes.Add(new Change(path, name, null));
        }

        // Write to a temporary file in the same folder, then rename over the target: a reader sees the old file or the new one, never half of one.
        // The temporary name is unique, so two writers can never take each other's temporary file.
        private static void WriteAtomically(string path, byte[] content)
        {
            string temporary = path + "." + Guid.NewGuid().ToString("N").Substring(0, 12) + ".ugs-tmp";
            File.WriteAllBytes(temporary, content);
            try
            {
                if (File.Exists(path))
                {
                    try
                    {
                        File.Replace(temporary, path, null);
                        return;
                    }
                    catch (Exception exception) when (exception is PlatformNotSupportedException or IOException)
                    {
                        // Some file systems cannot replace in one step. Fall back to delete and rename;
                        // readers that land in the gap retry (the table watcher does).
                        if (!File.Exists(temporary))
                        {
                            throw;
                        }

                        File.Delete(path);
                    }
                }

                File.Move(temporary, path);
            }
            catch
            {
                File.Delete(temporary);
                throw;
            }
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
            text.Append("# Written by UGS: the CSV files it made here. Only these are ever replaced or removed by a build.\n");
            foreach (string file in files.OrderBy(file => file, StringComparer.Ordinal))
            {
                text.Append(file).Append('\n');
            }

            return text.ToString();
        }

        private static List<string> ReadCsvList(string path)
        {
            var files = new List<string>();
            try
            {
                if (!File.Exists(path))
                {
                    return files;
                }

                string[] lines = File.ReadAllText(path, Utf8).Split('\n');
                if (lines.Length == 0 || lines[0].TrimEnd('\r') != CsvListHeader)
                {
                    return files;
                }

                foreach (string raw in lines.Skip(1))
                {
                    string file = raw.TrimEnd('\r');
                    // Only plain .csv names in this folder: a hand-edited list must never make the tool delete anything else.
                    if (file.Length > 0 && !file.StartsWith("#", StringComparison.Ordinal) && file == Path.GetFileName(file)
                        && file.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                    {
                        files.Add(file);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }

            return files;
        }

        private static List<string> ReadPreviousDataFiles(string manifestPath)
        {
            var files = new List<string>();
            if (!File.Exists(manifestPath))
            {
                return files;
            }

            try
            {
                foreach (string line in File.ReadAllLines(manifestPath))
                {
                    if (!line.StartsWith("table ", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    foreach (string part in line.Split(' '))
                    {
                        if (part.StartsWith("file=", StringComparison.Ordinal))
                        {
                            string file = part[5..];
                            // Only plain file names: a manifest must never be able to make the tool delete something outside its folder.
                            if (file.Length > 0 && file == Path.GetFileName(file))
                            {
                                files.Add(file);
                            }
                        }
                    }
                }
            }
            catch (IOException)
            {
            }

            return files;
        }

        private sealed class Change
        {
            public Change(string path, string name, byte[]? content)
            {
                Path = path;
                Name = name;
                Content = content;
            }

            public string Path { get; }

            public string Name { get; }

            /// <summary>Null means delete.</summary>
            public byte[]? Content { get; }

            public byte[]? Original { get; set; }

            public byte[]? OriginalMeta { get; set; }
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
