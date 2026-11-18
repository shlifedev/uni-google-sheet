#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using UGS.Core;
using UGS.Core.Config;
using UGS.Core.Csv;
using UGS.Core.Output;
using UGS.Core.Transform;
using UnityEditor;
using UnityEngine;

namespace UGS.Editor
{
    /// <summary>What one run from the editor did.</summary>
    public sealed class UgsRunResult
    {
        public BuildResult Build { get; }

        /// <summary>Null when the run only checked, or when the build had errors.</summary>
        public CommitReport? Commit { get; }

        /// <summary>Set when the run itself failed (unreadable config, cannot write), as opposed to the sheets having errors.</summary>
        public string? Failure { get; }

        public bool WroteFiles => Commit != null && Commit.HasChanges;

        public UgsRunResult(BuildResult build, CommitReport? commit, string? failure)
        {
            Build = build;
            Commit = commit;
            Failure = failure;
        }
    }

    /// <summary>
    /// The project's tables, as the editor sees them: where the config is, how to build, and what the last build said.
    /// The config is <c>ugs.json</c> in the project root, the same file the <c>ugs</c> command line reads, so the editor
    /// and a CI job build exactly the same output.
    /// </summary>
    public static class UgsProject
    {
        private const string AutoBuildKey = "UGS.AutoBuild";

        private static SourceWatcher? _watcher;
        private static int _sourcesChanged;
        private static Task<UgsRunResult>? _running;

        /// <summary>The project folder: the one that contains Assets.</summary>
        public static string RootDirectory => Path.GetDirectoryName(Application.dataPath)!;

        public static string ConfigPath => Path.Combine(RootDirectory, UgsConfig.DefaultFileName);

        public static bool HasConfig => File.Exists(ConfigPath);

        /// <summary>The result of the most recent run in this editor session.</summary>
        public static UgsRunResult? LastResult { get; private set; }

        public static bool IsRunning => _running != null && !_running.IsCompleted;

        /// <summary>Raised on the main thread when a run starts.</summary>
        public static event Action? RunStarted;

        /// <summary>Raised on the main thread when a run ends.</summary>
        public static event Action<UgsRunResult>? RunFinished;

        /// <summary>
        /// Supplies the transform rules for every build started from the editor. Assign it from an
        /// <c>[InitializeOnLoadMethod]</c> to add project-specific sheet conventions; null uses the default rules.
        /// </summary>
        public static Func<TransformPipeline>? CreatePipeline { get; set; }

        /// <summary>Rebuild automatically when a local source file changes. Stored per machine, not in the config.</summary>
        public static bool AutoBuild
        {
            get => EditorPrefs.GetBool(AutoBuildKey + Application.dataPath, false);
            set
            {
                EditorPrefs.SetBool(AutoBuildKey + Application.dataPath, value);
                RestartWatcher();
            }
        }

        /// <summary>The CSV parser this project's builds use on this editor.</summary>
        public static string DescribeCsvBackend(UgsConfig? config = null)
        {
            string requested = (config?.CsvBackend ?? CsvBackends.Auto).Trim().ToLowerInvariant();
            if (requested == CsvBackends.Sep)
            {
                return CsvBackends.Fast != null ? "sep" : "sep (not available on this editor)";
            }

            return "builtin";
        }

        /// <summary>True when the Sep backend can run in this editor: it needs .NET 8 or newer, which Unity 7 has.</summary>
        public static bool IsSepAvailable => CsvBackends.Fast != null;

        public static string DescribeRuntime() => RuntimeInformation.FrameworkDescription;

        public static UgsConfig LoadConfig() => UgsConfig.Load(ConfigPath);

        /// <summary>Creates <c>ugs.json</c> with paths that suit a Unity project, plus the sheet folder it points at.</summary>
        public static UgsConfig CreateDefaultConfig()
        {
            var config = new UgsConfig { BaseDirectory = RootDirectory };
            config.Sources.Add(new SourceConfig { Type = SourceType.Xlsx, Path = "Sheets" });
            // Generated code is compiled by Unity. Data goes to StreamingAssets: plain files a watcher can follow and a patch can replace.
            config.Output.Code = "Assets/Game/Generated/Tables";
            config.Output.Data = "Assets/StreamingAssets/Tables";
            Directory.CreateDirectory(Path.Combine(RootDirectory, "Sheets"));
            config.Save(ConfigPath);
            return config;
        }

        public static void SaveConfig(UgsConfig config)
        {
            config.Save(ConfigPath);
            RestartWatcher();
        }

        /// <summary>
        /// Reads the sources and, when <paramref name="commit"/> is true and there are no errors, writes code and data.
        /// With <paramref name="only"/>, reads just those workbooks and sheets again and takes the rest from the last build
        /// (see <see cref="SheetCache"/>).
        /// The work runs on a background thread; the returned task completes on it, while the events are raised on the main thread.
        /// </summary>
        public static Task<UgsRunResult> RunAsync(bool commit, bool offline = false, SheetSelection? only = null)
        {
            if (_running != null && !_running.IsCompleted)
            {
                return _running;
            }

            RunStarted?.Invoke();
            TransformPipeline? pipeline = CreatePipeline?.Invoke();
            // Unity's own properties may only be read on the main thread, so the path is resolved before the work moves off it.
            string configPath = ConfigPath;
            Task<UgsRunResult> task = Task.Run(() => Run(configPath, commit, offline, pipeline, only));
            _running = task;
            EditorApplication.update += Poll;
            return task;

            void Poll()
            {
                if (!task.IsCompleted)
                {
                    return;
                }

                EditorApplication.update -= Poll;
                UgsRunResult result = task.Status == TaskStatus.RanToCompletion
                    ? task.Result
                    : new UgsRunResult(new BuildResult(new DiagnosticBag()), null, task.Exception?.GetBaseException().Message ?? "The build was cancelled.");
                Finish(result);
            }
        }

        /// <summary>Runs on the calling thread and returns when done. For batch mode and tests.</summary>
        public static UgsRunResult RunBlocking(bool commit, bool offline = false, SheetSelection? only = null)
        {
            UgsRunResult result = Run(ConfigPath, commit, offline, CreatePipeline?.Invoke(), only);
            Finish(result);
            return result;
        }

        private static UgsRunResult Run(string configPath, bool commit, bool offline, TransformPipeline? pipeline, SheetSelection? only)
        {
            try
            {
                UgsConfig config = UgsConfig.Load(configPath);
                BuildResult build = TableBuild.Run(config, new TableBuildOptions { Offline = offline, Pipeline = pipeline, Only = only });
                CommitReport? report = commit && build.Success ? TableBuild.Commit(config, build) : null;
                // The result is kept for the window, which only shows the tables as read and the problems.
                // The typed values and generated bytes are several times larger and are not needed once written.
                build.ReleaseBuildData();
                return new UgsRunResult(build, report, null);
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is JsonFormatException)
            {
                return new UgsRunResult(new BuildResult(new DiagnosticBag()), null, exception.Message);
            }
        }

        private static void Finish(UgsRunResult result)
        {
            LastResult = result;
            if (result.WroteFiles)
            {
                // New code has to be compiled and new data imported before the game can use them.
                AssetDatabase.Refresh();
            }

            RunFinished?.Invoke(result);
        }

        /// <summary>One line describing a result, for the status bar and the console.</summary>
        public static string Summarize(UgsRunResult result)
        {
            if (result.Failure != null)
            {
                return "Failed: " + result.Failure;
            }

            BuildResult build = result.Build;
            if (!build.Success)
            {
                return build.Diagnostics.ErrorCount + " error(s), " + build.Diagnostics.WarningCount + " warning(s). Nothing was written.";
            }

            string summary = build.TableCount + " tables, " + build.RowCount + " rows, " + build.Enums.Count + " enums";
            if (build.Only != null)
            {
                summary += " (partial: " + build.RefreshedSheets.Count + " sheet" + (build.RefreshedSheets.Count == 1 ? "" : "s") + " read again)";
            }

            string timing = " in " + (build.ReadTime + build.BuildTime).TotalMilliseconds.ToString("F0") + " ms (csv " + build.CsvBackend + ")";
            if (result.Commit == null)
            {
                return "Checked " + summary + timing + ". No problems that stop a build.";
            }

            return result.Commit.HasChanges
                ? "Built " + summary + timing + ": " + result.Commit.Written.Count + " written, " + result.Commit.Deleted.Count + " deleted."
                : "Up to date: " + summary + timing + ".";
        }

        [InitializeOnLoadMethod]
        private static void Initialize()
        {
            EditorApplication.update += OnUpdate;
            AssemblyReloadEvents.beforeAssemblyReload += StopWatcher;
            EditorApplication.quitting += StopWatcher;
            RestartWatcher();
        }

        private static void OnUpdate()
        {
            // The watcher fires on a timer thread; the build is started from here, on the main thread.
            if (Interlocked.Exchange(ref _sourcesChanged, 0) == 1 && !IsRunning && !EditorApplication.isCompiling)
            {
                RunAsync(commit: true);
            }
        }

        private static void RestartWatcher()
        {
            StopWatcher();
            if (!HasConfig || !EditorPrefs.GetBool(AutoBuildKey + Application.dataPath, false))
            {
                return;
            }

            try
            {
                UgsConfig config = LoadConfig();
                List<string> paths = config.Sources.Where(source => source.Path != null).Select(source => config.Resolve(source.Path!)).ToList();
                if (paths.Count > 0)
                {
                    _watcher = new SourceWatcher(paths, TimeSpan.FromMilliseconds(400), () => Interlocked.Exchange(ref _sourcesChanged, 1));
                }
            }
            catch (Exception exception) when (exception is IOException || exception is JsonFormatException || exception is ArgumentException)
            {
                Debug.LogWarning("[UGS] Auto build is off because the config could not be read: " + exception.Message);
            }
        }

        private static void StopWatcher()
        {
            _watcher?.Dispose();
            _watcher = null;
        }

        [MenuItem("Tools/UGS/Build Tables %#&b", priority = 11)]
        private static void BuildMenu()
        {
            if (!EnsureConfig())
            {
                return;
            }

            RunAsync(commit: true).ContinueWith(_ => { });
            LogWhenFinished();
        }

        [MenuItem("Tools/UGS/Check Tables", priority = 12)]
        private static void CheckMenu()
        {
            if (!EnsureConfig())
            {
                return;
            }

            RunAsync(commit: false);
            LogWhenFinished();
        }

        private static bool EnsureConfig()
        {
            if (HasConfig)
            {
                return true;
            }

            Debug.LogWarning("[UGS] There is no ugs.json in the project root yet. Open Tools > UGS > Tables to create one.");
            return false;
        }

        private static void LogWhenFinished()
        {
            void OnFinished(UgsRunResult result)
            {
                RunFinished -= OnFinished;
                LogResult(result);
            }

            RunFinished += OnFinished;
        }

        /// <summary>Writes a result to the console: one line per problem, then the summary.</summary>
        public static void LogResult(UgsRunResult result)
        {
            foreach (Diagnostic diagnostic in result.Build.Diagnostics.Items)
            {
                string text = "[UGS] " + diagnostic.Location + ": " + diagnostic.Message + (diagnostic.Hint.Length > 0 ? "\nFix: " + diagnostic.Hint : "");
                if (diagnostic.Severity == Severity.Error)
                {
                    Debug.LogError(text);
                }
                else
                {
                    Debug.LogWarning(text);
                }
            }

            string summary = "[UGS] " + Summarize(result);
            if (result.Failure != null || !result.Build.Success)
            {
                Debug.LogError(summary);
            }
            else
            {
                Debug.Log(summary);
            }
        }
    }

    /// <summary>
    /// Entry points for batch mode, where no window exists:
    /// <code>Unity -batchmode -quit -projectPath . -executeMethod UGS.Editor.UgsCommandLine.Build</code>
    /// The editor exits with 0 on success, 1 when the sheets have errors and 3 when the tool could not run.
    /// Add <c>-ugsOffline</c> to build from the saved Google snapshot instead of the network.
    /// </summary>
    public static class UgsCommandLine
    {
        public static void Build() => Run(commit: true);

        public static void Check() => Run(commit: false);

        private static void Run(bool commit)
        {
            bool offline = Environment.GetCommandLineArgs().Contains("-ugsOffline");
            UgsRunResult result = UgsProject.RunBlocking(commit, offline);
            UgsProject.LogResult(result);
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(result.Failure != null ? 3 : result.Build.Success ? 0 : 1);
            }
        }
    }
}
