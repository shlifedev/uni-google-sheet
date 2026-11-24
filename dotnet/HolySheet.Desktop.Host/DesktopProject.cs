using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using HolySheet.Core;
using HolySheet.Core.Codecs;
using HolySheet.Core.Config;
using HolySheet.Core.Csv;
using HolySheet.Core.Output;
using HolySheet.Core.Sources;
using HolySheet.Tooling;

namespace HolySheet.Desktop.Host;

/// <summary>What one run did; kept until the next one so the tables can be previewed.</summary>
/// <param name="Committed">Whether the run was a build (as opposed to a check).</param>
public sealed record RunRecord(BuildResult Build, CommitReport? Commit, string? Failure, bool Committed, DateTimeOffset FinishedAt);

/// <summary>
/// The project the desktop app has open: a folder with a holysheet.json. Mirrors what the Unity window does through
/// HolySheetProject: one build at a time, with the last result kept for the table view.
/// </summary>
public sealed class DesktopProject
{
    private readonly Action<string, JsonNode?> _notify;
    private readonly object _gate = new();
    private string? _directory;
    private string _configName = HolySheetConfig.DefaultFileName;
    private RunRecord? _last;
    private string _sessionId = Guid.NewGuid().ToString("N");
    private string? _runningSession;
    private Task _running = Task.CompletedTask;

    public DesktopProject(Action<string, JsonNode?> notify)
    {
        _notify = notify;
    }

    public string? Directory => _directory;

    public string ConfigPath => Path.Combine(RequireDirectory(), _configName);

    public RunRecord? LastRun
    {
        get { lock (_gate) return _last; }
    }

    public static JsonObject AppInfo()
    {
        ICsvParser? fast = CsvBackends.Fast;
        return new JsonObject
        {
            ["tool"] = TableBuild.ToolName,
            ["runtime"] = RuntimeInformation.FrameworkDescription,
            ["os"] = RuntimeInformation.OSDescription,
            ["csvBackends"] = Json.Strings(fast != null ? [CsvBackends.Builtin, fast.Name] : [CsvBackends.Builtin]),
            ["csvDefault"] = CsvBackends.Resolve(CsvBackends.Auto).Name,
            ["sepAvailable"] = fast != null,
            ["sepUnavailableReason"] = fast == null ? CsvBackends.FastUnavailableReason : null,
            ["serializers"] = Json.Strings(TableCodecs.All.Select(codec => codec.Name))
        };
    }

    // ---- Project and config ----

    /// <summary>
    /// Opens a project: a folder (its holysheet.json, which may not exist yet) or a config file with another name
    /// (holysheet.memorypack.json), whose folder is then the project folder.
    /// </summary>
    public JsonObject Open(string path)
    {
        string full = Path.GetFullPath(path);
        string directory;
        string configName;
        if (File.Exists(full))
        {
            directory = Path.GetDirectoryName(full)!;
            configName = Path.GetFileName(full);
        }
        else if (System.IO.Directory.Exists(full))
        {
            directory = full;
            configName = HolySheetConfig.DefaultFileName;
        }
        else
        {
            throw new RpcException(RpcException.ToolError, $"'{full}' does not exist.");
        }

        return SelectProject(directory, configName);
    }

    public JsonObject Close() => SelectProject(null, HolySheetConfig.DefaultFileName);

    private JsonObject SelectProject(string? directory, string configName)
    {
        lock (_gate)
        {
            _directory = directory;
            _configName = configName;
            _sessionId = Guid.NewGuid().ToString("N");
            _last = null;
            return State();
        }
    }

    public JsonObject Init(string directory, bool sample, bool? unity)
    {
        ProjectInit.Create(directory, sample, unity);
        return Open(directory);
    }

    /// <summary>
    /// Where the project is and what its config says. The config goes out as the JSON that <c>holysheet.json</c> is saved as,
    /// so the app edits exactly what the file holds; a config that does not parse comes back as an error message instead.
    /// </summary>
    public JsonObject State()
    {
        string? directory, configPath;
        string sessionId;
        RunRecord? last;
        bool running;
        lock (_gate)
        {
            directory = _directory;
            configPath = directory == null ? null : Path.Combine(directory, _configName);
            sessionId = _sessionId;
            last = _last;
            running = IsRunning;
        }
        var state = new JsonObject
        {
            ["sessionId"] = sessionId,
            ["dir"] = directory,
            ["configPath"] = configPath,
            ["hasConfig"] = false,
            ["config"] = null,
            ["configError"] = null,
            ["unityProject"] = directory != null && ProjectInit.IsUnityProject(directory),
            ["sources"] = new JsonArray(),
            ["keyFileExists"] = false,
            ["keyIgnored"] = false,
            ["running"] = running,
            ["lastRun"] = last == null ? null : RunJson.Describe(last, sessionId)
        };
        if (directory == null || configPath == null || !File.Exists(configPath))
        {
            return state;
        }

        state["hasConfig"] = true;
        HolySheetConfig config;
        try
        {
            config = HolySheetConfig.Load(configPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonFormatException)
        {
            state["configError"] = exception.Message;
            return state;
        }

        state["canPreviewOffline"] = config.CanPreviewOffline();
        state["config"] = JsonNode.Parse(config.ToJson());
        state["sources"] = Json.Array(config.Sources.Select(source => (JsonNode?)SourceState(config, source)));
        string keyFile = config.Resolve(config.Signing.PrivateKeyFile ?? KeyPairWriter.DefaultFileName);
        state["keyFileExists"] = File.Exists(keyFile);
        state["keyIgnored"] = KeyPairWriter.IsIgnored(directory, Path.GetFileName(keyFile));
        return state;
    }

    // Whether a local source's file or folder is there, and a Google source's address.
    private static JsonObject SourceState(HolySheetConfig config, SourceConfig source)
    {
        string? resolved = source.Path is { Length: > 0 } path ? config.Resolve(path) : null;
        string? link = source.Type switch
        {
            SourceType.Google when !string.IsNullOrEmpty(source.SpreadsheetId) => HolySheet.Core.Sources.GoogleSheetsSource.SpreadsheetLink(source.SpreadsheetId),
            SourceType.GoogleFolder when !string.IsNullOrEmpty(source.FolderId) => HolySheet.Core.Sources.GoogleFolderSource.FolderLink(source.FolderId),
            _ => null
        };
        return new JsonObject
        {
            ["resolved"] = resolved,
            ["exists"] = resolved != null && (File.Exists(resolved) || System.IO.Directory.Exists(resolved)),
            ["link"] = link,
            ["snapshotResolved"] = source.Snapshot is { Length: > 0 } snapshot ? config.Resolve(snapshot) : null
        };
    }

    /// <summary>
    /// Saves the config the app edited. It is read back through the engine's own parser first, so anything the engine
    /// would refuse is refused here and the file is left as it was. Returns the new state plus the config's problems.
    /// </summary>
    public JsonObject SaveConfig(JsonObject edited)
    {
        string directory = RequireDirectory();
        HolySheetConfig config = HolySheetConfig.Parse(Json.WithoutNulls(edited).ToJsonString(), directory);
        var diagnostics = new DiagnosticBag();
        config.Validate(diagnostics, requireSources: false);
        config.Save(ConfigPath);
        JsonObject state = State();
        state["diagnostics"] = Json.Array(diagnostics.Items.Select(RunJson.Diagnostic));
        return state;
    }

    /// <summary>A path the user picked, as the config writes it: relative to the project with '/' when inside it.</summary>
    public JsonObject ToProjectPath(string path)
    {
        string directory = RequireDirectory();
        string full = Path.GetFullPath(path);
        string relative = Path.GetRelativePath(directory, full);
        bool inside = !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
        return new JsonObject { ["path"] = inside ? (relative == "." ? "." : relative.Replace('\\', '/')) : full.Replace('\\', '/'), ["inside"] = inside };
    }

    // ---- Runs ----

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _runningSession == _sessionId && !_running.IsCompleted;
            }
        }
    }

    /// <summary>
    /// Reads the sources and, with <paramref name="commit"/> and no errors, writes code and data. With
    /// <paramref name="only"/> ("Item.xlsx" or "Item.xlsx/Item.Rare"), reads just those again and takes the rest
    /// from the last build. Only one run per project session is accepted at a time. Runs across sessions are serialized.
    /// </summary>
    public Task<JsonObject> RunAsync(bool commit, bool offline, IReadOnlyList<string> only, string trigger, bool refreshGoogle = false,
        (string Workbook, string Sheet, bool UseLocal)? copy = null, string? expectedSession = null)
    {
        SheetSelection? selection = only.Count > 0 ? SheetSelection.Parse(only) : null;
        lock (_gate)
        {
            string sessionId = _sessionId;
            if (expectedSession != null && expectedSession != sessionId)
                throw new RpcException(RpcException.NoProject, "The active project changed before the run started.");
            string configPath = ConfigPath;
            if (IsRunning)
            {
                throw new RpcException(RpcException.Busy, "A build is already running.");
            }

            BuildResult? previous = copy.HasValue ? _last?.Build : null;
            if (copy.HasValue && previous == null) throw new RpcException(RpcException.ToolError, "Refresh the project before downloading a tab.");
            _notify("run.started", new JsonObject { ["sessionId"] = sessionId, ["commit"] = commit, ["offline"] = offline, ["only"] = Json.Strings(only), ["trigger"] = trigger });
            // Different projects can target the same output directory. Queue the new session behind its predecessor.
            var task = _running.ContinueWith(_ => Finish(Run(configPath, commit, offline, selection, refreshGoogle, copy, previous), trigger, sessionId),
                CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            _runningSession = sessionId;
            _running = task;
            return task;
        }
    }

    private static RunRecord Run(string configPath, bool commit, bool offline, SheetSelection? only, bool refreshGoogle,
        (string Workbook, string Sheet, bool UseLocal)? copy, BuildResult? previous)
    {
        try
        {
            HolySheetConfig config = HolySheetConfig.Load(configPath);
            using var validators = new ValidatorAssemblies();
            var options = new TableBuildOptions { LoadValidatorAssembly = validators.Load, Offline = offline, Only = only, RefreshGoogle = refreshGoogle, KeepGooglePreview = true };
            BuildResult build = copy is { } change && previous != null
                ? GoogleTabCopies.ChangeAsync(config, previous, change.Workbook, change.Sheet, change.UseLocal, options).GetAwaiter().GetResult()
                : TableBuild.Run(config, options);
            CommitReport? report = commit && build.Success ? TableBuild.Commit(config, build) : null;
            // The table view only needs the tables as read and the problems; the typed values and generated bytes go.
            build.ReleaseBuildData();
            return new RunRecord(build, report, null, commit, DateTimeOffset.Now);
        }
        catch (Exception exception)
        {
            // A desktop app has no console to throw into: whatever stopped the run is shown as its failure.
            string message = exception is IOException or UnauthorizedAccessException or JsonFormatException or InvalidOperationException
                ? exception.Message
                : exception.GetType().Name + ": " + exception.Message;
            return new RunRecord(previous ?? new BuildResult(new DiagnosticBag()), null, message, commit, DateTimeOffset.Now);
        }
    }

    private JsonObject Finish(RunRecord record, string trigger, string sessionId)
    {
        lock (_gate)
        {
            if (_sessionId == sessionId) _last = record;
        }

        JsonObject result = RunJson.Describe(record, sessionId);
        result["trigger"] = trigger;
        _notify("run.finished", result.DeepClone());
        return result;
    }

    // ---- Tables ----

    public JsonObject TableModel() => LastRun is { } last ? TableView.Model(last.Build) : TableView.Empty();

    public JsonObject Preview(string table, string? sheet)
    {
        RunRecord last = LastRun ?? throw new RpcException(RpcException.ToolError, "Nothing has been read yet. Run a check first.");
        return TableView.Preview(last.Build, table, sheet);
    }

    public JsonObject LocateSource(string workbook, string? sheet) =>
        SourceLocator.Result(SourceLocator.FindSourceFile(LoadConfigOrNull(), _last?.Build.WorkbookOrigins, workbook, sheet));

    public JsonObject LocateProblem(string location, string? workbook, string? sheet)
    {
        if (location is "config" or "holysheet" or "signing")
        {
            return SourceLocator.Result(ConfigPath);
        }

        (string book, string? page) = SourceLocator.SplitProblem(location, workbook, sheet);
        return LocateSource(book, page);
    }

    // ---- Signing ----

    /// <summary>Creates holysheet.private.key next to the config and turns signing on. Refuses to replace an existing key.</summary>
    public JsonObject CreateKeyPair()
    {
        HolySheetConfig config = HolySheetConfig.Load(ConfigPath);
        string path = KeyPairWriter.CreateForConfig(config);
        config.Save(ConfigPath);
        JsonObject state = State();
        state["privateKeyFile"] = path;
        return state;
    }

    public JsonObject IgnoreKey()
    {
        HolySheetConfig config = HolySheetConfig.Load(ConfigPath);
        KeyPairWriter.AddToGitignore(RequireDirectory(), Path.GetFileName(config.Signing.PrivateKeyFile ?? KeyPairWriter.DefaultFileName));
        return State();
    }

    /// <summary>Turns signing off: the next build writes an unsigned manifest. The key file is left where it is.</summary>
    public JsonObject StopSigning()
    {
        HolySheetConfig config = HolySheetConfig.Load(ConfigPath);
        config.Signing.PublicKey = null;
        config.Signing.PrivateKeyFile = null;
        config.Signing.PrivateKeyEnv = null;
        config.Save(ConfigPath);
        return State();
    }

    /// <summary>Checks a data folder (the config's output folder by default) against its manifest and signature.</summary>
    public JsonObject Verify(string? dataDirectory)
    {
        HolySheetConfig config = HolySheetConfig.Load(ConfigPath);
        string directory = string.IsNullOrEmpty(dataDirectory) ? config.Resolve(config.Output.Data) : config.Resolve(dataDirectory);
        VerifyReport report = DataVerifier.Verify(directory, config.Signing.PublicKey, config.Output.ManifestFile);
        return new JsonObject
        {
            ["success"] = report.Success,
            ["directory"] = report.Directory,
            ["signature"] = report.Signature,
            ["tables"] = report.Tables,
            ["content"] = report.Content,
            ["problems"] = Json.Strings(report.Problems)
        };
    }

    // ---- Helpers ----

    private string RequireDirectory() => _directory ?? throw new RpcException(RpcException.NoProject, "No project folder is open.");

    private HolySheetConfig? LoadConfigOrNull()
    {
        try
        {
            return _directory != null && File.Exists(ConfigPath) ? HolySheetConfig.Load(ConfigPath) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonFormatException)
        {
            return null;
        }
    }
}
