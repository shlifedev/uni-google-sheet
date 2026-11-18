using System.Runtime.InteropServices;
using UGS.Core;
using UGS.Core.Config;
using UGS.Core.Csv;
using UGS.Core.Output;
using UGS.Core.Schema;

namespace UGS.Cli;

/// <summary>
/// Entry point of the <c>ugs</c> command. It reads arguments, calls the engine and prints what happened.
/// Exit codes: 0 success, 1 the sheets have errors (or data failed verification), 2 the command line is wrong,
/// 3 the tool could not do its job (missing config, unreadable file, cannot write).
/// </summary>
public static class Program
{
    public const int ExitSuccess = 0;
    public const int ExitFailed = 1;
    public const int ExitUsage = 2;
    public const int ExitError = 3;

    public static async Task<int> Main(string[] args)
    {
        // Referencing the Sep backend from here is what puts its assembly next to the tool; the engine finds it by name.
        _ = typeof(UGS.Csv.SepCsvParser);

        if (!Arguments.TryParse(args, out Arguments? arguments, out string error))
        {
            Console.Error.WriteLine("error: " + error);
            Console.Error.WriteLine("Run 'ugs help' for usage.");
            return ExitUsage;
        }

        var console = new Output(arguments!.Json);
        try
        {
            return arguments.Command switch
            {
                "help" or "" => Help(),
                "version" => Version(),
                "info" => Commands.Info(arguments, console),
                "init" => Commands.Init(arguments, console),
                "build" => await Commands.BuildAsync(arguments, console, commit: true).ConfigureAwait(false),
                "check" => await Commands.BuildAsync(arguments, console, commit: false).ConfigureAwait(false),
                "watch" => await Commands.WatchAsync(arguments, console).ConfigureAwait(false),
                "diff" => Commands.Diff(arguments, console),
                "verify" => Commands.Verify(arguments, console),
                "keygen" => Commands.Keygen(arguments, console),
                _ => Unknown(arguments.Command)
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonFormatException or FormatException)
        {
            console.Error(exception.Message);
            return ExitError;
        }
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"error: unknown command '{command}'.");
        Console.Error.WriteLine("Run 'ugs help' for usage.");
        return ExitUsage;
    }

    private static int Version()
    {
        Console.WriteLine(TableBuild.ToolName);
        return ExitSuccess;
    }

    private static int Help()
    {
        Console.WriteLine("""
            ugs - build game tables from xlsx, CSV and Google Sheets into C# code and binary data

            Usage: ugs <command> [options]

            Commands:
              init                 Create a ugs.json config (and a sample sheet with --sample)
              build                Read the sources and write generated code and data
              check                Read and validate only; write nothing
              watch                Build, then build again whenever a source changes
              diff <old> <new>     Show what changed between two manifests (files or data folders)
              verify <dataDir>     Check a data folder against its manifest: hashes and signature
              keygen               Create a signing key pair
              info                 Show the runtime and the CSV backends available
              version              Print the tool version

            Options:
              -c, --config <path>  Config file (default: ./ugs.json)
              --json               Machine-readable output on stdout
              --offline            Do not contact Google; use the saved CSV snapshot           (build, check, watch)
              --label <text>       Text written to the manifest as the build line              (build, watch)
              --csv <backend>      CSV parser: builtin (default) or sep                        (build, check, watch)
              --sequential         Convert and encode tables on one thread                     (build, check, watch)
              --timings            Print how long each phase took                              (build, check, watch)
              --only <names>       Read only these again, the rest from the last build:        (build, check)
                                   "Item.xlsx" (a workbook) or "Item.xlsx/Item.Rare" (a sheet);
                                   comma separated or repeated
              --interval <sec>     How often to re-read Google sources while watching (30)     (watch)
              --dir <path>         Where to create the config (default: current folder)        (init)
              --sample             Also create a sample workbook as CSV                        (init)
              --unity              Use output paths suited to a Unity project                  (init)
              --out <path>         Private key file to write (default: ugs.private.key)        (keygen)
              --write-config       Store the public key in the config                          (keygen)
              --public-key <key>   Verify the signature with this key instead of the config's  (verify)

            Exit codes: 0 success, 1 sheet errors or failed verification, 2 bad command line, 3 tool failure
            """);
        return ExitSuccess;
    }
}

/// <summary>The parsed command line.</summary>
public sealed class Arguments
{
    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal) { "--json", "--offline", "--sequential", "--sample", "--unity", "--write-config", "--force", "--timings" };

    private static readonly HashSet<string> ValueOptions = new(StringComparer.Ordinal) { "--config", "--label", "--csv", "--interval", "--dir", "--out", "--public-key" };

    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly List<string> _only = [];

    /// <summary>Every --only value, in order.</summary>
    public IReadOnlyList<string> Only => _only;
    private readonly HashSet<string> _flags = new(StringComparer.Ordinal);

    public string Command { get; private set; } = "";

    public List<string> Positional { get; } = [];

    public bool Json => Has("--json");

    public bool Has(string flag) => _flags.Contains(flag);

    public string? Get(string option) => _values.TryGetValue(option, out string? value) ? value : null;

    public string ConfigPath => Path.GetFullPath(Get("--config") ?? UgsConfig.DefaultFileName);

    public static bool TryParse(string[] args, out Arguments? arguments, out string error)
    {
        arguments = new Arguments();
        error = "";
        for (int index = 0; index < args.Length; index++)
        {
            string arg = args[index] switch
            {
                "-c" => "--config",
                "-h" or "--help" => "help",
                "--version" => "version",
                _ => args[index]
            };

            if (arg == "--only")
            {
                if (index + 1 >= args.Length)
                {
                    error = "option '--only' needs a value.";
                    return false;
                }

                arguments._only.Add(args[++index]);
            }
            else if (Flags.Contains(arg))
            {
                arguments._flags.Add(arg);
            }
            else if (ValueOptions.Contains(arg))
            {
                if (index + 1 >= args.Length)
                {
                    error = $"option '{arg}' needs a value.";
                    return false;
                }

                arguments._values[arg] = args[++index];
            }
            else if (arg.StartsWith('-') && arg.Length > 1)
            {
                error = $"unknown option '{arg}'.";
                return false;
            }
            else if (arguments.Command.Length == 0)
            {
                arguments.Command = arg;
            }
            else
            {
                arguments.Positional.Add(arg);
            }
        }

        return true;
    }
}

/// <summary>Prints for people or, with --json, collects one JSON document for programs.</summary>
public sealed class Output(bool json)
{
    private static readonly bool UseColor = !Console.IsOutputRedirected && Environment.GetEnvironmentVariable("NO_COLOR") == null;

    public bool Json { get; } = json;

    public void Info(string message)
    {
        if (!Json)
        {
            Console.WriteLine(message);
        }
    }

    public void Success(string message)
    {
        if (!Json)
        {
            WriteColored(message, ConsoleColor.Green);
        }
    }

    public void Error(string message)
    {
        if (Json)
        {
            Console.WriteLine(JsonValue.Object().Set("success", false).Set("error", message).ToValue().ToJson());
        }
        else
        {
            Console.Error.WriteLine("error: " + message);
        }
    }

    public void Diagnostics(IReadOnlyList<Diagnostic> diagnostics)
    {
        if (Json)
        {
            return;
        }

        foreach (Diagnostic diagnostic in diagnostics)
        {
            bool isError = diagnostic.Severity == Severity.Error;
            WriteColored((isError ? "error" : "warning") + ": " + diagnostic.Location, isError ? ConsoleColor.Red : ConsoleColor.Yellow);
            Console.WriteLine("  " + diagnostic.Message);
            if (diagnostic.Hint.Length > 0)
            {
                Console.WriteLine("  fix: " + diagnostic.Hint);
            }
        }
    }

    public void Document(JsonObject document)
    {
        if (Json)
        {
            Console.WriteLine(document.ToValue().ToJson());
        }
    }

    private static void WriteColored(string message, ConsoleColor color)
    {
        if (UseColor)
        {
            Console.ForegroundColor = color;
            Console.WriteLine(message);
            Console.ResetColor();
        }
        else
        {
            Console.WriteLine(message);
        }
    }

    public static JsonValue ToJson(Diagnostic diagnostic)
    {
        JsonObject item = JsonValue.Object()
            .Set("severity", diagnostic.Severity == Severity.Error ? "error" : "warning")
            .Set("location", diagnostic.Location)
            .Set("message", diagnostic.Message)
            .Set("hint", diagnostic.Hint);
        if (diagnostic.Origin is { } origin)
        {
            item.Set("workbook", origin.Workbook).Set("sheet", origin.Sheet).Set("cell", origin.A1).Set("row", origin.Row).Set("column", origin.Column);
        }

        return item.ToValue();
    }
}

internal static class Commands
{
    public static int Info(Arguments arguments, Output console)
    {
        ICsvParser? fast = CsvBackends.Fast;
        string backends = fast != null ? $"builtin (default), {fast.Name}" : $"builtin (sep unavailable: {CsvBackends.FastUnavailableReason})";
        if (console.Json)
        {
            console.Document(JsonValue.Object()
                .Set("tool", TableBuild.ToolName)
                .Set("runtime", RuntimeInformation.FrameworkDescription)
                .Set("csvBackends", JsonValue.Array(fast != null ? ["builtin", fast.Name] : ["builtin"]))
                .Set("csvDefault", CsvBackends.Resolve(CsvBackends.Auto).Name)
                .Set("serializers", JsonValue.Array(UGS.Core.Codecs.TableCodecs.All.Select(codec => codec.Name))));
            return Program.ExitSuccess;
        }

        console.Info($"tool         {TableBuild.ToolName}");
        console.Info($"runtime      {RuntimeInformation.FrameworkDescription} ({RuntimeInformation.RuntimeIdentifier})");
        console.Info($"csv          {backends}");
        console.Info($"serializers  {string.Join(", ", UGS.Core.Codecs.TableCodecs.All.Select(codec => codec.Name))}");
        console.Info($"config       {arguments.ConfigPath}{(File.Exists(arguments.ConfigPath) ? "" : " (not found)")}");
        return Program.ExitSuccess;
    }

    public static int Init(Arguments arguments, Output console)
    {
        string directory = Path.GetFullPath(arguments.Get("--dir") ?? ".");
        string path = Path.Combine(directory, UgsConfig.DefaultFileName);
        if (File.Exists(path) && !arguments.Has("--force"))
        {
            console.Error($"'{path}' already exists. Use --force to replace it.");
            return Program.ExitError;
        }

        bool unity = arguments.Has("--unity") || (Directory.Exists(Path.Combine(directory, "Assets")) && Directory.Exists(Path.Combine(directory, "ProjectSettings")));
        var config = new UgsConfig { BaseDirectory = directory };
        config.Sources.Add(new SourceConfig { Type = arguments.Has("--sample") ? SourceType.Csv : SourceType.Xlsx, Path = "Sheets" });
        if (unity)
        {
            // Generated code is compiled by Unity; data goes to StreamingAssets so it is a plain file the watcher can follow.
            config.Output.Code = "Assets/Game/Generated/Tables";
            config.Output.Data = "Assets/StreamingAssets/Tables";
        }

        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(Path.Combine(directory, "Sheets"));
        config.Save(path);
        console.Info($"created {path}");
        if (arguments.Has("--sample"))
        {
            foreach ((string name, string content) in Samples.Files)
            {
                string samplePath = Path.Combine(directory, "Sheets", name);
                File.WriteAllText(samplePath, content);
                console.Info($"created {samplePath}");
            }
        }

        console.Info(arguments.Has("--sample") ? "Run 'ugs build' to generate code and data." : "Put .xlsx files in the Sheets folder, then run 'ugs build'.");
        console.Document(JsonValue.Object().Set("success", true).Set("config", path));
        return Program.ExitSuccess;
    }

    private static UgsConfig LoadConfig(Arguments arguments)
    {
        UgsConfig config = UgsConfig.Load(arguments.ConfigPath);
        if (arguments.Get("--label") is { } label)
        {
            config.BuildLabel = label;
        }

        if (arguments.Get("--csv") is { } csv)
        {
            config.CsvBackend = csv;
        }

        if (arguments.Has("--sequential"))
        {
            config.Parallel = false;
        }

        return config;
    }

    public static async Task<int> BuildAsync(Arguments arguments, Output console, bool commit)
    {
        UgsConfig config = LoadConfig(arguments);
        return await BuildOnceAsync(config, arguments, console, commit, CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task<int> BuildOnceAsync(UgsConfig config, Arguments arguments, Output console, bool commit, CancellationToken cancellationToken)
    {
        SheetSelection? only = arguments.Only.Count > 0 ? SheetSelection.Parse(arguments.Only) : null;
        BuildResult result = await TableBuild.RunAsync(config, new TableBuildOptions { Offline = arguments.Has("--offline"), Only = only }, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<Diagnostic> diagnostics = result.Diagnostics.Items;
        console.Diagnostics(diagnostics);

        CommitReport? report = null;
        if (result.Success && commit)
        {
            report = TableBuild.Commit(config, result);
        }

        if (console.Json)
        {
            JsonObject document = JsonValue.Object()
                .Set("success", result.Success)
                .Set("csvBackend", result.CsvBackend)
                .Set("errors", result.Diagnostics.ErrorCount)
                .Set("warnings", result.Diagnostics.WarningCount)
                .Set("readMs", (long)result.ReadTime.TotalMilliseconds)
                .Set("buildMs", (long)result.BuildTime.TotalMilliseconds)
                .Set("phases", JsonValue.Array(result.Phases.Select(phase => JsonValue.Object().Set("name", phase.Key).Set("ms", (long)phase.Value.TotalMilliseconds).ToValue())))
                .Set("diagnostics", JsonValue.Array(diagnostics.Select(Output.ToJson)))
                .Set("tables", JsonValue.Array(result.Tables.Select(table => JsonValue.Object().Set("name", table.Schema.Name).Set("rows", table.Rows.Count).Set("columns", table.Schema.Columns.Count).ToValue())))
                .Set("enums", JsonValue.Array(result.Enums.Select(definition => definition.Name)));
            if (result.Only != null)
            {
                document.Set("refreshed", JsonValue.Array(result.RefreshedSheets));
            }
            if (result.Output != null)
            {
                document.Set("schema", result.Output.SchemaHash).Set("content", result.Output.ContentHash);
            }

            if (report != null)
            {
                document.Set("written", JsonValue.Array(report.Written)).Set("deleted", JsonValue.Array(report.Deleted)).Set("unchanged", report.Unchanged);
            }

            console.Document(document);
        }

        if (result.Only != null && !console.Json)
        {
            console.Info($"  partial  read again: {(result.RefreshedSheets.Count == 0 ? "nothing" : string.Join(", ", result.RefreshedSheets))}; other sheets from the last build");
        }

        if (arguments.Has("--timings"))
        {
            console.Info("  phases  " + string.Join(", ", result.Phases.Select(phase => $"{phase.Key} {phase.Value.TotalMilliseconds:F0} ms")));
        }

        string summary = $"{result.Tables.Count} tables, {result.RowCount} rows, {result.Enums.Count} enums";
        string timing = $"read {result.ReadTime.TotalMilliseconds:F0} ms, build {result.BuildTime.TotalMilliseconds:F0} ms, csv {result.CsvBackend}";
        if (!result.Success)
        {
            console.Info($"failed: {result.Diagnostics.ErrorCount} errors, {result.Diagnostics.WarningCount} warnings. Nothing was written.");
            return Program.ExitFailed;
        }

        if (report == null)
        {
            console.Success($"ok: {summary}, {result.Diagnostics.WarningCount} warnings ({timing}). check writes nothing.");
            return Program.ExitSuccess;
        }

        foreach (string file in report.Written)
        {
            console.Info("  wrote   " + file);
        }

        foreach (string file in report.Deleted)
        {
            console.Info("  deleted " + file);
        }

        console.Success(report.HasChanges
            ? $"built: {summary}; {report.Written.Count} files written, {report.Deleted.Count} deleted, {report.Unchanged} unchanged ({timing})"
            : $"up to date: {summary}; nothing changed ({timing})");
        return Program.ExitSuccess;
    }

    public static async Task<int> WatchAsync(Arguments arguments, Output console)
    {
        UgsConfig config = LoadConfig(arguments);
        int interval = int.TryParse(arguments.Get("--interval"), out int seconds) && seconds > 0 ? seconds : 30;
        bool hasRemote = config.Sources.Any(source => source.IsRemote) && !arguments.Has("--offline");
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, args) =>
        {
            args.Cancel = true;
            stop.Cancel();
        };

        using var changed = new SemaphoreSlim(0);
        IEnumerable<string> paths = config.Sources.Where(source => source.Path != null).Select(source => config.Resolve(source.Path!))
            .Concat(arguments.Has("--offline") ? config.Sources.Where(source => source.Snapshot != null).Select(source => config.Resolve(source.Snapshot!)) : []);
        using var watcher = new SourceWatcher(paths, TimeSpan.FromMilliseconds(300), () =>
        {
            if (changed.CurrentCount == 0)
            {
                changed.Release();
            }
        });

        await BuildOnceAsync(config, arguments, console, commit: true, stop.Token).ConfigureAwait(false);
        console.Info($"watching {watcher.WatchedCount} folder(s){(hasRemote ? $", re-reading Google every {interval} s" : "")}. Press Ctrl+C to stop.");
        while (!stop.IsCancellationRequested)
        {
            try
            {
                // Local sources wake this up through the watcher. Remote ones have no change notification, so they are polled.
                bool signalled = await changed.WaitAsync(hasRemote ? TimeSpan.FromSeconds(interval) : Timeout.InfiniteTimeSpan, stop.Token).ConfigureAwait(false);
                if (signalled)
                {
                    console.Info($"[{DateTime.Now:HH:mm:ss}] change detected");
                }

                // A config edit takes effect on the next build without restarting the watch.
                config = LoadConfig(arguments);
                await BuildOnceAsync(config, arguments, console, commit: true, stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception exception) when (exception is IOException or JsonFormatException)
            {
                console.Error(exception.Message);
            }
        }

        return Program.ExitSuccess;
    }

    private static TableManifest ReadManifest(string path)
    {
        string file = Directory.Exists(path) ? Path.Combine(path, TableManifest.DefaultFileName) : path;
        if (!File.Exists(file))
        {
            throw new IOException($"There is no manifest at '{file}'.");
        }

        try
        {
            return TableManifest.Parse(File.ReadAllText(file));
        }
        catch (TableLoadException exception)
        {
            throw new IOException($"{file}: {exception.Message}");
        }
    }

    public static int Diff(Arguments arguments, Output console)
    {
        if (arguments.Positional.Count != 2)
        {
            Console.Error.WriteLine("error: diff needs two manifests: ugs diff <old> <new>");
            return Program.ExitUsage;
        }

        TableManifest older = ReadManifest(arguments.Positional[0]);
        TableManifest newer = ReadManifest(arguments.Positional[1]);
        TableManifestDiff diff = TableManifest.Diff(older, newer);
        long downloadBytes = diff.Added.Concat(diff.Changed).Sum(name => newer.FindTable(name)!.Bytes);
        if (console.Json)
        {
            // This is the patch plan: the files a client holding the old data needs to fetch, and the ones it can drop.
            console.Document(JsonValue.Object()
                .Set("hasChanges", diff.HasChanges)
                .Set("requiresNewCode", diff.RequiresNewCode)
                .Set("from", JsonValue.Object().Set("content", older.Content).Set("schema", older.Schema))
                .Set("to", JsonValue.Object().Set("content", newer.Content).Set("schema", newer.Schema))
                .Set("added", JsonValue.Array(diff.Added))
                .Set("changed", JsonValue.Array(diff.Changed))
                .Set("schemaChanged", JsonValue.Array(diff.SchemaChanged))
                .Set("removed", JsonValue.Array(diff.Removed))
                .Set("unchanged", JsonValue.Array(diff.Unchanged))
                .Set("download", JsonValue.Array(diff.Added.Concat(diff.Changed).Select(name =>
                {
                    TableManifestEntry entry = newer.FindTable(name)!;
                    return JsonValue.Object().Set("table", name).Set("file", entry.File).Set("bytes", entry.Bytes).Set("hash", entry.Hash).ToValue();
                })))
                .Set("downloadBytes", downloadBytes));
            return Program.ExitSuccess;
        }

        if (!diff.HasChanges)
        {
            console.Info("no changes: both manifests describe the same data.");
            return Program.ExitSuccess;
        }

        foreach (string name in diff.Added)
        {
            console.Info($"  + {name}  ({newer.FindTable(name)!.Rows} rows, {newer.FindTable(name)!.Bytes} bytes)");
        }

        foreach (string name in diff.Changed)
        {
            TableManifestEntry before = older.FindTable(name)!;
            TableManifestEntry after = newer.FindTable(name)!;
            string layout = diff.SchemaChanged.Contains(name) ? ", layout changed" : "";
            console.Info($"  ~ {name}  (rows {before.Rows} -> {after.Rows}, bytes {before.Bytes} -> {after.Bytes}{layout})");
        }

        foreach (string name in diff.Removed)
        {
            console.Info($"  - {name}");
        }

        console.Info($"{diff.Added.Count} added, {diff.Changed.Count} changed, {diff.Removed.Count} removed, {diff.Unchanged.Count} unchanged; {downloadBytes} bytes to fetch.");
        console.Info(diff.RequiresNewCode
            ? "The layout changed: clients need the new generated code, not only the new data."
            : "The layout is the same: clients can load the new data with the code they have.");
        return Program.ExitSuccess;
    }

    public static int Verify(Arguments arguments, Output console)
    {
        if (arguments.Positional.Count != 1)
        {
            Console.Error.WriteLine("error: verify needs a data folder: ugs verify <dataDir>");
            return Program.ExitUsage;
        }

        string directory = Path.GetFullPath(arguments.Positional[0]);
        string? publicKey = arguments.Get("--public-key");
        string manifestName = TableManifest.DefaultFileName;
        if (File.Exists(arguments.ConfigPath))
        {
            UgsConfig config = UgsConfig.Load(arguments.ConfigPath);
            publicKey ??= config.Signing.PublicKey;
            manifestName = config.Output.ManifestFile;
        }

        var problems = new List<string>();
        TableManifest? manifest = null;
        try
        {
            manifest = TableManifest.Parse(File.ReadAllText(Path.Combine(directory, manifestName)));
        }
        catch (Exception exception) when (exception is TableLoadException or IOException)
        {
            problems.Add("manifest: " + exception.Message);
        }

        string signature = "not checked (no public key given)";
        if (manifest != null)
        {
            if (publicKey != null)
            {
                bool valid = manifest.IsSigned && TableSignature.Verify(manifest, publicKey);
                signature = valid ? "valid" : manifest.IsSigned ? "INVALID" : "MISSING";
                if (!valid)
                {
                    problems.Add(manifest.IsSigned ? "signature: does not match the public key" : "signature: the manifest is not signed");
                }
            }
            else if (manifest.IsSigned)
            {
                signature = "present, not checked (no public key given)";
            }

            foreach (TableManifestEntry entry in manifest.Tables)
            {
                string path = Path.Combine(directory, entry.File);
                if (!File.Exists(path))
                {
                    problems.Add($"{entry.File}: missing");
                    continue;
                }

                byte[] bytes = File.ReadAllBytes(path);
                if (bytes.Length != entry.Bytes || TableHash.Compute(bytes) != entry.Hash)
                {
                    problems.Add($"{entry.File}: does not match the manifest (modified, truncated, or from another build)");
                }
            }
        }

        if (console.Json)
        {
            console.Document(JsonValue.Object().Set("success", problems.Count == 0).Set("signature", signature)
                .Set("tables", manifest?.Tables.Count ?? 0).Set("problems", JsonValue.Array(problems)));
        }
        else
        {
            foreach (string problem in problems)
            {
                Console.WriteLine("  FAILED " + problem);
            }

            console.Info($"signature: {signature}");
            if (problems.Count == 0)
            {
                console.Success($"ok: {manifest!.Tables.Count} tables match the manifest (content {manifest.Content[..12]}…)");
            }
            else
            {
                console.Info($"failed: {problems.Count} problem(s).");
            }
        }

        return problems.Count == 0 ? Program.ExitSuccess : Program.ExitFailed;
    }

    public static int Keygen(Arguments arguments, Output console)
    {
        string path = Path.GetFullPath(arguments.Get("--out") ?? "ugs.private.key");
        if (File.Exists(path) && !arguments.Has("--force"))
        {
            console.Error($"'{path}' already exists. Replacing a key invalidates every client built with the old one. Use --force if that is intended.");
            return Program.ExitError;
        }

        (string privateKey, string publicKey) = SigningKeys.Generate();
        File.WriteAllText(path, privateKey);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        bool written = false;
        if (arguments.Has("--write-config") && File.Exists(arguments.ConfigPath))
        {
            UgsConfig config = UgsConfig.Load(arguments.ConfigPath);
            config.Signing.PublicKey = publicKey;
            config.Signing.PrivateKeyFile ??= Path.GetRelativePath(config.BaseDirectory, path).Replace('\\', '/');
            config.Save(arguments.ConfigPath);
            written = true;
        }

        if (console.Json)
        {
            console.Document(JsonValue.Object().Set("success", true).Set("privateKeyFile", path).Set("publicKey", publicKey).Set("configUpdated", written));
            return Program.ExitSuccess;
        }

        console.Info($"private key: {path}");
        console.Info("  Keep it secret and out of version control (add it to .gitignore). In CI, put its content in a secret");
        console.Info("  and name the environment variable in signing.privateKeyEnv.");
        console.Info($"public key:  {publicKey}");
        console.Info(written
            ? $"The public key was written to {arguments.ConfigPath}. The next build signs the manifest and bakes the key into the generated code."
            : "Put the public key in the config as signing.publicKey (or run again with --write-config).");
        return Program.ExitSuccess;
    }
}

/// <summary>A small workbook as CSV, so that 'ugs init --sample' gives something that builds.</summary>
internal static class Samples
{
    public static readonly (string Name, string Content)[] Files =
    [
        ("Enums.csv", "byte,,Flags<int>,\nEItemKind,,EItemTag,\nKind of item,,Tags an item can carry,\nNone,,None,0\nWeapon,,Tradable,1 << 0\nArmor,,Stackable,1 << 1\nConsumable,,Quest,1 << 2\n"),
        ("Item.csv", "Key<int>,string,Enum<EItemKind>,Enum<EItemTag>,int,float[],#note\nId,Name,Kind,Tags,Price,Stats,\nItem id,Display name,Kind,Tags,Price in gold,Stat values,Designer notes\n1001,Wooden Sword,Weapon,Tradable,100,3|1.5,starter\n1002,Leather Armor,Armor,Tradable,150,5,\n2001,Potion,Consumable,\"Tradable, Stackable\",25,,\n~9999,Unfinished,Weapon,,0,,this row is skipped\n"),
        ("Monster.csv", "Key<int>,Mapping<EMonsterId>,string,int,Ref<Item>[],Ref<Item>?\nId,Code,Name,Hp,Drops,Rare\nMonster id,Name in code,Display name,Hit points,Items it can drop,Rare drop\n1,Slime,Slime,30,2001,\n2,Goblin,Goblin,80,1001|2001,1002\n"),
        ("Game.Config.csv", "int,MaxLevel,Highest level a character can reach,60\nfloat,GoldRate,Multiplier on gold drops,1.5\nstring,Version,Data version label,sample\n")
    ];
}
