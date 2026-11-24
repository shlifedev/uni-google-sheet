#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using HolySheet.Core.Building;
using HolySheet.Core.Codecs;
using HolySheet.Core.Config;
using HolySheet.Core.Csv;
using HolySheet.Core.Generation;
using HolySheet.Core.Output;
using HolySheet.Core.Schema;
using HolySheet.Core.Sources;
using HolySheet.Core.Transform;

namespace HolySheet.Core
{
    /// <summary>Per-run settings that do not belong in the config file.</summary>
    public sealed class TableBuildOptions
    {
        /// <summary>Additional project rules discovered by the host, such as Unity editor scripts.</summary>
        public IReadOnlyList<Type>? ValidatorTypes { get; set; }

        /// <summary>Loads the configured validator DLLs. Hosts can supply their own assembly load context.</summary>
        public Func<string, System.Reflection.Assembly>? LoadValidatorAssembly { get; set; }

        /// <summary>Do not contact Google; read the CSV snapshot saved by the last online build instead.</summary>
        public bool Offline { get; set; }

        /// <summary>Fetch online Google sources even when Drive reports the last downloaded version.</summary>
        public bool RefreshGoogle { get; set; }

        /// <summary>Keep remote input grids for single-tab downloads and immediate validation in an interactive window.</summary>
        public bool KeepGooglePreview { get; set; }

        internal IReadOnlyDictionary<string, GoogleSourcePreview>? ReplayGoogle { get; private set; }

        internal TableBuildOptions ForGooglePreview(IReadOnlyDictionary<string, GoogleSourcePreview> inputs)
        {
            var options = (TableBuildOptions)MemberwiseClone();
            options.KeepGooglePreview = true;
            options.ReplayGoogle = inputs;
            options.Only = null;
            options.Sources = null;
            return options;
        }

        /// <summary>The transform rules to apply. Null uses <see cref="TransformPipeline.CreateDefault"/>.</summary>
        public TransformPipeline? Pipeline { get; set; }

        /// <summary>Sources to read instead of the ones the config lists. For hosts that already hold the sheets in memory.</summary>
        public IReadOnlyList<IWorkbookSource>? Sources { get; set; }

        /// <summary>Replaces the HTTP transport, for tests.</summary>
        public HttpMessageHandler? HttpHandler { get; set; }

        /// <summary>Replaces the Google service addresses, for tests.</summary>
        public GoogleEndpoints? GoogleEndpoints { get; set; }

        /// <summary>
        /// The providers that sources with auth type "custom" choose from by name (see <see cref="IGoogleCredentialProvider"/>).
        /// The Unity editor fills this with the provider classes it finds; null means the host has none.
        /// </summary>
        public IReadOnlyList<IGoogleCredentialProvider>? CredentialProviders { get; set; }

        /// <summary>
        /// Read only these workbooks and sheets again; take every other sheet from the sheet cache, as the last build on this
        /// machine read it. Null or empty reads everything. See <see cref="SheetCache"/>.
        /// </summary>
        public SheetSelection? Only { get; set; }
    }

    /// <summary>What a build found and produced. Tables that could be built are present even when other parts failed.</summary>
    public sealed class BuildResult
    {
        public BuildResult(DiagnosticBag diagnostics)
        {
            Diagnostics = diagnostics;
        }

        public DiagnosticBag Diagnostics { get; }

        /// <summary>The sheets exactly as read, before any rule was applied. In a partial build, the cached sheets with the selected ones replaced.</summary>
        public IReadOnlyList<Workbook> Workbooks { get; internal set; } = Array.Empty<Workbook>();

        public IReadOnlyDictionary<(string Workbook, string Sheet), GoogleTab> GoogleTabs => _googleTabs;

        internal readonly Dictionary<(string Workbook, string Sheet), GoogleTab> _googleTabs = new();
        internal readonly Dictionary<string, GoogleSourcePreview> GoogleInputs = new();

        /// <summary>The source each workbook was read from, by workbook name. Kept by <see cref="ReleaseBuildData"/>, for the window's tree.</summary>
        public IReadOnlyDictionary<string, WorkbookOrigin> WorkbookOrigins { get; internal set; } = new Dictionary<string, WorkbookOrigin>();

        /// <summary>What a partial build read again; null for a full build.</summary>
        public SheetSelection? Only { get; internal set; }

        /// <summary>"Workbook / Sheet" of every sheet a partial build read again, in workbook order. Empty for a full build.</summary>
        public IReadOnlyList<string> RefreshedSheets { get; internal set; } = Array.Empty<string>();

        /// <summary>The tables after the transform rules, still as text.</summary>
        public IReadOnlyList<RawTable> RawTables { get; internal set; } = Array.Empty<RawTable>();

        /// <summary>The typed tables, in ordinal name order.</summary>
        public IReadOnlyList<TableData> Tables { get; internal set; } = Array.Empty<TableData>();

        public IReadOnlyList<EnumDefinition> Enums { get; internal set; } = Array.Empty<EnumDefinition>();

        /// <summary>Null when there is any error: a build either produces everything or nothing.</summary>
        public GeneratedOutput? Output { get; internal set; }

        private bool _released;
        private bool _succeeded;
        private int _tableCount;
        private int _rowCount;

        /// <summary>The CSV parser that was used ("builtin" or "sep").</summary>
        public string CsvBackend { get; internal set; } = "";

        public TimeSpan ReadTime { get; internal set; }

        public TimeSpan BuildTime { get; internal set; }

        /// <summary>How long each phase of the build took, in the order they ran. For finding out where time goes on large data.</summary>
        public IReadOnlyList<KeyValuePair<string, TimeSpan>> Phases => _phases;

        private readonly List<KeyValuePair<string, TimeSpan>> _phases = new();

        internal void AddPhase(string name, TimeSpan elapsed) => _phases.Add(new KeyValuePair<string, TimeSpan>(name, elapsed));

        public bool Success => _released ? _succeeded : Output != null;

        public int TableCount => _released ? _tableCount : Tables.Count;

        public int RowCount => _released ? _rowCount : Tables.Sum(table => table.Rows.Count);

        /// <summary>True after <see cref="ReleaseBuildData"/>.</summary>
        public bool IsReleased => _released;

        /// <summary>
        /// Hash of the manifest that was on disk when the build started; null when there was none. A commit checks it again,
        /// so a build never writes over output that another build wrote in the meantime.
        /// </summary>
        internal string? BaseManifestHash { get; set; }

        /// <summary>
        /// Lets go of everything that is only needed to write files: the sheets as read, the typed values and the generated output.
        /// What is left is what a person looks at afterwards: <see cref="RawTables"/>, <see cref="Diagnostics"/>, <see cref="Enums"/>
        /// and the counts. Call it after <see cref="TableBuild.Commit"/> when the result is kept around, as the editor window does;
        /// on 300,000 rows it brings a kept result from about 820 MB down to about 270 MB. With KeepGooglePreview, the remote
        /// grids stay available for checking a single-tab download. A released result cannot be committed.
        /// </summary>
        public void ReleaseBuildData()
        {
            if (_released)
            {
                return;
            }

            _succeeded = Success;
            _tableCount = Tables.Count;
            _rowCount = RowCount;
            _released = true;
            Workbooks = Array.Empty<Workbook>();
            Tables = Array.Empty<TableData>();
            Output = null;
        }
    }

    /// <summary>
    /// Reads the configured sources and produces generated code and data in memory. Nothing is written to disk here;
    /// <see cref="Commit"/> does that, and only for a build without errors.
    /// <para>
    /// Every run builds every table, because the shared outputs (manifest, tables class, enums) must agree with all of them.
    /// A partial build (<see cref="TableBuildOptions.Only"/>) differs only in where the sheets come from: the selected ones are
    /// read again, the rest come from the sheet cache. Tables made only of unselected sheets come out byte for byte as before,
    /// so their files are not written again.
    /// </para>
    /// </summary>
    public static class TableBuild
    {
        public const string ToolName = "holysheet/2.0.0";

        public static async Task<BuildResult> RunAsync(HolySheetConfig config, TableBuildOptions? options = null, CancellationToken cancellationToken = default)
        {
            Guard.NotNull(config, nameof(config));
            options ??= new TableBuildOptions();
            var diagnostics = new DiagnosticBag();
            var result = new BuildResult(diagnostics);
            config.Validate(diagnostics, requireSources: options.Sources == null);
            if (diagnostics.HasErrors)
            {
                return result;
            }

            ICsvParser parser;
            try
            {
                parser = CsvBackends.Resolve(config.CsvBackend);
            }
            catch (Exception exception) when (exception is NotSupportedException or ArgumentException)
            {
                diagnostics.AddError("config", exception.Message);
                return result;
            }

            result.CsvBackend = parser.Name;
            ITableCodec codec = TableCodecs.Find(config.Codegen.Serializer)!;
            result.BaseManifestHash = HashManifestOnDisk(config);

            // 1. Read every source. Sources are independent, so they are read at the same time.
            var readWatch = Stopwatch.StartNew();
            SheetSelection? only = options.Only is { IsEmpty: false } selection ? selection : null;
            SheetCache? cache = only == null ? null : LoadCacheForPartialBuild(config, diagnostics);
            if (diagnostics.HasErrors)
            {
                return result;
            }

            IReadOnlyList<IWorkbookSource> sources = options.Sources ?? WorkbookSources.Create(config, parser, options, only);
            var copies = new GoogleLocalCopies(config.BaseDirectory);
            var sourceBags = new DiagnosticBag[sources.Count];
            var loads = new Task<IReadOnlyList<Workbook>>[sources.Count];
            for (int index = 0; index < sources.Count; index++)
            {
                sourceBags[index] = new DiagnosticBag();
                IWorkbookSource source = sources[index];
                if (options.ReplayGoogle != null && config.Sources[index].IsRemote)
                {
                    if (!options.ReplayGoogle.TryGetValue(copies.CatalogPath(config.Sources[index]), out var preview))
                        throw new InvalidOperationException("The Google sources changed. Refresh the project before downloading a tab.");
                    source = preview;
                }

                DiagnosticBag bag = sourceBags[index];
                loads[index] = Task.Run(() => source.LoadAsync(bag, cancellationToken), cancellationToken);
            }

            var workbooks = new List<Workbook>();
            for (int index = 0; index < loads.Length; index++)
            {
                IReadOnlyList<Workbook> loaded = await loads[index].ConfigureAwait(false);
                // Sources made from the config are in config order, so each workbook can be tagged with the source it came from.
                SourceConfig? origin = options.Sources == null && index < config.Sources.Count ? config.Sources[index] : null;
                // A source may already know a link to the file (a spreadsheet in a Drive folder); it is kept.
                workbooks.AddRange(origin == null ? loaded : loaded.Select(workbook => workbook.WithOrigin(new WorkbookOrigin(origin.Type, DescribeSource(origin), workbook.Origin?.Link))));
                diagnostics.AddRange(sourceBags[index]);
            }

            if (only != null)
            {
                result.Only = only;
                workbooks = MergeIntoCache(cache!.Workbooks, workbooks, only, diagnostics, out List<string> refreshed);
                result.RefreshedSheets = refreshed;
            }

            result.Workbooks = workbooks;
            result.WorkbookOrigins = workbooks.Where(workbook => workbook.Origin != null)
                .GroupBy(workbook => workbook.Name, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().Origin!, StringComparer.Ordinal);
            if (options.KeepGooglePreview && options.Sources == null)
            {
                for (int i = 0; i < config.Sources.Count; i++)
                {
                    SourceConfig source = config.Sources[i];
                    if (!source.IsRemote) continue;
                    // A full read retains the exact source entry, even when a document is registered twice under different names.
                    var books = only == null ? loads[i].Result.ToList()
                        : workbooks.Where(book => book.Origin?.Type == source.Type && book.Origin.Source == DescribeSource(source)
                            && (source.Type != SourceType.Google || source.Name == null || book.Name == source.Name)).ToList();
                    result.GoogleInputs[copies.CatalogPath(source)] = new GoogleSourcePreview(books, sourceBags[i]);
                    foreach (Workbook book in books)
                    {
                        string? id = GoogleLocalCopies.SpreadsheetId(source, book);
                        if (id == null) continue;
                        foreach (Sheet sheet in book.Sheets)
                        {
                            string path = copies.CsvPath(id, sheet.Name);
                            DateTime? stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
                            if (stamp.HasValue || SheetRole.FromName(sheet.Name).Kind != SheetKind.Ignored)
                                result._googleTabs[(book.Name, sheet.Name)] = new GoogleTab(book.Name, sheet.Name, id, stamp, stamp.HasValue ? path : null);
                        }
                    }
                }
            }

            result.ReadTime = readWatch.Elapsed;
            result.AddPhase("read", readWatch.Elapsed);

            // 2. Apply the transform rules: sheets as written become tables ready to be typed.
            var buildWatch = Stopwatch.StartNew();
            var phase = Stopwatch.StartNew();
            TransformContext context = (options.Pipeline ?? TransformPipeline.CreateDefault()).Run(workbooks, config.Sheet, diagnostics);
            result.RawTables = context.Tables;
            result.AddPhase("transform", phase.Elapsed);
            phase.Restart();

            // 3. Enums: from Enums sheets and from Mapping columns. They must exist before column types are resolved.
            var enums = new Dictionary<string, EnumDefinition>(StringComparer.Ordinal);
            foreach (Sheet sheet in context.EnumSheets)
            {
                AddEnums(EnumSheetReader.Read(sheet, config.Sheet, diagnostics), enums, diagnostics);
            }

            foreach (RawTable table in context.Tables)
            {
                AddEnums(MappingBuilder.Build(table, diagnostics), enums, diagnostics);
            }

            // 4. Resolve types, then convert every cell. Tables do not depend on each other here, so they convert in parallel.
            List<(TableSchema Schema, RawTable Raw)> schemas = SchemaBuilder.Build(context.Tables, new TypeResolver(enums), diagnostics);
            schemas.Sort((left, right) => string.CompareOrdinal(left.Schema.Name, right.Schema.Name));
            result.AddPhase("schema", phase.Elapsed);
            phase.Restart();
            var tables = new TableData[schemas.Count];
            var tableBags = new DiagnosticBag[schemas.Count];
            ForEachTable(schemas.Count, config.Parallel, cancellationToken, index =>
            {
                tableBags[index] = new DiagnosticBag();
                tables[index] = RowConverter.Convert(schemas[index].Raw, schemas[index].Schema, config.Sheet.ArraySeparator, tableBags[index]);
            });
            foreach (DiagnosticBag bag in tableBags)
            {
                diagnostics.AddRange(bag);
            }

            result.Tables = tables;
            result.AddPhase("convert", phase.Elapsed);
            phase.Restart();
            List<EnumDefinition> sortedEnums = enums.Values.OrderBy(definition => definition.Name, StringComparer.Ordinal).ToList();
            result.Enums = sortedEnums;

            // 5. Checks that need every table: unique keys, references that resolve, names that do not collide.
            DataValidator.Validate(tables, diagnostics);
            ValidateNames(schemas.Select(entry => entry.Schema).ToList(), sortedEnums, config, diagnostics);
            if (!diagnostics.HasErrors && schemas.Count == 0)
            {
                diagnostics.AddError("input", "No table was found.", $"A table is a sheet whose header rows are, top to bottom: {string.Join(", ", config.Sheet.Header)}.");
            }

            if (!diagnostics.HasErrors)
            {
                TableValidation.Run(config, options, tables, diagnostics, cancellationToken);
            }
            result.AddPhase("validate", phase.Elapsed);
            phase.Restart();

            // 6. Only a build with no errors produces output.
            if (!diagnostics.HasErrors)
            {
                result.Output = Generate(tables, context.Tables, sortedEnums, config, codec, diagnostics, cancellationToken);
                result.AddPhase("generate", phase.Elapsed);
            }

            result.BuildTime = buildWatch.Elapsed;
            return result;
        }

        private static string DescribeSource(SourceConfig source) => source.Type switch
        {
            SourceType.Google => source.SpreadsheetId ?? "",
            SourceType.GoogleFolder => source.FolderId ?? "",
            _ => source.Path ?? ""
        };

        /// <summary>Synchronous form of <see cref="RunAsync"/> for hosts without an async entry point.</summary>
        public static BuildResult Run(HolySheetConfig config, TableBuildOptions? options = null, CancellationToken cancellationToken = default)
        {
            return Task.Run(() => RunAsync(config, options, cancellationToken), cancellationToken).GetAwaiter().GetResult();
        }

        /// <summary>Writes a successful build to the folders the config names. Returns what changed on disk.</summary>
        public static CommitReport Commit(HolySheetConfig config, BuildResult result)
        {
            if (result.Output == null)
            {
                throw new InvalidOperationException(result.IsReleased ? "The build data was released; run the build again to write it." : "A build with errors has no output to write.");
            }

            // Two builds of the same project can overlap: the CLI next to the editor, a watch next to a build. Commits are
            // taken one at a time, and a build whose starting point was replaced by another build's output is refused:
            // writing it would put back what that build changed (a partial build carries every unselected sheet as it was).
            using (OutputLock.Acquire(config.Resolve(config.CacheDirectory), CommitLockTimeout))
            {
                if (HashManifestOnDisk(config) != result.BaseManifestHash)
                {
                    throw new IOException("Another build wrote the output while this one was running. Nothing was written, so its changes are kept; run this build again.");
                }

                CommitReport report = OutputCommitter.Commit(result.Output, config.Resolve(config.Output.Code), config.Resolve(config.Output.Data),
                    config.Output.Csv == null ? null : config.Resolve(config.Output.Csv));
                SaveCache(config, result);
                return report;
            }
        }

        /// <summary>How long a commit waits for another build's commit to finish.</summary>
        internal static TimeSpan CommitLockTimeout { get; set; } = TimeSpan.FromMinutes(2);

        private static string? HashManifestOnDisk(HolySheetConfig config)
        {
            string manifestPath = Path.Combine(config.Resolve(config.Output.Data), config.Output.ManifestFile);
            try
            {
                return File.Exists(manifestPath) ? SheetCache.HashManifest(File.ReadAllBytes(manifestPath)) : null;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Unreadable now; a commit will find out properly. Use a value no real hash equals, so it is not mistaken for "no manifest".
                return "unreadable";
            }
        }

        // The cache records what was just written. If it cannot be saved, the old one is removed rather than left behind:
        // a later partial build then asks for a full build instead of starting from sheets that no longer match the data.
        private static void SaveCache(HolySheetConfig config, BuildResult result)
        {
            string directory = config.Resolve(config.CacheDirectory);
            try
            {
                SheetCache.Save(directory, result.Workbooks, result.Output!.ManifestText);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                SheetCache.Delete(directory);
            }
        }

        private static SheetCache? LoadCacheForPartialBuild(HolySheetConfig config, DiagnosticBag diagnostics)
        {
            SheetCache? cache = SheetCache.Load(config.Resolve(config.CacheDirectory));
            if (cache == null)
            {
                diagnostics.AddError("partial build", "There is no sheet cache from an earlier build on this machine.",
                    "Run a full build once. A partial build reads the selected sheets and takes the others from that build.");
                return null;
            }

            string manifestPath = Path.Combine(config.Resolve(config.Output.Data), config.Output.ManifestFile);
            if (!File.Exists(manifestPath) || SheetCache.HashManifest(File.ReadAllBytes(manifestPath)) != cache.ManifestHash)
            {
                diagnostics.AddError("partial build", "The data folder was not written by the last build on this machine, for example after a pull.",
                    "Run a full build. A partial build now could put back values from older sheets.");
                return null;
            }

            return cache;
        }

        // The cached workbooks, in their order, with the selected parts replaced by what was just read.
        // A selected workbook is replaced as a whole: sheets added to it appear, removed ones disappear.
        // A selected sheet is replaced, added at the end of its workbook if new, or removed if the source no longer has it.
        private static List<Workbook> MergeIntoCache(IReadOnlyList<Workbook> cached, List<Workbook> fresh, SheetSelection only, DiagnosticBag diagnostics, out List<string> refreshed)
        {
            var refreshedSheets = new List<string>();
            refreshed = refreshedSheets;
            var freshByName = new Dictionary<string, Workbook>(StringComparer.Ordinal);
            foreach (Workbook workbook in fresh)
            {
                freshByName[workbook.Name] = workbook;
            }

            bool sourceFailed = diagnostics.HasErrors;
            var merged = new List<Workbook>(cached.Count + 1);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (Workbook workbook in cached)
            {
                seen.Add(workbook.Name);
                merged.Add(only.Touches(workbook.Name) ? Merge(workbook, freshByName.TryGetValue(workbook.Name, out Workbook? read) ? read : null) : workbook);
            }

            foreach (Workbook workbook in fresh)
            {
                if (seen.Add(workbook.Name))
                {
                    merged.Add(Merge(new Workbook(workbook.Name, Array.Empty<Sheet>(), workbook.Origin), workbook));
                }
            }

            foreach (string name in only.Workbooks.Where(name => !seen.Contains(name)))
            {
                if (!sourceFailed)
                {
                    diagnostics.AddError(name, $"No source has a workbook named '{name}'.", "Use the name the tree shows: the file name for xlsx, the folder name for CSV, the spreadsheet name for Google.");
                }
            }

            return merged;

            Workbook Merge(Workbook old, Workbook? read)
            {
                if (read == null)
                {
                    // No source returned the workbook at all. That is far more often a name that does not match (an offline
                    // snapshot, a renamed file) than a deliberate removal, and dropping its sheets here would delete their
                    // tables' data on commit. A partial build never removes a whole workbook; a full build does.
                    if (!sourceFailed)
                    {
                        diagnostics.AddError(old.Name, "The workbook was not found in any source, so this partial build cannot read it again.",
                            "Check the workbook name. If it was removed or renamed on purpose, run a full build.");
                    }

                    return old;
                }

                if (only.IsWholeWorkbook(old.Name))
                {
                    refreshedSheets.AddRange(read.Sheets.Select(sheet => sheet.Location));
                    return read;
                }

                var sheets = old.Sheets.ToList();
                foreach ((string _, string sheetName) in only.Sheets.Where(entry => entry.Workbook == old.Name))
                {
                    Sheet? replacement = read.Sheets.FirstOrDefault(sheet => sheet.Name == sheetName);
                    int index = sheets.FindIndex(sheet => sheet.Name == sheetName);
                    if (replacement == null)
                    {
                        if (index >= 0)
                        {
                            sheets.RemoveAt(index);
                            if (!sourceFailed)
                            {
                                diagnostics.AddWarning($"{old.Name} / {sheetName}", "The sheet was not found in its source; it is left out of this build.");
                            }
                        }
                        else if (!sourceFailed)
                        {
                            diagnostics.AddError($"{old.Name} / {sheetName}", "No such sheet.", "Check the workbook and sheet names.");
                        }

                        continue;
                    }

                    refreshedSheets.Add(replacement.Location);
                    if (index >= 0)
                    {
                        sheets[index] = replacement;
                    }
                    else
                    {
                        sheets.Add(replacement);
                    }
                }

                return new Workbook(old.Name, sheets, read.Origin ?? old.Origin);
            }
        }

        private static GeneratedOutput? Generate(TableData[] tables, IReadOnlyList<RawTable> rawTables, List<EnumDefinition> enums, HolySheetConfig config, ITableCodec codec, DiagnosticBag diagnostics, CancellationToken cancellationToken)
        {
            // Signing is decided first: the public key is part of the generated code.
            string? publicKey = config.Signing.PublicKey;
            System.Security.Cryptography.RSAParameters? privateKey = null;
            string? privateKeyText = SigningKeys.ReadPrivateKey(config);
            if (privateKeyText != null)
            {
                try
                {
                    privateKey = SigningKeys.ParsePrivateKey(privateKeyText);
                }
                catch (FormatException exception)
                {
                    diagnostics.AddError("signing", exception.Message);
                    return null;
                }

                string derived = SigningKeys.FormatPublicKey(privateKey.Value);
                if (!string.IsNullOrEmpty(publicKey) && publicKey != derived)
                {
                    diagnostics.AddError("signing", "The private key does not belong to the public key in the config.", $"Set signing.publicKey to the key that matches: {derived}");
                    return null;
                }

                publicKey = derived;
            }
            else if (!string.IsNullOrEmpty(publicKey))
            {
                diagnostics.AddError("signing", "A public key is configured, so clients will refuse unsigned data, but no private key is available to sign with.",
                    "Provide the private key through signing.privateKeyFile or signing.privateKeyEnv, or remove signing.publicKey to build unsigned data.");
                return null;
            }

            var output = new GeneratedOutput { ManifestFileName = config.Output.ManifestFile };
            List<TableSchema> schemas = tables.Select(table => table.Schema).ToList();
            foreach (TableSchema schema in schemas)
            {
                schema.Hash = SchemaHasher.HashTable(schema, codec.Name);
            }

            string schemaHash = SchemaHasher.HashSet(schemas, enums, codec.Name);
            var codegen = new CodegenOptions(config.Codegen.Namespace, config.Codegen.TablesClass, codec, config.Output.ManifestFile, publicKey);

            var encoded = new byte[tables.Length][];
            ForEachTable(tables.Length, config.Parallel, cancellationToken, index => encoded[index] = codec.Encode(tables[index]));

            var manifestTables = new List<ManifestTable>(tables.Length);
            for (int index = 0; index < tables.Length; index++)
            {
                TableSchema schema = schemas[index];
                string file = schema.Name + ManifestWriter.DataExtension;
                output.CodeFiles.Add(schema.RowTypeName + ".g.cs", RowCodeGenerator.Generate(schema, codegen));
                output.CodeFiles.Add(schema.TableTypeName + ".g.cs", TableCodeGenerator.Generate(schema, codegen));
                output.DataFiles.Add(file, encoded[index]);
                manifestTables.Add(new ManifestTable(schema.Name, file, tables[index].Rows.Count, encoded[index].Length, SchemaHasher.Sha256Hex(encoded[index]), schema.Hash));
            }

            if (enums.Count > 0)
            {
                string enumFile = string.Equals(config.Codegen.TablesClass + ".g.cs", EnumCodeGenerator.FileName, StringComparison.OrdinalIgnoreCase)
                    ? "Enums.Definitions.g.cs" : EnumCodeGenerator.FileName;
                output.CodeFiles.Add(enumFile, EnumCodeGenerator.Generate(enums, codegen));
            }

            output.CodeFiles.Add(config.Codegen.TablesClass + ".g.cs", TablesCodeGenerator.Generate(schemas, schemaHash, codegen));
            if (config.Output.Schema)
            {
                output.DataFiles.Add(SchemaJson.FileName, new System.Text.UTF8Encoding(false).GetBytes(SchemaJson.Write(schemas, enums, codec.Name, schemaHash)));
            }

            if (config.Output.Csv != null)
            {
                foreach (RawTable raw in rawTables)
                {
                    output.CsvFiles[raw.Name + ".csv"] = raw.ToCsv(config.Sheet);
                }
            }

            var properties = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["tool"] = ToolName };
            if (!string.IsNullOrWhiteSpace(config.BuildLabel))
            {
                properties["build"] = config.BuildLabel!.Trim();
            }

            string signedText = ManifestWriter.Write(codec.Name, schemaHash, manifestTables, properties, out string contentHash);
            output.ManifestText = privateKey != null ? ManifestWriter.AppendSignature(signedText, SigningKeys.Sign(signedText, privateKey.Value)) : signedText;
            output.SchemaHash = schemaHash;
            output.ContentHash = contentHash;
            return output;
        }

        private static void ForEachTable(int count, bool parallel, CancellationToken cancellationToken, Action<int> action)
        {
            if (parallel && count > 1)
            {
                Parallel.For(0, count, new ParallelOptions { CancellationToken = cancellationToken }, action);
                return;
            }

            for (int index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                action(index);
            }
        }

        private static void AddEnums(List<EnumDefinition> definitions, Dictionary<string, EnumDefinition> enums, DiagnosticBag diagnostics)
        {
            foreach (EnumDefinition definition in definitions)
            {
                if (enums.TryGetValue(definition.Name, out EnumDefinition? existing))
                {
                    diagnostics.AddError(definition.Location, $"The enum '{definition.Name}' is already defined at {existing.Location}.", "Rename one of them.");
                    continue;
                }

                enums.Add(definition.Name, definition);
            }
        }

        // Generated type names (row, table, reference, enum, tables class) share one namespace and must not collide.
        private static void ValidateNames(List<TableSchema> schemas, List<EnumDefinition> enums, HolySheetConfig config, DiagnosticBag diagnostics)
        {
            var owners = new Dictionary<string, string>(StringComparer.Ordinal) { [config.Codegen.TablesClass] = "the tables class" };
            void Claim(string name, string location)
            {
                if (owners.TryGetValue(name, out string? owner))
                {
                    diagnostics.AddError(location, $"The generated name '{name}' is also used by {owner}.", "Rename the table or the enum. A table 'X' produces the types XRow, XTable and XRef.");
                    return;
                }

                owners.Add(name, location);
            }

            foreach (EnumDefinition definition in enums)
            {
                Claim(definition.Name, definition.Location);
            }

            foreach (TableSchema schema in schemas)
            {
                Claim(schema.RowTypeName, schema.Location);
                Claim(schema.TableTypeName, schema.Location);
                Claim(schema.RefTypeName, schema.Location);
                if (schema.Name == config.Codegen.TablesClass || TablesCodeGenerator.ReservedNames.Contains(schema.Name))
                {
                    diagnostics.AddError(schema.Location, $"'{schema.Name}' cannot be a table name: the tables class already has a member with that name.", "Rename the sheet.");
                }
            }
        }
    }
}
