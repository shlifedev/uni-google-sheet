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
    public sealed class HolySheetRunResult
    {
        public BuildResult Build { get; }

        public CommitReport? Commit { get; }

        public string? Failure { get; }

        public bool WroteFiles => Commit != null && Commit.HasChanges;

        public HolySheetRunResult(BuildResult build, CommitReport? commit, string? failure)
        {
            Build = build;
            Commit = commit;
            Failure = failure;
        }
    }

    public static class HolySheetProject
    {
        private static Task<HolySheetRunResult>? _running;

        public static string RootDirectory => Path.GetDirectoryName(Application.dataPath)!;

        public static string ConfigPath => Path.Combine(RootDirectory, HolySheetConfig.DefaultFileName);

        public static bool HasConfig => File.Exists(ConfigPath);

        public static HolySheetRunResult? LastResult { get; private set; }

        public static bool IsRunning => _running != null && !_running.IsCompleted;

        public static event Action? RunStarted;

        public static event Action<HolySheetRunResult>? RunFinished;

        public static Func<TransformPipeline>? CreatePipeline { get; set; }

        public static IReadOnlyList<string> CredentialProviderNames() =>
            ProviderTypes().Where(GoogleCredentialProviders.IsProvider).Select(GoogleCredentialProviders.NameOf).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name, StringComparer.Ordinal).ToList();

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

        private static IEnumerable<Type> ProviderTypes() =>
            TypeCache.GetTypesDerivedFrom<IGoogleCredentialProvider>().Concat(TypeCache.GetTypesWithAttribute<HolySheetCredentialProviderAttribute>());

        public static string DescribeCsvBackend(HolySheetConfig? config = null)
        {
            string requested = (config?.CsvBackend ?? CsvBackends.Auto).Trim().ToLowerInvariant();
            if (requested == CsvBackends.Sep)
            {
                return CsvBackends.Fast != null ? "sep" : "sep (not available on this editor)";
            }

            return "builtin";
        }

        public static bool IsSepAvailable => CsvBackends.Fast != null;

        public static string DescribeRuntime() => RuntimeInformation.FrameworkDescription;

        public static HolySheetConfig LoadConfig() => HolySheetConfig.Load(ConfigPath);

        public static HolySheetConfig CreateDefaultConfig()
        {
            var config = new HolySheetConfig { BaseDirectory = RootDirectory };
            config.Sources.Add(new SourceConfig { Type = SourceType.Xlsx, Path = "Sheets" });
            config.Output.Code = "Assets/Game/Generated/Tables";
            config.Output.Data = "Assets/StreamingAssets/Tables";
            Directory.CreateDirectory(Path.Combine(RootDirectory, "Sheets"));
            config.Save(ConfigPath);
            return config;
        }

        public static void SaveConfig(HolySheetConfig config) => config.Save(ConfigPath);

        public static Task<HolySheetRunResult> RunAsync(bool commit, bool offline = false, SheetSelection? only = null)
            => StartRun(commit, offline, only, refreshGoogle: false);

        internal static Task<HolySheetRunResult> RefreshAsync()
            => StartRun(commit: false, offline: false, only: null, refreshGoogle: true);

        internal static Task<HolySheetRunResult> ChangeLocalCopy(GoogleTab tab, bool useLocal)
            => StartRun(false, false, null, false, (tab.Workbook, tab.Sheet, useLocal));

        private static Task<HolySheetRunResult> StartRun(bool commit, bool offline, SheetSelection? only, bool refreshGoogle,
            (string Workbook, string Sheet, bool UseLocal)? copy = null)
        {
            if (_running != null && !_running.IsCompleted)
            {
                return _running;
            }

            TransformPipeline? pipeline = CreatePipeline?.Invoke();
            string configPath = ConfigPath;
            IReadOnlyList<IGoogleCredentialProvider> providers = CreateCredentialProviders();
            Type[] validators = DiscoverValidators();
            BuildResult? previous = copy.HasValue ? LastResult?.Build : null;
            Task<HolySheetRunResult> task = Task.Run(() => Run(configPath, commit, offline, pipeline, only, providers, validators, refreshGoogle, copy, previous));
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

        public static HolySheetRunResult RunBlocking(bool commit, bool offline = false, SheetSelection? only = null)
        {
            HolySheetRunResult result = Run(ConfigPath, commit, offline, CreatePipeline?.Invoke(), only, CreateCredentialProviders(), DiscoverValidators(), refreshGoogle: false);
            Finish(result);
            return result;
        }

        private static Type[] DiscoverValidators() => TypeCache.GetTypesDerivedFrom<ITableValidator>()
            .Where(type => type.IsVisible && type.IsClass && !type.IsAbstract && !type.ContainsGenericParameters).ToArray();

        private static HolySheetRunResult Run(string configPath, bool commit, bool offline, TransformPipeline? pipeline, SheetSelection? only, IReadOnlyList<IGoogleCredentialProvider> providers, Type[] validators, bool refreshGoogle,
            (string Workbook, string Sheet, bool UseLocal)? copy = null, BuildResult? previous = null)
        {
            try
            {
                HolySheetConfig config = HolySheetConfig.Load(configPath);
                var options = new TableBuildOptions { ValidatorTypes = validators, Offline = offline, RefreshGoogle = refreshGoogle, Pipeline = pipeline, Only = only, CredentialProviders = providers, KeepGooglePreview = true };
                BuildResult build = copy is { } change && previous != null
                    ? GoogleTabCopies.ChangeAsync(config, previous, change.Workbook, change.Sheet, change.UseLocal, options).GetAwaiter().GetResult()
                    : TableBuild.Run(config, options);
                CommitReport? report = commit && build.Success ? TableBuild.Commit(config, build) : null;
                build.ReleaseBuildData();
                return new HolySheetRunResult(build, report, null);
            }
            catch (Exception exception) when (copy.HasValue || exception is IOException || exception is UnauthorizedAccessException || exception is JsonFormatException)
            {
                return new HolySheetRunResult(previous ?? new BuildResult(new DiagnosticBag()), null, exception.Message);
            }
        }

        private static void Finish(HolySheetRunResult result)
        {
            LastResult = result;
            if (result.WroteFiles)
            {
                AssetDatabase.Refresh();
            }

            RunFinished?.Invoke(result);
        }

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
