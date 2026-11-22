#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using HolySheet.Core;
using HolySheet.Core.Config;
using HolySheet.Core.Csv;
using HolySheet.Core.Output;
using HolySheet.Core.Sources;
using HolySheet.Core.Transform;
using UnityEditor;
using UnityEngine;

namespace HolySheet.Editor
{
    /// <summary>What one run from the editor did.</summary>
    public sealed class HolySheetRunResult
    {
        public BuildResult Build { get; }

        /// <summary>Null when the run only checked, or when the build had errors.</summary>
        public CommitReport? Commit { get; }

        /// <summary>Set when the run itself failed (unreadable config, cannot write), as opposed to the sheets having errors.</summary>
        public string? Failure { get; }

        public bool WroteFiles => Commit != null && Commit.HasChanges;

        public HolySheetRunResult(BuildResult build, CommitReport? commit, string? failure)
        {
            Build = build;
            Commit = commit;
            Failure = failure;
        }
    }

    /// <summary>
    /// The project's tables, as the editor sees them: where the config is, how to build, and what the last build said.
    /// The config is <c>holysheet.json</c> in the project root, the same file the <c>holysheet</c> command line reads, so the editor
    /// and a CI job build exactly the same output.
    /// </summary>
    public static class HolySheetProject
    {
        private static Task<HolySheetRunResult>? _running;

        /// <summary>The project folder: the one that contains Assets.</summary>
        public static string RootDirectory => Path.GetDirectoryName(Application.dataPath)!;

        public static string ConfigPath => Path.Combine(RootDirectory, HolySheetConfig.DefaultFileName);

        public static bool HasConfig => File.Exists(ConfigPath);

        /// <summary>The result of the most recent run in this editor session.</summary>
        public static HolySheetRunResult? LastResult { get; private set; }

        public static bool IsRunning => _running != null && !_running.IsCompleted;

        /// <summary>Raised on the main thread when a run starts.</summary>
        public static event Action? RunStarted;

        /// <summary>Raised on the main thread when a run ends.</summary>
        public static event Action<HolySheetRunResult>? RunFinished;

        /// <summary>
        /// Supplies the transform rules for every build started from the editor. Assign it from an
        /// <c>[InitializeOnLoadMethod]</c> to add project-specific sheet conventions; null uses the default rules.
        /// </summary>
        public static Func<TransformPipeline>? CreatePipeline { get; set; }

        /// <summary>
        /// The names of the credential providers in the editor's assemblies, for sources with auth type "custom": every
        /// non-abstract class with a public parameterless constructor that implements <see cref="IGoogleCredentialProvider"/>.
        /// The name is the class name, or the one in <see cref="HolySheetCredentialProviderAttribute"/>.
        /// </summary>
        public static IReadOnlyList<string> CredentialProviderNames() =>
            ProviderTypes().Where(GoogleCredentialProviders.IsProvider).Select(GoogleCredentialProviders.NameOf).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name, StringComparer.Ordinal).ToList();

        // Builds make new instances each time, so a provider never outlives a domain reload and edits to it apply at once.
        private static IReadOnlyList<IGoogleCredentialProvider> CreateCredentialProviders()
        {
            var problems = new List<string>();
            IReadOnlyList<IGoogleCredentialProvider> providers = GoogleCredentialProviders.Create(ProviderTypes(), problems);
            foreach (string problem in problems)
            {
                Debug.LogWarning("[HolySheet] " + problem);
            }

            return providers;
        }

        // Classes marked with the attribute are included so that one which cannot be used is reported instead of ignored.
        private static IEnumerable<Type> ProviderTypes() =>
            TypeCache.GetTypesDerivedFrom<IGoogleCredentialProvider>().Concat(TypeCache.GetTypesWithAttribute<HolySheetCredentialProviderAttribute>());

        /// <summary>The CSV parser this project's builds use on this editor.</summary>
        public static string DescribeCsvBackend(HolySheetConfig? config = null)
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

        public static HolySheetConfig LoadConfig() => HolySheetConfig.Load(ConfigPath);

        /// <summary>Creates <c>holysheet.json</c> with paths that suit a Unity project, plus the sheet folder it points at.</summary>
        public static HolySheetConfig CreateDefaultConfig()
        {
            var config = new HolySheetConfig { BaseDirectory = RootDirectory };
            config.Sources.Add(new SourceConfig { Type = SourceType.Xlsx, Path = "Sheets" });
            // Generated code is compiled by Unity. Data goes to StreamingAssets: plain files a watcher can follow and a patch can replace.
            config.Output.Code = "Assets/Game/Generated/Tables";
            config.Output.Data = "Assets/StreamingAssets/Tables";
            Directory.CreateDirectory(Path.Combine(RootDirectory, "Sheets"));
            config.Save(ConfigPath);
            return config;
        }

        public static void SaveConfig(HolySheetConfig config) => config.Save(ConfigPath);

        /// <summary>
        /// Reads the sources and, when <paramref name="commit"/> is true and there are no errors, writes code and data.
        /// With <paramref name="only"/>, reads just those workbooks and sheets again and takes the rest from the last build
        /// (see <see cref="SheetCache"/>).
        /// The work runs on a background thread; the returned task completes on it, while the events are raised on the main thread.
        /// </summary>
        public static Task<HolySheetRunResult> RunAsync(bool commit, bool offline = false, SheetSelection? only = null)
            => StartRun(commit, offline, only, refreshGoogle: false);

        internal static Task<HolySheetRunResult> RefreshAsync(bool offline)
            => StartRun(commit: false, offline: offline, only: null, refreshGoogle: true);

        private static Task<HolySheetRunResult> StartRun(bool commit, bool offline, SheetSelection? only, bool refreshGoogle)
        {
            if (_running != null && !_running.IsCompleted)
            {
                return _running;
            }

            TransformPipeline? pipeline = CreatePipeline?.Invoke();
            // Unity's own properties may only be read on the main thread, so the path is resolved before the work moves off it.
            string configPath = ConfigPath;
            IReadOnlyList<IGoogleCredentialProvider> providers = CreateCredentialProviders();
            Type[] validators = DiscoverValidators();
            Task<HolySheetRunResult> task = Task.Run(() => Run(configPath, commit, offline, pipeline, only, providers, validators, refreshGoogle));
            _running = task;
            EditorApplication.update += Poll;
            RunStarted?.Invoke();
            return task;

            void Poll()
            {
                if (!task.IsCompleted)
                {
                    return;
                }

                EditorApplication.update -= Poll;
                HolySheetRunResult result = task.Status == TaskStatus.RanToCompletion
                    ? task.Result
                    : new HolySheetRunResult(new BuildResult(new DiagnosticBag()), null, task.Exception?.GetBaseException().Message ?? "The build was cancelled.");
                Finish(result);
            }
        }

        /// <summary>Runs on the calling thread and returns when done. For batch mode and tests.</summary>
        public static HolySheetRunResult RunBlocking(bool commit, bool offline = false, SheetSelection? only = null)
        {
            HolySheetRunResult result = Run(ConfigPath, commit, offline, CreatePipeline?.Invoke(), only, CreateCredentialProviders(), DiscoverValidators(), refreshGoogle: false);
            Finish(result);
            return result;
        }

        private static Type[] DiscoverValidators() => TypeCache.GetTypesDerivedFrom<ITableValidator>()
            .Where(type => type.IsVisible && type.IsClass && !type.IsAbstract && !type.ContainsGenericParameters).ToArray();

        private static HolySheetRunResult Run(string configPath, bool commit, bool offline, TransformPipeline? pipeline, SheetSelection? only, IReadOnlyList<IGoogleCredentialProvider> providers, Type[] validators, bool refreshGoogle)
        {
            try
            {
                HolySheetConfig config = HolySheetConfig.Load(configPath);
                BuildResult build = TableBuild.Run(config, new TableBuildOptions { ValidatorTypes = validators, Offline = offline, RefreshGoogle = refreshGoogle, Pipeline = pipeline, Only = only, CredentialProviders = providers });
                CommitReport? report = commit && build.Success ? TableBuild.Commit(config, build) : null;
                // The result is kept for the window, which only shows the tables as read and the problems.
                // The typed values and generated bytes are several times larger and are not needed once written.
                build.ReleaseBuildData();
                return new HolySheetRunResult(build, report, null);
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is JsonFormatException)
            {
                return new HolySheetRunResult(new BuildResult(new DiagnosticBag()), null, exception.Message);
            }
        }

        private static void Finish(HolySheetRunResult result)
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
        public static string Summarize(HolySheetRunResult result)
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

        [MenuItem("Tools/HolySheet/Build Tables %#&b", priority = 11)]
        private static void BuildMenu()
        {
            if (!EnsureConfig())
            {
                return;
            }

            RunAsync(commit: true).ContinueWith(_ => { });
            LogWhenFinished();
        }

        [MenuItem("Tools/HolySheet/Check Tables", priority = 12)]
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

            Debug.LogWarning("[HolySheet] There is no holysheet.json in the project root yet. Open Tools > HolySheet > Tables to create one.");
            return false;
        }

        private static void LogWhenFinished()
        {
            void OnFinished(HolySheetRunResult result)
            {
                RunFinished -= OnFinished;
                LogResult(result);
            }

            RunFinished += OnFinished;
        }

        /// <summary>Writes a result to the console: one line per problem, then the summary.</summary>
        public static void LogResult(HolySheetRunResult result)
        {
            foreach (Diagnostic diagnostic in result.Build.Diagnostics.Items)
            {
                string text = "[HolySheet] " + diagnostic.Location + ": " + diagnostic.Message + (diagnostic.Hint.Length > 0 ? "\nFix: " + diagnostic.Hint : "");
                if (diagnostic.Severity == Severity.Error)
                {
                    Debug.LogError(text);
                }
                else
                {
                    Debug.LogWarning(text);
                }
            }

            string summary = "[HolySheet] " + Summarize(result);
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
    /// <code>Unity -batchmode -quit -projectPath . -executeMethod HolySheet.Editor.HolySheetCommandLine.Build</code>
    /// The editor exits with 0 on success, 1 when the sheets have errors and 3 when the tool could not run.
    /// Add <c>-holySheetOffline</c> to build from the saved Google snapshot instead of the network.
    /// </summary>
    public static class HolySheetCommandLine
    {
        public static void Build() => Run(commit: true);

        public static void Check() => Run(commit: false);

        private static void Run(bool commit)
        {
            bool offline = Environment.GetCommandLineArgs().Contains("-holySheetOffline");
            HolySheetRunResult result = HolySheetProject.RunBlocking(commit, offline);
            HolySheetProject.LogResult(result);
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(result.Failure != null ? 3 : result.Build.Success ? 0 : 1);
            }
        }
    }
}
