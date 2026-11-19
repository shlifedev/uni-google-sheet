using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using UGS.Core;
using UGS.Core.Codecs;
using UGS.Core.Config;
using UGS.Core.Csv;
using UGS.Core.Output;
using UGS.Tooling;

namespace UGS.Desktop.Host;

/// <summary>What one run did; kept until the next one so the tables can be previewed.</summary>
/// <param name="Committed">Whether the run was a build (as opposed to a check).</param>
public sealed record RunRecord(BuildResult Build, CommitReport? Commit, string? Failure, bool Committed, DateTimeOffset FinishedAt);

/// <summary>
/// The project the desktop app has open: a folder with a ugs.json. Mirrors what the Unity window does through
/// UgsProject, without Unity: one build at a time, the last result kept for the table view, auto build on file changes.
/// </summary>
public sealed class DesktopProject : IDisposable
{
    private readonly Action<string, JsonNode?> _notify;
    private readonly object _gate = new();
    private string? _directory;
    private string _configName = UgsConfig.DefaultFileName;
    private RunRecord? _last;
    private Task<JsonObject>? _running;

    // Auto build: a watcher on the local sources and, when asked, a timer that re-reads the Google sources.
    private SourceWatcher? _watcher;
    private CancellationTokenSource? _polling;
    private bool _watchEnabled;
    private bool _watchOffline;
    private int _watchInterval;
    private int _changedWhileRunning;

    public DesktopProject(Action<string, JsonNode?> notify)
    {
        _notify = notify;
    }

    public string? Directory => _directory;

    public string ConfigPath => Path.Combine(RequireDirectory(), _configName);

    public RunRecord? LastRun => _last;

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
    /// Opens a project: a folder (its ugs.json, which may not exist yet) or a config file with another name
    /// (ugs.memorypack.json), whose folder is then the project folder.
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
            configName = UgsConfig.DefaultFileName;
        }
        else
        {
            throw new RpcException(RpcException.ToolError, $"'{full}' does not exist.");
        }

        lock (_gate)
        {
            _directory = directory;
            _configName = configName;
            _last = null;
        }

        RestartWatcher();
        return State();
    }

    public JsonObject Close()
    {
        lock (_gate)
        {
            _directory = null;
            _configName = UgsConfig.DefaultFileName;
            _last = null;
        }

        StopWatcher();
        return State();
    }

    public JsonObject Init(string directory, bool sample, bool? unity)
    {
        ProjectInit.Create(directory, sample, unity);
        return Open(directory);
    }

    /// <summary>
    /// Where the project is and what its config says. The config goes out as the JSON that <c>ugs.json</c> is saved as,
    /// so the app edits exactly what the file holds; a config that does not parse comes back as an error message instead.
    /// </summary>
    public JsonObject State()
    {
        string? directory = _directory;
        string? configPath = directory == null ? null : Path.Combine(directory, _configName);
        var state = new JsonObject
        {
            ["dir"] = directory,
            ["configPath"] = configPath,
            ["hasConfig"] = false,
            ["config"] = null,
            ["configError"] = null,
            ["unityProject"] = directory != null && ProjectInit.IsUnityProject(directory),
            ["sources"] = new JsonArray(),
            ["keyFileExists"] = false,
            ["keyIgnored"] = false,
            ["watch"] = new JsonObject { ["enabled"] = _watchEnabled, ["offline"] = _watchOffline, ["intervalSec"] = _watchInterval, ["watching"] = _watcher?.WatchedCount ?? 0 },
            ["running"] = IsRunning,
            ["lastRun"] = _last == null ? null : RunJson.Describe(_last)
        };
        if (directory == null || configPath == null || !File.Exists(configPath))
        {
            return state;
        }

        state["hasConfig"] = true;
        UgsConfig config;
        try
        {
            config = UgsConfig.Load(configPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonFormatException)
        {
            state["configError"] = exception.Message;
            return state;
        }

        state["config"] = JsonNode.Parse(config.ToJson());
        state["sources"] = Json.Array(config.Sources.Select(source => (JsonNode?)SourceState(config, source)));
        string keyFile = config.Resolve(config.Signing.PrivateKeyFile ?? KeyPairWriter.DefaultFileName);
        state["keyFileExists"] = File.Exists(keyFile);
        state["keyIgnored"] = KeyPairWriter.IsIgnored(directory, Path.GetFileName(keyFile));
        return state;
    }

    // Whether a local source's file or folder is there, and a Google source's address.
    private static JsonObject SourceState(UgsConfig config, SourceConfig source)
    {
        string? resolved = source.Path is { Length: > 0 } path ? config.Resolve(path) : null;
        string? link = source.Type switch
        {
            SourceType.Google when !string.IsNullOrEmpty(source.SpreadsheetId) => UGS.Core.Sources.GoogleSheetsSource.SpreadsheetLink(source.SpreadsheetId),
            SourceType.GoogleFolder when !string.IsNullOrEmpty(source.FolderId) => UGS.Core.Sources.GoogleFolderSource.FolderLink(source.FolderId),
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
        UgsConfig config = UgsConfig.Parse(Json.WithoutNulls(edited).ToJsonString(), directory);
        var diagnostics = new DiagnosticBag();
        config.Validate(diagnostics, requireSources: false);
        config.Save(ConfigPath);
        RestartWatcher();
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
                return _running is { IsCompleted: false };
            }
        }
    }

    /// <summary>
    /// Reads the sources and, with <paramref name="commit"/> and no errors, writes code and data. With
    /// <paramref name="only"/> ("Item.xlsx" or "Item.xlsx/Item.Rare"), reads just those again and takes the rest
    /// from the last build. Refuses while another run is going: two builds must never write the same folder.
    /// </summary>
    public Task<JsonObject> RunAsync(bool commit, bool offline, IReadOnlyList<string> only, string trigger)
    {
        string configPath = ConfigPath;
        SheetSelection? selection = only.Count > 0 ? SheetSelection.Parse(only) : null;
        Task<JsonObject> task;
        lock (_gate)
        {
            if (_running is { IsCompleted: false })
            {
                throw new RpcException(RpcException.Busy, "A build is already running.");
            }

            _notify("run.started", new JsonObject { ["commit"] = commit, ["offline"] = offline, ["only"] = Json.Strings(only), ["trigger"] = trigger });
            task = Task.Run(() => Finish(Run(configPath, commit, offline, selection), trigger));
            _running = task;
        }

        return task;
    }

    private static RunRecord Run(string configPath, bool commit, bool offline, SheetSelection? only)
    {
        try
        {
            UgsConfig config = UgsConfig.Load(configPath);
            BuildResult build = TableBuild.Run(config, new TableBuildOptions { Offline = offline, Only = only });
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
            return new RunRecord(new BuildResult(new DiagnosticBag()), null, message, commit, DateTimeOffset.Now);
        }
    }

    private JsonObject Finish(RunRecord record, string trigger)
    {
        lock (_gate)
        {
            _last = record;
        }

        JsonObject result = RunJson.Describe(record);
        result["trigger"] = trigger;
        _notify("run.finished", result.DeepClone());
        // A save during the build is built as soon as it ends, once, however many saves there were.
        if (Interlocked.Exchange(ref _changedWhileRunning, 0) == 1 && _watchEnabled)
        {
            _ = Task.Run(async () =>
            {
                // This method runs inside the run's task; wait until that task has completed so the new run is not refused.
                Task? current;
                lock (_gate)
                {
                    current = _running;
                }

                if (current != null)
                {
                    await current.ContinueWith(_ => { }, TaskScheduler.Default).ConfigureAwait(false);
                }

                AutoBuild();
            });
        }

        return result;
    }

    // ---- Tables ----

    public JsonObject TableModel() => _last == null ? TableView.Empty() : TableView.Model(_last.Build);

    public JsonObject Preview(string table, string? sheet)
    {
        RunRecord last = _last ?? throw new RpcException(RpcException.ToolError, "Nothing has been read yet. Run a check first.");
        return TableView.Preview(last.Build, table, sheet);
    }

    public JsonObject LocateSource(string workbook, string? sheet) =>
        SourceLocator.Result(SourceLocator.FindSourceFile(LoadConfigOrNull(), _last?.Build.WorkbookOrigins, workbook, sheet));

    public JsonObject LocateProblem(string location, string? workbook, string? sheet)
    {
        if (location is "config" or "ugs" or "signing")
        {
            return SourceLocator.Result(ConfigPath);
        }

        (string book, string? page) = SourceLocator.SplitProblem(location, workbook, sheet);
        return LocateSource(book, page);
    }

    // ---- Auto build ----

    public JsonObject SetWatch(bool enabled, bool offline, int intervalSeconds)
    {
        _watchEnabled = enabled;
        _watchOffline = offline;
        _watchInterval = Math.Max(0, intervalSeconds);
        RestartWatcher();
        return State();
    }

    private void RestartWatcher()
    {
        StopWatcher();
        if (!_watchEnabled || _directory == null)
        {
            return;
        }

        UgsConfig? config = LoadConfigOrNull();
        if (config == null)
        {
            return;
        }

        // Local files wake the build up; offline, the saved snapshots of the Google sources do too.
        List<string> paths = config.Sources.Where(source => source.Path != null).Select(source => config.Resolve(source.Path!))
            .Concat(_watchOffline ? config.Sources.Where(source => source.Snapshot != null).Select(source => config.Resolve(source.Snapshot!)) : [])
            .ToList();
        if (paths.Count > 0)
        {
            _watcher = new SourceWatcher(paths, TimeSpan.FromMilliseconds(400), AutoBuild);
        }

        // Google has no change notification, so its sources are read again on a timer, when the user set one.
        if (_watchInterval > 0 && !_watchOffline && config.Sources.Any(source => source.IsRemote))
        {
            var polling = new CancellationTokenSource();
            _polling = polling;
            TimeSpan interval = TimeSpan.FromSeconds(_watchInterval);
            _ = Task.Run(async () =>
            {
                while (!polling.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(interval, polling.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    AutoBuild();
                }
            });
        }
    }

    private void AutoBuild()
    {
        if (!_watchEnabled || _directory == null)
        {
            return;
        }

        try
        {
            _ = RunAsync(commit: true, _watchOffline, [], "watch");
        }
        catch (RpcException exception) when (exception.Code == RpcException.Busy)
        {
            Interlocked.Exchange(ref _changedWhileRunning, 1);
        }
    }

    private void StopWatcher()
    {
        _watcher?.Dispose();
        _watcher = null;
        _polling?.Cancel();
        _polling?.Dispose();
        _polling = null;
    }

    // ---- Signing ----

    /// <summary>Creates ugs.private.key next to the config and turns signing on. Refuses to replace an existing key.</summary>
    public JsonObject CreateKeyPair()
    {
        UgsConfig config = UgsConfig.Load(ConfigPath);
        string path = KeyPairWriter.CreateForConfig(config);
        config.Save(ConfigPath);
        RestartWatcher();
        JsonObject state = State();
        state["privateKeyFile"] = path;
        return state;
    }

    public JsonObject IgnoreKey()
    {
        UgsConfig config = UgsConfig.Load(ConfigPath);
        KeyPairWriter.AddToGitignore(RequireDirectory(), Path.GetFileName(config.Signing.PrivateKeyFile ?? KeyPairWriter.DefaultFileName));
        return State();
    }

    /// <summary>Turns signing off: the next build writes an unsigned manifest. The key file is left where it is.</summary>
    public JsonObject StopSigning()
    {
        UgsConfig config = UgsConfig.Load(ConfigPath);
        config.Signing.PublicKey = null;
        config.Signing.PrivateKeyFile = null;
        config.Signing.PrivateKeyEnv = null;
        config.Save(ConfigPath);
        return State();
    }

    /// <summary>Checks a data folder (the config's output folder by default) against its manifest and signature.</summary>
    public JsonObject Verify(string? dataDirectory)
    {
        UgsConfig config = UgsConfig.Load(ConfigPath);
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

    private UgsConfig? LoadConfigOrNull()
    {
        try
        {
            return _directory != null && File.Exists(ConfigPath) ? UgsConfig.Load(ConfigPath) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonFormatException)
        {
            return null;
        }
    }

    public void Dispose() => StopWatcher();
}
