#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UGS.Core;
using UGS.Core.Config;
using UGS.Core.Output;
using UGS.Core.Schema;
using UGS.Core.Sources;
using UGS.Core.Transform;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using static UGS.Editor.UgsText;

namespace UGS.Editor
{
    /// <summary>
    /// Tools > UGS > Tables. Build, look at the tables, read the problems, change ugs.json.
    /// Everything runs through <see cref="UgsProject"/>, so the window can do nothing the command line cannot.
    /// </summary>
    public sealed class UgsWindow : EditorWindow
    {
        private const string StyleSheetPath = "Packages/com.shlifedev.ugs/Editor/UgsWindow.uss";

        // Problems are not a page: they open in a panel under the table view, so the data stays in sight.
        private static readonly string[] Pages = { "Tables", "Settings" };

        private static readonly (string Label, string[] Rows)[] HeaderLayouts =
        {
            ("type, name, comment", new[] { "type", "name", "comment" }),
            ("comment, name, type", new[] { "comment", "name", "type" }),
            ("name, type, comment", new[] { "name", "type", "comment" }),
            ("name, type", new[] { "name", "type" }),
            ("type, name", new[] { "type", "name" })
        };

        private static readonly List<string> Serializers = new List<string> { "memorypack", "messagepack" };
        private static readonly List<string> CsvBackendNames = new List<string> { "auto", "builtin", "sep" };
        // In GoogleAuthType order, in the window's language.
        private static List<string> AuthNames => new List<string> { T("Public link"), T("API key"), T("Service account"), T("Access token") };

        private UgsConfig? _config;
        private string? _configError;
        private string _page = "Tables";
        private string? _selectedTable;
        private string? _verifyReport;
        private bool _offline;

        // The problems panel under the table view: open or not, and its height, kept while the window is open.
        private bool _problemsOpen;
        private float _problemsHeight = 150f;

        private readonly List<ToolbarToggle> _tabs = new List<ToolbarToggle>();
        private VisualElement _banner = null!;
        private VisualElement _content = null!;
        private Image _statusIcon = null!;
        private Label _statusText = null!;
        private ToolbarButton _checkButton = null!;
        private ToolbarButton _buildButton = null!;

        public static IReadOnlyList<string> PageNames => Pages;

        [MenuItem("Tools/UGS/Tables", priority = 0)]
        public static void Open()
        {
            UgsWindow window = GetWindow<UgsWindow>();
            window.titleContent = new GUIContent("UGS");
            window.minSize = new Vector2(560, 320);
            window.Show();
        }

        public void SelectPage(string page)
        {
            // "Problems" was a page of its own; it now opens the problems panel under the tables.
            if (page == "Problems")
            {
                _problemsOpen = true;
                page = "Tables";
            }

            if (Array.IndexOf(Pages, page) < 0)
            {
                throw new ArgumentException("There is no page named '" + page + "'.", nameof(page));
            }

            _page = page;
            if (_content != null)
            {
                ShowPage();
            }
        }

        private void OnEnable()
        {
            UgsProject.RunStarted += OnRunStarted;
            UgsProject.RunFinished += OnRunFinished;
        }

        private void OnDisable()
        {
            UgsProject.RunStarted -= OnRunStarted;
            UgsProject.RunFinished -= OnRunFinished;
        }

        public void CreateGUI()
        {
            VisualElement root = rootVisualElement;
            root.Clear();
            StyleSheet? styleSheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(StyleSheetPath);
            if (styleSheet != null)
            {
                root.styleSheets.Add(styleSheet);
            }

            root.AddToClassList("ugs");
            root.AddToClassList(EditorGUIUtility.isProSkin ? "ugs--dark" : "ugs--light");
            root.Add(BuildToolbar());
            // Ctrl+F (Cmd+F on macOS) opens the find bar of the table preview. Unity routes that shortcut through its
            // Edit > Find menu as a "Find" command, so both the command and the raw key are handled.
            root.RegisterCallback<ValidateCommandEvent>(evt =>
            {
                if (evt.commandName == "Find" && _page == "Tables" && _findBar != null)
                {
                    evt.StopPropagation();
                }
            }, TrickleDown.TrickleDown);
            root.RegisterCallback<ExecuteCommandEvent>(evt =>
            {
                if (evt.commandName == "Find" && OpenFind())
                {
                    evt.StopPropagation();
                }
            }, TrickleDown.TrickleDown);
            root.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.F && evt.actionKey && OpenFind())
                {
                    evt.StopPropagation();
                }
                else if (evt.keyCode == KeyCode.Escape && _findBar != null && _findBar.style.display == DisplayStyle.Flex)
                {
                    CloseFind();
                    evt.StopPropagation();
                }
            }, TrickleDown.TrickleDown);
            _banner = new VisualElement();
            _banner.AddToClassList("ugs-banner");
            root.Add(_banner);
            _content = new VisualElement();
            _content.AddToClassList("ugs-fill");
            root.Add(_content);

            ReloadConfig();
            CheckIfNothingWasRead();
            // Errors from a run before the window opened are shown too, not only those that arrive while it is open.
            UgsRunResult? last = UgsProject.LastResult;
            _problemsOpen |= last != null && (last.Failure != null || last.Build.Diagnostics.HasErrors);
            Refresh();
        }

        // The last result lives in memory, so it is gone after an editor restart or a script reload (which every build that
        // writes code causes). The sheets are then read again right away, so the tree and the problems are there without
        // pressing Check. Google sources are read from their saved snapshot when every one of them has a snapshot on disk.
        private void CheckIfNothingWasRead()
        {
            if (_config == null || UgsProject.LastResult != null || UgsProject.IsRunning)
            {
                return;
            }

            bool snapshotsReady = _config.Sources.Where(source => source.IsRemote)
                .All(source => source.Snapshot != null && Directory.Exists(_config.Resolve(source.Snapshot)));
            UgsProject.RunAsync(commit: false, offline: snapshotsReady);
        }

        // ------------------------------------------------------------ toolbar

        private Toolbar BuildToolbar()
        {
            var toolbar = new Toolbar();
            _tabs.Clear();
            foreach (string page in Pages)
            {
                string name = page;
                var tab = new ToolbarToggle { text = T(page) };
                tab.AddToClassList("ugs-tab");
                tab.RegisterValueChangedCallback(change =>
                {
                    if (change.newValue)
                    {
                        SelectPage(name);
                    }
                    else if (_page == name)
                    {
                        tab.SetValueWithoutNotify(true);
                    }
                });
                _tabs.Add(tab);
                toolbar.Add(tab);
            }

            toolbar.Add(new ToolbarSpacer { flex = true });

            _statusIcon = new Image();
            _statusIcon.AddToClassList("ugs-status-icon");
            toolbar.Add(_statusIcon);
            _statusText = new Label();
            _statusText.AddToClassList("ugs-status");
            // The status says how many problems there are; clicking it shows them.
            _statusText.RegisterCallback<ClickEvent>(_ => SelectPage("Problems"));
            _statusIcon.RegisterCallback<ClickEvent>(_ => SelectPage("Problems"));
            toolbar.Add(_statusText);

            var offline = new ToolbarToggle { text = T("Offline"), value = _offline, tooltip = T("Build Google sources from the saved snapshot instead of the network.") };
            offline.RegisterValueChangedCallback(change => _offline = change.newValue);
            toolbar.Add(offline);

            var auto = new ToolbarToggle { text = T("Auto build"), value = UgsProject.AutoBuild, tooltip = T("Build whenever a local sheet file is saved.") };
            auto.RegisterValueChangedCallback(change => UgsProject.AutoBuild = change.newValue);
            toolbar.Add(auto);

            _checkButton = new ToolbarButton(() => Run(commit: false)) { text = T("Check"), tooltip = T("Read and validate the sheets. Writes nothing.") };
            toolbar.Add(_checkButton);
            _buildButton = new ToolbarButton(() => Run(commit: true)) { text = T("Build"), tooltip = T("Write generated code and data (Ctrl+Shift+Alt+B).") };
            _buildButton.AddToClassList("ugs-build");
            toolbar.Add(_buildButton);
            return toolbar;
        }

        private void Refresh()
        {
            RefreshBanner();
            ShowPage();
        }

        private void RefreshToolbar()
        {
            for (int index = 0; index < Pages.Length; index++)
            {
                _tabs[index].SetValueWithoutNotify(Pages[index] == _page);
            }

            bool canRun = _config != null && !UgsProject.IsRunning;
            _checkButton.SetEnabled(canRun);
            _buildButton.SetEnabled(canRun);
            _selectionBar?.Query<ToolbarButton>().ForEach(button => button.SetEnabled(canRun));

            UgsRunResult? result = UgsProject.LastResult;
            string icon = "";
            string text = "";
            if (UgsProject.IsRunning)
            {
                icon = "d_WaitSpin00";
                text = T("Running");
            }
            else if (result != null)
            {
                DiagnosticBag diagnostics = result.Build.Diagnostics;
                string counts = Plural(diagnostics.ErrorCount, "error") + (diagnostics.WarningCount > 0 ? " · " + Plural(diagnostics.WarningCount, "warning") : "");
                if (result.Failure != null)
                {
                    icon = "console.erroricon.sml";
                    text = T("Failed");
                }
                else if (!result.Build.Success)
                {
                    icon = "console.erroricon.sml";
                    text = T("Failed") + " · " + counts;
                }
                else
                {
                    icon = diagnostics.WarningCount > 0 ? "console.warnicon.sml" : "TestPassed";
                    string verb = result.Commit == null ? T("Checked") : result.Commit.HasChanges ? T("Built") : T("Up to date");
                    if (result.Build.Only != null)
                    {
                        verb += " " + Plural(result.Build.RefreshedSheets.Count, "sheet");
                    }

                    text = verb + " · " + Plural(result.Build.TableCount, "table") + (diagnostics.WarningCount > 0 ? " · " + Plural(diagnostics.WarningCount, "warning") : "");
                }
            }

            _statusText.text = text;
            _statusText.tooltip = result == null || UgsProject.IsRunning ? "" : UgsProject.Summarize(result);
            _statusIcon.image = icon.Length == 0 ? null : EditorGUIUtility.IconContent(icon).image;
            _statusIcon.style.display = icon.Length == 0 ? DisplayStyle.None : DisplayStyle.Flex;
        }

        private void RefreshBanner()
        {
            _banner.Clear();
            if (_config != null && !IsSerializerInstalled(_config.Codegen.Serializer))
            {
                _banner.Add(new HelpBox(F("{0} is not in this project. The generated code will not compile until it is added.", SerializerPackage(_config.Codegen.Serializer)), HelpBoxMessageType.Warning));
            }
        }

        // ------------------------------------------------------------ actions

        private void ReloadConfig()
        {
            _config = null;
            _configError = null;
            if (!UgsProject.HasConfig)
            {
                return;
            }

            try
            {
                _config = UgsProject.LoadConfig();
            }
            catch (Exception exception) when (exception is IOException || exception is JsonFormatException)
            {
                _configError = exception.Message;
            }
        }

        private void Save()
        {
            if (_config == null)
            {
                return;
            }

            try
            {
                UgsProject.SaveConfig(_config);
            }
            catch (IOException exception)
            {
                Debug.LogError("[UGS] ugs.json could not be saved: " + exception.Message);
            }

            RefreshBanner();
        }

        private void Run(bool commit)
        {
            if (_config != null && !UgsProject.IsRunning)
            {
                UgsProject.RunAsync(commit, _offline);
            }
        }

        private void OnRunStarted()
        {
            if (_content != null)
            {
                RefreshToolbar();
            }
        }

        private void OnRunFinished(UgsRunResult result)
        {
            if (_content == null)
            {
                return;
            }

            if (result.Failure != null || result.Build.Diagnostics.HasErrors)
            {
                _page = "Tables";
                _problemsOpen = true;
            }

            _sheetsByTable.Clear();
            Refresh();
        }

        // ------------------------------------------------------------ pages

        private void ShowPage()
        {
            RefreshToolbar();
            _content.Clear();
            if (_config == null)
            {
                _content.Add(BuildNoConfig());
                return;
            }

            switch (_page)
            {
                case "Settings":
                    _content.Add(BuildSettings());
                    break;
                default:
                    _content.Add(BuildTables());
                    break;
            }
        }

        private VisualElement BuildNoConfig()
        {
            var page = new VisualElement();
            page.AddToClassList("ugs-page");
            if (_configError == null)
            {
                page.Add(new HelpBox(T("This project has no ugs.json yet. It holds the sources and output folders; the ugs command line reads the same file."), HelpBoxMessageType.Info));
                page.Add(Buttons(new Button(() =>
                {
                    UgsProject.CreateDefaultConfig();
                    ReloadConfig();
                    _page = "Settings";
                    Refresh();
                }) { text = T("Create ugs.json") }));
            }
            else
            {
                page.Add(new HelpBox(T("ugs.json could not be read: ") + _configError, HelpBoxMessageType.Error));
                page.Add(Buttons(
                    new Button(() => EditorUtility.OpenWithDefaultApp(UgsProject.ConfigPath)) { text = T("Open") },
                    new Button(() =>
                    {
                        ReloadConfig();
                        Refresh();
                    }) { text = T("Reload") }));
            }

            return page;
        }

        private VisualElement BuildProblems()
        {
            UgsRunResult? result = UgsProject.LastResult;
            if (result == null)
            {
                return Empty(UgsProject.IsRunning ? T("Reading the sheets…") : T("Nothing has run yet."));
            }

            List<Diagnostic> items = result.Failure != null
                ? new List<Diagnostic> { new Diagnostic(Severity.Error, "ugs", null, result.Failure, "") }
                : result.Build.Diagnostics.Items.OrderBy(item => item.Severity == Severity.Error ? 0 : 1).ToList();
            if (items.Count == 0)
            {
                return Empty(T("No problems."));
            }

            var container = new VisualElement();
            container.AddToClassList("ugs-fill");

            var list = new ListView(items, -1, MakeProblemRow, (element, index) => BindProblemRow(element, items[index]))
            {
                virtualizationMethod = CollectionVirtualizationMethod.DynamicHeight,
                showAlternatingRowBackgrounds = AlternatingRowBackground.ContentOnly,
                selectionType = SelectionType.Single
            };
            list.AddToClassList("ugs-fill");
            list.tooltip = T("Double-click a problem to open its file.");
            list.itemsChosen += _ =>
            {
                if (list.selectedIndex >= 0 && list.selectedIndex < items.Count)
                {
                    OpenProblem(items[list.selectedIndex]);
                }
            };
            container.Add(list);
            return container;
        }

        // The workbook (and sheet) a tree node stands for. Source groups stand for no single file.
        private static (string Workbook, string? Sheet)? SourceOf(TableNode node)
        {
            if (node.GroupWorkbooks != null)
            {
                return null;
            }

            if (node.Workbook != null)
            {
                return (node.Workbook, node.Sheet);
            }

            if (node.Table == null)
            {
                return null;
            }

            // A table opens at the sheet that defines it: its location is "Workbook / Sheet".
            int separator = node.Table.Location.IndexOf(" / ", StringComparison.Ordinal);
            return separator < 0 ? (node.Table.Location, null) : (node.Table.Location.Substring(0, separator), node.Table.Location.Substring(separator + 3));
        }

        private static void OpenNode(TableNode node)
        {
            if (SourceOf(node) is { } source)
            {
                OpenSource(source.Workbook, source.Sheet);
            }
        }

        // A problem names a cell ("Book / Sheet!C8"), a sheet ("Book / Sheet"), a workbook, or the config.
        private static void OpenProblem(Diagnostic problem)
        {
            if (problem.Location == "config" || problem.Location == "ugs")
            {
                EditorUtility.OpenWithDefaultApp(UgsProject.ConfigPath);
                return;
            }

            string workbook = problem.Origin?.Workbook ?? problem.Location;
            string? sheet = problem.Origin?.Sheet;
            int separator = workbook.IndexOf(" / ", StringComparison.Ordinal);
            if (problem.Origin == null && separator >= 0)
            {
                sheet = workbook.Substring(separator + 3);
                int cell = sheet.IndexOf('!');
                sheet = cell >= 0 ? sheet.Substring(0, cell) : sheet;
                workbook = workbook.Substring(0, separator);
            }

            OpenSource(workbook, sheet);
        }

        private static void OpenSource(string workbook, string? sheet)
        {
            string? target = FindSourceFile(workbook, sheet);
            if (target == null)
            {
                Debug.LogWarning(F("[UGS] The file of '{0}' was not found among the sources.", workbook));
            }
            else if (target.StartsWith("https://", StringComparison.Ordinal))
            {
                Application.OpenURL(target);
            }
            else
            {
                // The program the system uses for the file: Excel for .xlsx, the text editor for .csv.
                EditorUtility.OpenWithDefaultApp(target);
            }
        }

        /// <summary>
        /// Where a workbook can be opened: the .xlsx or .csv file on disk, or the address of a Google spreadsheet or Drive
        /// file. A CSV workbook opens at the given sheet's file. Null when no configured source has it.
        /// </summary>
        public static string? FindSourceFile(string workbook, string? sheet = null)
        {
            UgsConfig config;
            try
            {
                if (!UgsProject.HasConfig)
                {
                    return null;
                }

                config = UgsProject.LoadConfig();
            }
            catch (Exception exception) when (exception is IOException || exception is JsonFormatException)
            {
                return null;
            }

            WorkbookOrigin? origin = null;
            UgsProject.LastResult?.Build.WorkbookOrigins.TryGetValue(workbook, out origin);
            if (origin?.Link != null)
            {
                return origin.Link;
            }

            if (origin != null)
            {
                switch (origin.Type)
                {
                    case SourceType.Google:
                        return GoogleSheetsSource.SpreadsheetLink(origin.Source);
                    case SourceType.GoogleFolder:
                        return GoogleFolderSource.FolderLink(origin.Source);
                    case SourceType.Csv:
                        return CsvFile(config.Resolve(origin.Source), sheet);
                    default:
                        return XlsxFile(config.Resolve(origin.Source), workbook);
                }
            }

            // Not read (for example the file could not be parsed): look for it among the configured sources.
            foreach (SourceConfig source in config.Sources)
            {
                switch (source.Type)
                {
                    case SourceType.Xlsx:
                        if (XlsxFile(config.Resolve(source.Path ?? ""), workbook) is { } file)
                        {
                            return file;
                        }

                        break;
                    case SourceType.Csv:
                        string folder = config.Resolve(source.Path ?? "");
                        if (Path.GetFileName(folder.TrimEnd('/', '\\')) == workbook)
                        {
                            return CsvFile(folder, sheet);
                        }

                        break;
                    case SourceType.Google:
                        if (source.Name == workbook || source.SpreadsheetId == workbook)
                        {
                            return GoogleSheetsSource.SpreadsheetLink(source.SpreadsheetId ?? "");
                        }

                        break;
                }
            }

            return null;
        }

        private static string? XlsxFile(string path, string workbook)
        {
            if (File.Exists(path))
            {
                return Path.GetFileName(path) == workbook ? path : null;
            }

            string inFolder = Path.Combine(path, workbook);
            return File.Exists(inFolder) ? inFolder : null;
        }

        private static string? CsvFile(string folder, string? sheet)
        {
            string file = Path.Combine(folder, (sheet ?? "") + ".csv");
            return sheet != null && File.Exists(file) ? file : Directory.Exists(folder) ? folder : null;
        }

        private static VisualElement MakeProblemRow()
        {
            var row = new VisualElement();
            row.AddToClassList("ugs-problem");
            var icon = new Image();
            icon.AddToClassList("ugs-problem__icon");
            row.Add(icon);
            var text = new VisualElement();
            text.AddToClassList("ugs-fill");
            text.Add(Selectable("ugs-problem__message"));
            var detail = new VisualElement();
            detail.AddToClassList("ugs-problem__detail");
            detail.Add(Selectable("ugs-problem__location"));
            detail.Add(Selectable("ugs-problem__hint"));
            text.Add(detail);
            row.Add(text);
            return row;
        }

        private static void BindProblemRow(VisualElement row, Diagnostic diagnostic)
        {
            row.Q<Image>().image = EditorGUIUtility.IconContent(diagnostic.Severity == Severity.Error ? "console.erroricon.sml" : "console.warnicon.sml").image;
            row.Q<Label>(className: "ugs-problem__message").text = diagnostic.Message;
            row.Q<Label>(className: "ugs-problem__location").text = diagnostic.Location;
            Label hint = row.Q<Label>(className: "ugs-problem__hint");
            hint.text = diagnostic.Hint;
            hint.style.display = diagnostic.Hint.Length > 0 ? DisplayStyle.Flex : DisplayStyle.None;
        }

        /// <summary>A node of the table tree: a workbook, or a table in it.</summary>
        private sealed class TableNode
        {
            public string Label = "";
            public string Icon = "";
            public string Note = "";
            public string Tooltip = "";
            public RawTable? Table;

            /// <summary>Set on the sheet nodes under a table built from several sheets.</summary>
            public string? Sheet;

            /// <summary>The workbook of a workbook node or a sheet node.</summary>
            public string? Workbook;

            /// <summary>Identifies the node across rebuilds of the tree, so a selection survives filtering.</summary>
            public string Key = "";

            /// <summary>Set on source group nodes (Excel, Google Drive, …): the workbooks shown under it.</summary>
            public string[]? GroupWorkbooks;
        }

        /// <summary>The top level of the tree when a project reads from more than one kind of source.</summary>
        private enum SourceGroup
        {
            Excel,
            GoogleSheets,
            GoogleDrive,
            Csv,
            Other
        }

        private IReadOnlyDictionary<string, WorkbookOrigin> _origins = new Dictionary<string, WorkbookOrigin>();

        private SourceGroup GroupOf(string workbook)
        {
            if (!_origins.TryGetValue(workbook, out WorkbookOrigin? origin))
            {
                return workbook.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) ? SourceGroup.Excel : SourceGroup.Other;
            }

            switch (origin.Type)
            {
                case SourceType.Xlsx:
                    return SourceGroup.Excel;
                case SourceType.Csv:
                    return SourceGroup.Csv;
                case SourceType.Google:
                    return SourceGroup.GoogleSheets;
                case SourceType.GoogleFolder:
                    return SourceGroup.GoogleDrive;
                default:
                    return SourceGroup.Other;
            }
        }

        private static string GroupLabel(SourceGroup group)
        {
            switch (group)
            {
                case SourceGroup.Excel:
                    return "Excel";
                case SourceGroup.GoogleSheets:
                    return "Google Sheets";
                case SourceGroup.GoogleDrive:
                    return "Google Drive";
                case SourceGroup.Csv:
                    return "CSV";
                default:
                    return T("Other");
            }
        }

        private static string GroupIcon(SourceGroup group)
        {
            switch (group)
            {
                case SourceGroup.Excel:
                    return ExcelIconName;
                case SourceGroup.GoogleSheets:
                    return GoogleSheetsIconName;
                case SourceGroup.GoogleDrive:
                    return "CloudConnect";
                default:
                    return "Folder Icon";
            }
        }

        // The keys of the selected tree nodes. Several can be selected (Ctrl/Cmd or Shift click) for a partial build.
        private readonly HashSet<string> _selectedKeys = new HashSet<string>();

        // What the selected nodes cover: a workbook node is the whole workbook, a table node every sheet it was built from.
        private SheetSelection _treeSelection = new SheetSelection();

        private string? _selectedSheet;

        private string _tableFilter = "";

        // The sheets each table was assembled from. Counting them walks every row, so it is done once per build.
        private readonly Dictionary<RawTable, (string Workbook, string Sheet)[]> _sheetsByTable = new Dictionary<RawTable, (string Workbook, string Sheet)[]>();

        private VisualElement BuildTables() => BuildTableView();

        // The data on top, the problems panel under it (closed to a one-line bar unless opened), split so it can be resized.
        // It sits under the data only: the tree beside it keeps the full height.
        private VisualElement WithProblems(VisualElement data)
        {
            var page = new VisualElement();
            page.AddToClassList("ugs-fill");
            var split = new TwoPaneSplitView(1, _problemsHeight, TwoPaneSplitViewOrientation.Vertical);
            split.AddToClassList("ugs-fill");
            split.Add(data);
            VisualElement problems = BuildProblems();
            problems.AddToClassList("ugs-problems");
            problems.RegisterCallback<GeometryChangedEvent>(change =>
            {
                if (_problemsOpen && change.newRect.height > 40f)
                {
                    _problemsHeight = change.newRect.height;
                }
            });
            split.Add(problems);
            page.Add(split);
            page.Add(BuildProblemsBar(split));
            if (!_problemsOpen)
            {
                split.CollapseChild(1);
            }

            return page;
        }

        // A bar under the data: the counts, and a click opens or closes the panel above it.
        private VisualElement BuildProblemsBar(TwoPaneSplitView split)
        {
            UgsRunResult? result = UgsProject.LastResult;
            List<Diagnostic> items = result == null ? new List<Diagnostic>()
                : result.Failure != null ? new List<Diagnostic> { new Diagnostic(Severity.Error, "ugs", null, result.Failure, "") }
                : result.Build.Diagnostics.Items.ToList();
            int errors = items.Count(item => item.Severity == Severity.Error);
            int warnings = items.Count - errors;

            var bar = new Toolbar();
            bar.AddToClassList("ugs-problems-bar");
            var arrow = new Label(_problemsOpen ? "▼" : "▲");
            arrow.AddToClassList("ugs-problems-bar__arrow");
            bar.Add(arrow);
            if (errors > 0)
            {
                bar.Add(CountBadge("console.erroricon.sml", errors));
            }

            if (warnings > 0)
            {
                bar.Add(CountBadge("console.warnicon.sml", warnings));
            }

            string text = result == null ? (UgsProject.IsRunning ? T("Reading the sheets…") : T("Nothing has run yet."))
                : items.Count == 0 ? T("No problems.")
                : T("Problems");
            var label = new Label(text);
            label.AddToClassList("ugs-problems-bar__text");
            bar.Add(label);
            bar.Add(new ToolbarSpacer { flex = true });
            if (items.Count > 0)
            {
                var copy = new ToolbarButton(() => EditorGUIUtility.systemCopyBuffer = string.Join("\n\n", items.Select(item => item.ToString()))) { text = T("Copy all") };
                // Copying is not a click on the bar.
                copy.RegisterCallback<ClickEvent>(click => click.StopPropagation());
                bar.Add(copy);
            }

            bar.tooltip = T("Click to show or hide the problems of the last run.");
            bar.RegisterCallback<ClickEvent>(_ =>
            {
                _problemsOpen = !_problemsOpen;
                if (_problemsOpen)
                {
                    split.UnCollapse();
                }
                else
                {
                    split.CollapseChild(1);
                }

                arrow.text = _problemsOpen ? "▼" : "▲";
            });
            return bar;
        }

        private static VisualElement CountBadge(string icon, int count)
        {
            var badge = new VisualElement();
            badge.AddToClassList("ugs-problems-bar__count");
            var image = new Image { image = Icon(icon) };
            image.AddToClassList("ugs-icon");
            badge.Add(image);
            badge.Add(new Label(count.ToString("N0")));
            return badge;
        }

        private VisualElement BuildTableView()
        {
            BuildResult? build = UgsProject.LastResult?.Build;
            if (build == null || build.RawTables.Count == 0)
            {
                // No tables, but there may be problems that explain why.
                return WithProblems(Empty(UgsProject.IsRunning ? T("Reading the sheets…") : T("Run Check to see the tables as the build reads them.")));
            }

            var split = new TwoPaneSplitView(0, 170, TwoPaneSplitViewOrientation.Horizontal);
            split.AddToClassList("ugs-fill");

            var side = new VisualElement();
            side.AddToClassList("ugs-side");
            var search = new ToolbarSearchField { value = _tableFilter, tooltip = T("Filter by table, sheet or workbook name.") };
            search.AddToClassList("ugs-search");
            var searchBar = new Toolbar();
            searchBar.Add(search);
            side.Add(searchBar);

            var tree = new TreeView
            {
                fixedItemHeight = 20,
                selectionType = SelectionType.Multiple,
                makeItem = MakeTreeRow
            };
            tree.AddToClassList("ugs-fill");
            tree.bindItem = (element, index) =>
            {
                TableNode node = tree.GetItemDataForIndex<TableNode>(index);
                element.Q<Image>().image = TreeIcon(node.Icon);
                element.Q<Label>(className: "ugs-tree-row__name").text = node.Label;
                Label note = element.Q<Label>(className: "ugs-tree-row__note");
                note.text = node.Note;
                note.style.display = node.Note.Length > 0 ? DisplayStyle.Flex : DisplayStyle.None;
                element.tooltip = node.Tooltip;
            };
            side.Add(tree);

            // Build or check only what is selected in the tree; everything else comes from the last build.
            var selectionBar = new Toolbar();
            selectionBar.AddToClassList("ugs-selection");
            var selectionLabel = new Label();
            selectionLabel.AddToClassList("ugs-selection__label");
            selectionBar.Add(selectionLabel);
            var selectionCheck = new ToolbarButton(() => RunSelection(commit: false)) { text = T("Check"), tooltip = T("Read only the selected sheets again and validate. Writes nothing.") };
            var selectionBuild = new ToolbarButton(() => RunSelection(commit: true)) { text = T("Build"), tooltip = T("Read only the selected sheets again and write the result.") };
            selectionBuild.AddToClassList("ugs-build");
            selectionBar.Add(selectionCheck);
            selectionBar.Add(selectionBuild);
            side.Add(selectionBar);
            _selectionBar = selectionBar;
            _selectionLabel = selectionLabel;
            // Double-click (or Enter) opens the workbook the node came from.
            tree.itemsChosen += _ =>
            {
                if (tree.selectedIndex >= 0)
                {
                    OpenNode(tree.GetItemDataForIndex<TableNode>(tree.selectedIndex));
                }
            };
            tree.AddManipulator(new ContextualMenuManipulator(menu =>
            {
                List<(string Workbook, string? Sheet)> targets = tree.selectedIndices
                    .Select(index => SourceOf(tree.GetItemDataForIndex<TableNode>(index)))
                    .Where(target => target != null).Select(target => target!.Value).Distinct().Take(10).ToList();
                if (targets.Count > 0)
                {
                    menu.menu.AppendAction(T("Open"), _ =>
                    {
                        foreach ((string workbook, string? sheet) in targets)
                        {
                            OpenSource(workbook, sheet);
                        }
                    });
                    menu.menu.AppendSeparator();
                }

                if (_treeSelection.IsEmpty)
                {
                    return;
                }

                DropdownMenuAction.Status status = _config != null && !UgsProject.IsRunning ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled;
                menu.menu.AppendAction(F("Build {0}", DescribeSelection()), _ => RunSelection(commit: true), status);
                menu.menu.AppendAction(F("Check {0}", DescribeSelection()), _ => RunSelection(commit: false), status);
            }));

            var preview = new VisualElement();
            preview.AddToClassList("ugs-fill");
            tree.selectedIndicesChanged += indices =>
            {
                TableNode? shown = null;
                _selectedKeys.Clear();
                foreach (int index in indices)
                {
                    TableNode node = tree.GetItemDataForIndex<TableNode>(index);
                    _selectedKeys.Add(node.Key);
                    shown = node.Table != null ? node : shown;
                }

                UpdateTreeSelection(tree);
                // With several nodes selected the preview shows the one clicked last.
                if (shown != null && (shown.Table!.Name != _selectedTable || shown.Sheet != _selectedSheet || preview.childCount == 0))
                {
                    _selectedTable = shown.Table.Name;
                    _selectedSheet = shown.Sheet;
                    ShowPreview(preview, shown.Table, shown.Sheet);
                }
            };

            search.RegisterValueChangedCallback(change =>
            {
                _tableFilter = change.newValue ?? "";
                FillTree(tree, build.RawTables, preview);
            });

            split.Add(side);
            split.Add(WithProblems(preview));
            FillTree(tree, build.RawTables, preview);
            return split;
        }

        // Workbook > table > sheet. A table built from one sheet has no sheet nodes; one built from several
        // (Item, Item.Rare, Item.Col.Balance) lists them under it, collapsed unless the filter matched one.
        // Selects what was selected before, else a sheet the filter matched, else the first table.
        private void FillTree(TreeView tree, IReadOnlyList<RawTable> tables, VisualElement preview)
        {
            string filter = _tableFilter.Trim();
            var roots = new List<TreeViewItemData<TableNode>>();
            var expand = new List<int>();
            int nextId = 0;
            (int Id, RawTable Table, string? Sheet)? previous = null, sheetMatch = null, first = null;
            // Whether the node selected before matches the filter itself, rather than being shown only for a child sheet.
            bool previousMatches = false;
            var keyed = new List<(string Key, int Id)>();
            _origins = UgsProject.LastResult?.Build.WorkbookOrigins ?? new Dictionary<string, WorkbookOrigin>();
            List<IGrouping<string, RawTable>> workbooks = tables.GroupBy(WorkbookOf)
                .OrderBy(group => (int)GroupOf(group.Key)).ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase).ToList();
            // The source level appears once the project reads from more than one kind of source, and stays while filtering.
            bool showGroups = workbooks.Select(group => GroupOf(group.Key)).Distinct().Count() > 1;
            var workbookNodes = new List<(SourceGroup Group, string Workbook, TreeViewItemData<TableNode> Node)>();
            foreach (IGrouping<string, RawTable> workbook in workbooks)
            {
                bool workbookMatches = filter.Length == 0 || Contains(workbook.Key, filter) || (showGroups && Contains(GroupLabel(GroupOf(workbook.Key)), filter));
                var children = new List<TreeViewItemData<TableNode>>();
                foreach (RawTable table in workbook.OrderBy(table => table.Name, StringComparer.Ordinal))
                {
                    string[] sheets = SheetsOf(table);
                    bool tableMatches = workbookMatches || Contains(table.Name, filter);
                    var sheetNodes = new List<TreeViewItemData<TableNode>>();
                    bool anySheetMatched = false;
                    int tableId = nextId++;
                    if (sheets.Length > 1)
                    {
                        foreach (string sheet in sheets)
                        {
                            bool sheetMatches = filter.Length > 0 && Contains(sheet, filter);
                            if (!tableMatches && !sheetMatches)
                            {
                                continue;
                            }

                            int sheetId = nextId++;
                            sheetNodes.Add(new TreeViewItemData<TableNode>(sheetId, new TableNode
                            {
                                Label = sheet,
                                Table = table,
                                Sheet = sheet,
                                Workbook = SheetRefsOf(table).First(entry => entry.Sheet == sheet).Workbook,
                                Key = "sheet:" + table.Name + "/" + sheet,
                                Icon = TableIconName,
                                Tooltip = DescribeSheet(table, sheet)
                            }));
                            keyed.Add(("sheet:" + table.Name + "/" + sheet, sheetId));
                            if (table.Name == _selectedTable && sheet == _selectedSheet)
                            {
                                previous = (sheetId, table, sheet);
                                previousMatches = tableMatches || sheetMatches;
                            }

                            if (sheetMatches && !tableMatches && sheetMatch == null)
                            {
                                sheetMatch = (sheetId, table, sheet);
                            }

                            anySheetMatched |= sheetMatches;
                        }
                    }

                    if (!tableMatches && sheetNodes.Count == 0)
                    {
                        continue;
                    }

                    children.Add(new TreeViewItemData<TableNode>(tableId, new TableNode
                    {
                        Label = table.Name,
                        Table = table,
                        Key = "table:" + table.Name,
                        Icon = table.Kind == TableKind.Config ? "Settings" : TableIconName,
                        // A table built from more than one sheet (Item + Item.Rare + Item.Col.Balance) says how many more.
                        Note = sheets.Length > 1 ? "+" + (sheets.Length - 1) : "",
                        Tooltip = (table.Kind == TableKind.Config ? T("Config table: one row, fields written down the sheet.\n") : "")
                            + Plural(table.Rows.Count, "row") + " · " + Plural(table.Columns.Count, "column") + "\n" + string.Join("\n", sheets)
                    }, sheetNodes));
                    keyed.Add(("table:" + table.Name, tableId));
                    if (table.Name == _selectedTable && _selectedSheet == null)
                    {
                        previous = (tableId, table, null);
                        previousMatches = tableMatches;
                    }

                    first ??= (tableId, table, null);
                    if (anySheetMatched || (previous?.Table == table && previous?.Sheet != null))
                    {
                        expand.Add(tableId);
                    }
                }

                if (children.Count > 0)
                {
                    int workbookId = nextId++;
                    workbookNodes.Add((GroupOf(workbook.Key), workbook.Key, new TreeViewItemData<TableNode>(workbookId, new TableNode
                    {
                        Label = workbook.Key,
                        Workbook = workbook.Key,
                        Key = "workbook:" + workbook.Key,
                        Icon = WorkbookIcon(workbook.Key),
                        Tooltip = _origins.TryGetValue(workbook.Key, out WorkbookOrigin? origin) && origin.Source.Length > 0
                            ? workbook.Key + "\n" + GroupLabel(GroupOf(workbook.Key)) + ": " + origin.Source
                            : workbook.Key
                    }, children)));
                    keyed.Add(("workbook:" + workbook.Key, workbookId));
                    expand.Add(workbookId);
                }
            }

            if (!showGroups)
            {
                roots.AddRange(workbookNodes.Select(entry => entry.Node));
            }
            else
            {
                foreach (IGrouping<SourceGroup, (SourceGroup Group, string Workbook, TreeViewItemData<TableNode> Node)> group in workbookNodes.GroupBy(entry => entry.Group))
                {
                    int groupId = nextId++;
                    string label = GroupLabel(group.Key);
                    string[] names = group.Select(entry => entry.Workbook).ToArray();
                    roots.Add(new TreeViewItemData<TableNode>(groupId, new TableNode
                    {
                        Label = label,
                        Key = "group:" + group.Key,
                        Icon = GroupIcon(group.Key),
                        Note = names.Length.ToString(),
                        GroupWorkbooks = names,
                        Tooltip = Plural(names.Length, "workbook") + "\n" + string.Join("\n", names)
                    }, group.Select(entry => entry.Node).ToList()));
                    keyed.Add(("group:" + group.Key, groupId));
                    expand.Add(groupId);
                }
            }

            tree.SetRootItems(roots);
            tree.Rebuild();
            tree.CollapseAll();
            foreach (int id in expand)
            {
                tree.ExpandItem(id);
            }

            preview.Clear();
            // Keep the earlier selection if it still matches; otherwise prefer the sheet the filter named.
            (int Id, RawTable Table, string? Sheet)? selected = (previousMatches ? previous : null) ?? sheetMatch ?? previous ?? first;
            if (selected == null)
            {
                preview.Add(Empty(F("No table matches '{0}'.", filter)));
                _selectedKeys.Clear();
                UpdateTreeSelection(tree);
                return;
            }

            _selectedTable = selected.Value.Table.Name;
            _selectedSheet = selected.Value.Sheet;
            // A multiple selection is kept for the nodes still shown; a single one follows the previewed table.
            var ids = new List<int>();
            if (_selectedKeys.Count > 1)
            {
                ids.AddRange(keyed.Where(entry => _selectedKeys.Contains(entry.Key)).Select(entry => entry.Id));
            }

            if (ids.Count < 2)
            {
                ids.Clear();
                ids.Add(selected.Value.Id);
            }

            _selectedKeys.Clear();
            foreach ((string key, int id) in keyed)
            {
                if (ids.Contains(id))
                {
                    _selectedKeys.Add(key);
                }
            }

            tree.SetSelectionByIdWithoutNotify(ids);
            UpdateTreeSelection(tree);
            ShowPreview(preview, selected.Value.Table, selected.Value.Sheet);
        }

        private Toolbar? _selectionBar;
        private Label? _selectionLabel;

        // Turns the selected nodes into the sheets a partial build reads again.
        private void UpdateTreeSelection(TreeView tree)
        {
            var selection = new SheetSelection();
            _singleSelectedNode = null;
            int count = 0;
            foreach (int index in tree.selectedIndices)
            {
                TableNode node = tree.GetItemDataForIndex<TableNode>(index);
                _singleSelectedNode = count++ == 0 ? node : null;
                if (node.GroupWorkbooks != null)
                {
                    foreach (string workbook in node.GroupWorkbooks)
                    {
                        selection.AddWorkbook(workbook);
                    }
                }
                else if (node.Key.StartsWith("workbook:", StringComparison.Ordinal))
                {
                    selection.AddWorkbook(node.Workbook!);
                }
                else if (node.Sheet != null)
                {
                    selection.AddSheet(node.Workbook!, node.Sheet);
                }
                else if (node.Table != null)
                {
                    foreach ((string workbook, string sheet) in SheetRefsOf(node.Table))
                    {
                        selection.AddSheet(workbook, sheet);
                    }
                }
            }

            _treeSelection = selection;
            if (_selectionBar == null || _selectionLabel == null)
            {
                return;
            }

            _selectionBar.style.display = selection.IsEmpty ? DisplayStyle.None : DisplayStyle.Flex;
            _selectionLabel.text = DescribeSelection();
            _selectionBar.tooltip = T("Reads these again; every other sheet comes from the last build on this machine.\n") + string.Join("\n", SelectionLines());
            bool canRun = _config != null && !UgsProject.IsRunning;
            _selectionBar.Query<ToolbarButton>().ForEach(button => button.SetEnabled(canRun));
        }

        private IEnumerable<string> SelectionLines()
        {
            return _treeSelection.Workbooks.OrderBy(name => name, StringComparer.Ordinal).Select(name => name + T(" (all sheets)"))
                .Concat(_treeSelection.Sheets.Select(entry => entry.Workbook + " / " + entry.Sheet));
        }

        private TableNode? _singleSelectedNode;

        /// <summary>"Item.xlsx", "Job · 2 sheets", "3 sheets", "Item.xlsx + 2 sheets": what the selection bar and the context menu call the selection.</summary>
        private string DescribeSelection()
        {
            if (_singleSelectedNode?.GroupWorkbooks != null)
            {
                return _singleSelectedNode.Label + " · " + Plural(_singleSelectedNode.GroupWorkbooks.Length, "workbook");
            }

            if (_singleSelectedNode != null)
            {
                int count = _treeSelection.Sheets.Count;
                return _singleSelectedNode.Label + (_treeSelection.Workbooks.Count == 0 && count > 1 ? " · " + Plural(count, "sheet") : "");
            }

            int workbooks = _treeSelection.Workbooks.Count;
            int sheets = _treeSelection.Sheets.Count;
            string workbookText = workbooks == 1 ? _treeSelection.Workbooks.First() : Plural(workbooks, "workbook");
            string sheetText = sheets == 1 ? _treeSelection.Sheets.First().Sheet : Plural(sheets, "sheet");
            return workbooks == 0 ? sheetText : sheets == 0 ? workbookText : workbookText + " + " + sheetText;
        }

        private void RunSelection(bool commit)
        {
            if (_config != null && !UgsProject.IsRunning && !_treeSelection.IsEmpty)
            {
                UgsProject.RunAsync(commit, _offline, _treeSelection);
            }
        }

        private static string DescribeSheet(RawTable table, string sheet)
        {
            bool addsColumns = table.Columns.Any(column => column.NameCell.Origin.Sheet == sheet) && table.Rows.Count > 0 && table.Rows[0].Length > 0 && table.Rows[0][0].Origin.Sheet != sheet
                && !table.Rows.Any(row => row.Length > 0 && row[0].Origin.Sheet == sheet);
            if (addsColumns)
            {
                return F("Adds columns to {0}, matched by key.", table.Name);
            }

            return sheet == table.Name || sheet.StartsWith(table.Name + ".", StringComparison.Ordinal) && table.Columns.Count > 0 && table.Columns[0].NameCell.Origin.Sheet == sheet
                ? F("Defines {0}.", table.Name)
                : F("Adds rows to {0}.", table.Name);
        }

        private static VisualElement MakeTreeRow()
        {
            var row = new VisualElement();
            row.AddToClassList("ugs-tree-row");
            var icon = new Image();
            icon.AddToClassList("ugs-icon");
            row.Add(icon);
            var name = new Label();
            name.AddToClassList("ugs-tree-row__name");
            row.Add(name);
            var note = new Label();
            note.AddToClassList("ugs-tree-row__note");
            row.Add(note);
            return row;
        }

        private string[] SheetsOf(RawTable table) => SheetRefsOf(table).Select(sheet => sheet.Sheet).ToArray();

        private (string Workbook, string Sheet)[] SheetRefsOf(RawTable table)
        {
            if (!_sheetsByTable.TryGetValue(table, out (string Workbook, string Sheet)[]? sheets))
            {
                var found = new List<(string Workbook, string Sheet)>();
                void Add(CellOrigin origin)
                {
                    if (!found.Contains((origin.Workbook, origin.Sheet)))
                    {
                        found.Add((origin.Workbook, origin.Sheet));
                    }
                }

                // Extra columns come from Table.Col.* sheets, extra rows from Table.Child sheets.
                foreach (RawColumn column in table.Columns)
                {
                    Add(column.NameCell.Origin);
                }

                foreach (Cell[] row in table.Rows)
                {
                    if (row.Length > 0)
                    {
                        Add(row[0].Origin);
                    }
                }

                sheets = found.ToArray();
                _sheetsByTable[table] = sheets;
            }

            return sheets;
        }

        // xlsx files show as Excel workbooks, CSV folders as folders, and anything else is a Google spreadsheet.
        private string WorkbookIcon(string workbook)
        {
            if (_origins.TryGetValue(workbook, out WorkbookOrigin? origin))
            {
                switch (origin.Type)
                {
                    case SourceType.Csv:
                        return "Folder Icon";
                    case SourceType.Google:
                        return GoogleSheetsIconName;
                    case SourceType.GoogleFolder:
                        // A Drive folder holds both: Google spreadsheets and uploaded .xlsx files.
                        return workbook.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) ? ExcelIconName : GoogleSheetsIconName;
                    default:
                        return ExcelIconName;
                }
            }

            if (workbook.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            {
                return ExcelIconName;
            }

            bool csv = _config != null && _config.Sources.Any(source => source.Type == SourceType.Csv
                && string.Equals(Path.GetFileName((source.Path ?? "").TrimEnd('/', '\\')), workbook, StringComparison.OrdinalIgnoreCase));
            return csv ? "Folder Icon" : GoogleSheetsIconName;
        }

        // A sheet's location is "Workbook / Sheet". A workbook is a file or a spreadsheet title, which cannot contain '/'.
        private static string WorkbookOf(RawTable table)
        {
            int separator = table.Location.IndexOf(" / ", StringComparison.Ordinal);
            return separator < 0 ? table.Location : table.Location.Substring(0, separator);
        }

        private static bool Contains(string text, string part) => text.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>
        /// Shows a table, or the part of it that came from one sheet: that sheet's rows, and its columns when it is a column
        /// extension (plus the key they are matched by).
        /// </summary>
        private VisualElement? _findBar;
        private ToolbarSearchField? _findField;

        // Find searches the key column unless the user switched to all columns. One choice for every table.
        private bool _findInAllColumns;

        // Matched text: a yellow background with dark text, readable on both editor themes.
        private const string MarkOpen = "<mark=#F2C94CE6><color=#1E1E1E>";
        private const string MarkClose = "</color></mark>";

        /// <summary>Shows the find bar of the table preview and puts the cursor in it. False when there is no table to search.</summary>
        public bool OpenFind()
        {
            if (_page != "Tables" || _findBar == null || _findField == null)
            {
                return false;
            }

            _findBar.style.display = DisplayStyle.Flex;
            _findField.Q<TextField>()?.Focus();
            return true;
        }

        private void CloseFind()
        {
            if (_findBar == null || _findField == null)
            {
                return;
            }

            _findField.value = "";
            _findBar.style.display = DisplayStyle.None;
        }

        private void ShowPreview(VisualElement container, RawTable table, string? sheet = null)
        {
            container.Clear();
            List<Cell[]> rows = table.Rows;
            var columns = Enumerable.Range(0, table.Columns.Count).ToList();
            string where = table.Location;
            if (sheet != null)
            {
                List<Cell[]> fromSheet = table.Rows.Where(row => row.Length > 0 && row[0].Origin.Sheet == sheet).ToList();
                List<int> ownColumns = columns.Where(index => table.Columns[index].NameCell.Origin.Sheet == sheet).ToList();
                bool columnExtension = fromSheet.Count == 0 && ownColumns.Count > 0;
                if (columnExtension)
                {
                    int key = table.FindKeyColumn();
                    if (key >= 0 && !ownColumns.Contains(key))
                    {
                        ownColumns.Insert(0, key);
                    }

                    columns = ownColumns;
                }
                else
                {
                    rows = fromSheet;
                    if (ownColumns.Count > 0 && ownColumns.Count < columns.Count && fromSheet.Count > 0)
                    {
                        // The defining sheet: hide the columns that other sheets added.
                        columns = ownColumns;
                    }
                }

                where = F("{0}  ·  part of {1}", WorkbookOf(table) + " / " + sheet, table.Name);
            }

            List<Cell[]> allRows = rows;
            string query = "";
            var captionRow = new VisualElement();
            captionRow.AddToClassList("ugs-caption");
            var caption = new Label(where + "  ·  " + Plural(rows.Count, "row") + "  ·  " + Plural(columns.Count, "column"));
            caption.AddToClassList("ugs-fill");
            captionRow.Add(caption);
            var shortcut = new Label(Application.platform == RuntimePlatform.OSXEditor ? T("⌘F to find") : T("Ctrl+F to find"));
            shortcut.AddToClassList("ugs-caption__hint");
            captionRow.Add(shortcut);
            container.Add(captionRow);

            // Find bar: search the key or every column, type, and only matching rows stay, with the matches highlighted.
            var findBar = new Toolbar();
            findBar.AddToClassList("ugs-find");
            findBar.style.display = DisplayStyle.None;
            int keyColumn = table.FindKeyColumn();
            bool hasKey = keyColumn >= 0 && columns.Contains(keyColumn);
            var keyToggle = new ToolbarToggle { text = T("Key"), tooltip = hasKey ? F("Search {0}", table.Columns[keyColumn].Name) : T("This table has no key") };
            var allToggle = new ToolbarToggle { text = T("All"), tooltip = T("Search every column") };
            keyToggle.AddToClassList("ugs-find__key");
            allToggle.AddToClassList("ugs-find__all");
            keyToggle.SetEnabled(hasKey);
            bool searchAll = _findInAllColumns || !hasKey;
            keyToggle.SetValueWithoutNotify(!searchAll);
            allToggle.SetValueWithoutNotify(searchAll);
            findBar.Add(keyToggle);
            findBar.Add(allToggle);
            var findField = new ToolbarSearchField { tooltip = T("Rows whose cell contains this text. Esc closes.") };
            findField.AddToClassList("ugs-find__field");
            findBar.Add(findField);
            var matches = new Label();
            matches.AddToClassList("ugs-find__count");
            findBar.Add(matches);
            container.Add(findBar);
            _findBar = findBar;
            _findField = findField;

            var grid = new MultiColumnListView
            {
                itemsSource = rows,
                fixedItemHeight = 20,
                showAlternatingRowBackgrounds = AlternatingRowBackground.ContentOnly,
                selectionType = SelectionType.None,
                sortingMode = ColumnSortingMode.None
            };
            grid.AddToClassList("ugs-fill");
            grid.AddToClassList("ugs-grid");
            // The row number is the sheet's own, so it matches the cell addresses on the Problems page. Rows that another
            // sheet added (Item.Rare) carry that sheet's short name, so "Rare 4" is not mistaken for row 4 of Item.
            // The column is as wide as the longest label needs, so 200,000 rows do not show as "199…".
            string definingSheet = table.Columns.Count > 0 ? table.Columns[0].NameCell.Origin.Sheet : "";
            string RowLabel(Cell[] row)
            {
                if (row.Length == 0)
                {
                    return "";
                }

                string number = (row[0].Origin.Row + 1).ToString();
                string from = row[0].Origin.Sheet;
                if (sheet != null || from == definingSheet)
                {
                    return number;
                }

                int dot = from.LastIndexOf('.');
                return (dot >= 0 ? from.Substring(dot + 1) : from) + " " + number;
            }

            int labelLength = rows.Count == 0 ? 1 : Math.Max(RowLabel(rows[rows.Count - 1]).Length, RowLabel(rows[0]).Length);
            foreach (string other in table.Rows.Select(row => row.Length > 0 ? row[0].Origin.Sheet : "").Distinct())
            {
                Cell[]? sample = rows.LastOrDefault(row => row.Length > 0 && row[0].Origin.Sheet == other);
                if (sample != null)
                {
                    labelLength = Math.Max(labelLength, RowLabel(sample).Length);
                }
            }

            grid.columns.Add(new Column
            {
                name = "row",
                title = T("Row"),
                width = Math.Max(44, 14 + 8 * labelLength),
                resizable = false,
                makeHeader = () => Header(T("Row"), "", ""),
                makeCell = () => Cell("ugs-cell--row"),
                bindCell = (element, row) =>
                {
                    var label = (Label)element;
                    label.text = RowLabel(rows[row]);
                    label.tooltip = rows[row].Length > 0 ? F("{0} row {1}", rows[row][0].Origin.Sheet, rows[row][0].Origin.Row + 1) : "";
                }
            });
            foreach (int index in columns)
            {
                int column = index;
                RawColumn raw = table.Columns[index];
                string typeClass = TypeClass(raw.TypeText);
                bool number = (typeClass == "ugs-type--int" || typeClass == "ugs-type--float") && !IsArray(raw.TypeText);
                grid.columns.Add(new Column
                {
                    name = "c" + index,
                    title = raw.Name,
                    width = 120,
                    makeHeader = () => Header(raw.Name, raw.TypeText, raw.Comment),
                    // Values keep the default text color; only the type under the heading is colored.
                    // Numbers line up on the right, as in a spreadsheet.
                    makeCell = () => Cell(number ? "ugs-cell--number" : ""),
                    bindCell = (element, row) =>
                    {
                        Cell cell = rows[row][column];
                        var label = (Label)element;
                        string text = cell.Text.Replace('\n', ' ');
                        bool searched = query.Length > 0 && (searchAll || column == keyColumn);
                        string marked = searched ? Highlight(text, query) : "";
                        // Rich text is only on while highlighting, so a cell that happens to contain "<b>" shows it as typed.
                        label.enableRichText = marked.Length > 0;
                        label.text = marked.Length > 0 ? marked : text;
                        label.tooltip = cell.Origin.A1;
                    }
                });
            }

            container.Add(grid);
            var noMatch = new Label(T("No rows match."));
            noMatch.AddToClassList("ugs-empty");
            noMatch.style.display = DisplayStyle.None;
            container.Add(noMatch);

            void ApplyFind()
            {
                query = (findField.value ?? "").Trim();
                if (query.Length == 0)
                {
                    rows = allRows;
                    matches.text = "";
                }
                else
                {
                    rows = allRows.Where(row => searchAll
                        ? columns.Any(index => index < row.Length && Contains(row[index].Text, query))
                        : keyColumn < row.Length && Contains(row[keyColumn].Text, query)).ToList();
                    matches.text = F("{0} of {1}", rows.Count.ToString("N0"), allRows.Count.ToString("N0"));
                }

                grid.itemsSource = rows;
                grid.Rebuild();
                noMatch.style.display = query.Length > 0 && rows.Count == 0 ? DisplayStyle.Flex : DisplayStyle.None;
                grid.style.display = noMatch.style.display == DisplayStyle.Flex ? DisplayStyle.None : DisplayStyle.Flex;
            }

            void SetScope(bool all)
            {
                searchAll = all || !hasKey;
                _findInAllColumns = searchAll;
                keyToggle.SetValueWithoutNotify(!searchAll);
                allToggle.SetValueWithoutNotify(searchAll);
                ApplyFind();
            }

            findField.RegisterValueChangedCallback(_ => ApplyFind());
            keyToggle.RegisterValueChangedCallback(change => SetScope(!change.newValue));
            allToggle.RegisterValueChangedCallback(change => SetScope(change.newValue));
        }

        /// <summary>
        /// Wraps every occurrence of <paramref name="query"/> (ignoring case) in a highlight. The rest is wrapped in noparse,
        /// so text that looks like a rich text tag is shown as written. Empty when there is no match.
        /// </summary>
        private static string Highlight(string text, string query)
        {
            int at = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
            {
                return "";
            }

            var builder = new System.Text.StringBuilder(text.Length + 64);
            int start = 0;
            while (at >= 0)
            {
                AppendPlain(builder, text.Substring(start, at - start));
                builder.Append(MarkOpen);
                AppendPlain(builder, text.Substring(at, query.Length));
                builder.Append(MarkClose);
                start = at + query.Length;
                at = text.IndexOf(query, start, StringComparison.OrdinalIgnoreCase);
            }

            AppendPlain(builder, text.Substring(start));
            return builder.ToString();
        }

        private static void AppendPlain(System.Text.StringBuilder builder, string text)
        {
            if (text.Length > 0)
            {
                // "</noparse>" inside the text would end the noparse block early; break it so it cannot.
                builder.Append("<noparse>").Append(text.Replace("</noparse>", "</no\u200Bparse>")).Append("</noparse>");
            }
        }

        /// <summary>
        /// The color group of a column type, used on the type under each heading and, more softly, on the values.
        /// Integers, decimals, booleans, dates, enums and references each get their own color; text keeps the default.
        /// </summary>
        private static string TypeClass(string typeText)
        {
            if (!TypeExpression.TryParse(typeText, out TypeExpression? expression, out _) || expression == null)
            {
                return "";
            }

            switch (expression.Kind)
            {
                case TypeExpressionKind.Enum:
                    return "ugs-type--enum";
                case TypeExpressionKind.Reference:
                    return "ugs-type--ref";
                case TypeExpressionKind.Mapping:
                    return "ugs-type--mapping";
            }

            if (!Scalars.TryFind(expression.Name, out ScalarKind kind))
            {
                // A bare name that is not a value type is an enum.
                return "ugs-type--enum";
            }

            switch (kind)
            {
                case ScalarKind.Bool:
                    return "ugs-type--bool";
                case ScalarKind.Float:
                case ScalarKind.Double:
                    return "ugs-type--float";
                case ScalarKind.String:
                    return "";
                case ScalarKind.DateTime:
                    return "ugs-type--date";
                default:
                    return "ugs-type--int";
            }
        }

        private static bool IsArray(string typeText) => TypeExpression.TryParse(typeText, out TypeExpression? expression, out _) && expression != null && expression.IsArray;

        private static VisualElement Header(string name, string type, string comment)
        {
            var header = new VisualElement { tooltip = comment };
            header.AddToClassList("ugs-header");
            header.Add(new Label(name));
            if (type.Length > 0)
            {
                var typeLabel = new Label(type);
                typeLabel.AddToClassList("ugs-header__type");
                string typeClass = TypeClass(type);
                if (typeClass.Length > 0)
                {
                    typeLabel.AddToClassList(typeClass);
                }
                header.Add(typeLabel);
            }

            return header;
        }

        private static Label Cell(string className)
        {
            var label = new Label();
            label.AddToClassList("ugs-cell");
            if (className.Length > 0)
            {
                label.AddToClassList(className);
            }

            return label;
        }

        private VisualElement BuildSettings()
        {
            UgsConfig config = _config!;
            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.AddToClassList("ugs-fill");
            var form = new VisualElement();
            form.AddToClassList("ugs-form");
            scroll.Add(form);

            Foldout sources = Section(T("Sources"), "sources", "Folder Icon", open: true);
            for (int index = 0; index < config.Sources.Count; index++)
            {
                sources.Add(BuildSource(config.Sources[index], index));
            }

            var add = new Button { text = T("Add source"), iconImage = Background.FromTexture2D(Icon("Toolbar Plus")) };
            add.AddToClassList("ugs-add");
            add.clicked += () =>
            {
                var menu = new GenericMenu();
                menu.AddItem(new GUIContent(T("Excel workbook")), false, () => AddSource(new SourceConfig { Type = SourceType.Xlsx, Path = "Sheets" }));
                menu.AddItem(new GUIContent(T("CSV folder")), false, () => AddSource(new SourceConfig { Type = SourceType.Csv, Path = "Sheets/Csv" }));
                menu.AddItem(new GUIContent("Google Sheets"), false, () => AddSource(new SourceConfig { Type = SourceType.Google, SpreadsheetId = "" }));
                // A folder can only be listed with credentials, so it starts on an API key rather than on "public".
                menu.AddItem(new GUIContent(T("Google Drive folder")), false, () => AddSource(new SourceConfig { Type = SourceType.GoogleFolder, FolderId = "", Auth = { Type = GoogleAuthType.ApiKey } }));
                menu.DropDown(add.worldBound);
            };
            sources.Add(add);
            form.Add(sources);

            Foldout output = Section(T("Output"), "output", "Import", open: true);
            output.Add(Subheading(T("Folders")));
            output.Add(PathField(T("Code"), config.Output.Code, value => config.Output.Code = value, T("Generated .g.cs files. Keep it under Assets so Unity compiles them."), Browse.Folder));
            output.Add(PathField(T("Data"), config.Output.Data, value => config.Output.Data = value, T(".bytes files and the manifest. Under StreamingAssets they stay plain files that can be watched and patched."), Browse.Folder));
            output.Add(PathField(T("Transformed CSV"), config.Output.Csv ?? "", value => config.Output.Csv = Optional(value), T("Optional. Each table as CSV after the sheet rules, for review."), Browse.Folder));
            output.Add(Check(T("Write schema file"), config.Output.Schema, value => config.Output.Schema = value, T("tables.schema.json next to the data, for tools without generated code.")));
            output.Add(Subheading(T("Generated code")));
            output.Add(Text(T("Namespace"), config.Codegen.Namespace, value => config.Codegen.Namespace = value));
            output.Add(Text(T("Tables class"), config.Codegen.TablesClass, value => config.Codegen.TablesClass = value, T("The class that holds every table: Load, LoadAsync, Watch.")));
            output.Add(Choice(T("Serializer"), Serializers, config.Codegen.Serializer, value => config.Codegen.Serializer = value,
                T("memorypack: fastest to load, C# only.\nmessagepack: readable from other languages.")));
            form.Add(output);

            Foldout build = Section(T("Sheet and build options"), "build", "Settings", open: false);
            List<string> layouts = HeaderLayouts.Select(layout => layout.Label).ToList();
            string current = string.Join(", ", config.Sheet.Header);
            if (!layouts.Contains(current))
            {
                layouts.Add(current);
            }

            build.Add(Choice(T("Header rows"), layouts, current, value =>
            {
                int layout = Array.FindIndex(HeaderLayouts, item => item.Label == value);
                if (layout >= 0)
                {
                    config.Sheet.Header = HeaderLayouts[layout].Rows.ToList();
                }
            }, T("What the header rows of a table sheet hold, top to bottom. Data starts on the next row.")));
            build.Add(Text(T("Array separator"), config.Sheet.ArraySeparator.ToString(), value =>
            {
                if (value.Length > 0)
                {
                    config.Sheet.ArraySeparator = value[0];
                }
            }, T("Separates array elements in one cell. [a, b] is accepted too.")));
            build.Add(Choice(T("CSV reader"), CsvBackendNames, config.CsvBackend, value => config.CsvBackend = value,
                UgsProject.IsSepAvailable ? T("auto and builtin use the built-in reader; sep uses Sep. Same results.") : T("auto and builtin use the built-in reader. sep needs Unity 7 (.NET 8+).")));
            build.Add(Check(T("Parallel"), config.Parallel, value => config.Parallel = value, T("Convert and encode tables on several threads. The output is the same either way.")));
            build.Add(Text(T("Build label"), config.BuildLabel ?? "", value => config.BuildLabel = Optional(value), T("Optional text written to the manifest, such as a version.")));
            form.Add(build);

            form.Add(BuildSigning(config));
            form.Add(BuildEditorSettings());

            var footer = Buttons(
                new Button(() => EditorUtility.OpenWithDefaultApp(UgsProject.ConfigPath)) { text = T("Open ugs.json") },
                new Button(() => EditorUtility.RevealInFinder(UgsProject.ConfigPath)) { text = T("Reveal") });
            footer.AddToClassList("ugs-footer");
            form.Add(footer);
            return scroll;
        }

        private VisualElement BuildSource(SourceConfig source, int index)
        {
            var card = new VisualElement();
            card.AddToClassList("ugs-card");
            var head = new VisualElement();
            head.AddToClassList("ugs-card__head");
            var icon = new Image
            {
                image = source.Type switch
                {
                    SourceType.Google => TreeIcon(GoogleSheetsIconName),
                    SourceType.GoogleFolder => Icon("CloudConnect"),
                    SourceType.Csv => Icon("Folder Icon"),
                    _ => TreeIcon(ExcelIconName)
                }
            };
            icon.AddToClassList("ugs-icon");
            head.Add(icon);
            var title = new Label(source.Type switch
            {
                SourceType.Google => "Google Sheets",
                SourceType.GoogleFolder => T("Google Drive folder"),
                SourceType.Csv => T("CSV folder"),
                _ => T("Excel workbook")
            });
            title.AddToClassList("ugs-card__title");
            head.Add(title);
            var summary = new Label(source.Type switch
            {
                SourceType.Google => source.Name ?? source.SpreadsheetId ?? "",
                SourceType.GoogleFolder => source.Name ?? source.FolderId ?? "",
                _ => source.Path ?? ""
            });
            summary.AddToClassList("ugs-card__summary");
            head.Add(summary);
            head.Add(IconButton("Toolbar Minus", T("Remove this source"), () =>
            {
                _config!.Sources.RemoveAt(index);
                Save();
                ShowPage();
            }));
            card.Add(head);

            if (!source.IsRemote)
            {
                bool csv = source.Type == SourceType.Csv;
                card.Add(PathField(csv ? T("Folder") : T("File or folder"), source.Path ?? "", value => source.Path = value,
                    csv ? T("A folder of .csv files. Each file is a sheet.") : T("An .xlsx file, or a folder whose .xlsx files are all read."),
                    csv ? Browse.Folder : Browse.WorkbookOrFolder));
                string resolved = _config!.Resolve(source.Path ?? "");
                if (!Directory.Exists(resolved) && !File.Exists(resolved))
                {
                    card.Add(new HelpBox(T("Not found: ") + resolved, HelpBoxMessageType.Warning));
                }

                return card;
            }

            bool folder = source.Type == SourceType.GoogleFolder;
            if (folder)
            {
                card.Add(Text(T("Folder"), source.FolderId ?? "", value => source.FolderId = SourceConfig.ExtractGoogleId(value),
                    T("The id from drive.google.com/drive/folders/<id>, or paste the whole URL. Every Google spreadsheet and .xlsx file directly in the folder is read; names starting with # are skipped.")));
            }
            else
            {
                card.Add(Text(T("Spreadsheet"), source.SpreadsheetId ?? "", value => source.SpreadsheetId = ExtractSpreadsheetId(value), T("The id from the URL, or paste the whole URL.")));
            }

            card.Add(Choice(T("Access"), AuthNames, AuthNames[(int)source.Auth.Type], value =>
            {
                source.Auth.Type = (GoogleAuthType)AuthNames.IndexOf(value);
                ShowPage();
            }, T("Public link and API key read spreadsheets shared as 'anyone with the link'. A service account reads private ones shared with its e-mail.")));
            switch (source.Auth.Type)
            {
                case GoogleAuthType.ApiKey:
                    card.Add(Text(T("Key variable"), source.Auth.ApiKeyEnv ?? "", value => source.Auth.ApiKeyEnv = Optional(value), T("Environment variable that holds the API key.")));
                    card.Add(Text(T("Key"), source.Auth.ApiKey ?? "", value => source.Auth.ApiKey = Optional(value), T("Or the key itself. Saved to ugs.json as plain text."), password: true));
                    break;
                case GoogleAuthType.ServiceAccount:
                    card.Add(PathField(T("Key file"), source.Auth.KeyFile ?? "", value => source.Auth.KeyFile = Optional(value), T("The service account's JSON key. Keep it out of version control."), Browse.File));
                    break;
                case GoogleAuthType.AccessToken:
                    card.Add(Text(T("Token variable"), source.Auth.AccessTokenEnv ?? "", value => source.Auth.AccessTokenEnv = Optional(value), T("Environment variable that holds an OAuth access token.")));
                    break;
            }

            if (folder)
            {
                if (source.Auth.Type == GoogleAuthType.Public)
                {
                    card.Add(new HelpBox(T("Google lists a folder only through the Drive API: choose an API key (for a link-shared folder), a service account or an access token."), HelpBoxMessageType.Warning));
                }

                card.Add(PathField(T("Snapshot folder"), source.Snapshot ?? "", value => source.Snapshot = Optional(value), T("Optional. Each workbook is saved here as a folder of CSV, for diffs and offline builds."), Browse.Folder));
                return card;
            }

            TextField tabs = Text(T("Tabs"), FormatTabs(source.Sheets), _ => { }, T("Optional. Name:gid pairs, e.g. Item:0, Monster:123456. Empty reads every tab."));
            tabs.RegisterValueChangedCallback(change =>
            {
                List<GoogleSheetRef>? parsed = ParseTabs(change.newValue);
                if (parsed == null)
                {
                    // Keep what was there rather than silently dropping the entries that did not parse.
                    tabs.SetValueWithoutNotify(FormatTabs(source.Sheets));
                    Debug.LogWarning("[UGS] Tabs must be Name:gid pairs separated by commas, e.g. Item:0, Monster:123456.");
                    return;
                }

                source.Sheets = parsed;
                Save();
            });
            card.Add(tabs);
            card.Add(PathField(T("Snapshot folder"), source.Snapshot ?? "", value => source.Snapshot = Optional(value), T("Optional. The fetched CSV is saved here, for diffs and offline builds."), Browse.Folder));
            return card;
        }

        private void AddSource(SourceConfig source)
        {
            _config!.Sources.Add(source);
            Save();
            ShowPage();
        }

        // Settings of this window for this user, not of the project: kept in UserSettings, not in ugs.json.
        private Foldout BuildEditorSettings()
        {
            Foldout editor = Section(T("Editor"), "editor", "d_UnityEditor.SceneHierarchyWindow", open: true);
            string systemName = Application.systemLanguage == SystemLanguage.Korean ? "한국어" : "English";
            var names = new List<string> { F("System ({0})", systemName), "English", "한국어" };
            // Not through Choice(): that saves ugs.json, and the language is not part of it.
            var language = new DropdownField(T("Language"), names, (int)UgsText.Language) { tooltip = T("The language of this window. Kept for you only, in UserSettings.") };
            language.AddToClassList(DropdownField.alignedFieldUssClassName);
            language.RegisterValueChangedCallback(change =>
            {
                UgsText.Language = (UgsLanguage)names.IndexOf(change.newValue);
                // Every label is made in the language of the moment, so the window is built again.
                rootVisualElement.schedule.Execute(CreateGUI);
            });
            editor.Add(language);
            return editor;
        }

        private Foldout BuildSigning(UgsConfig config)
        {
            bool signed = !string.IsNullOrEmpty(config.Signing.PublicKey);
            Foldout signing = Section(signed ? T("Signing (signed builds)") : T("Signing (unsigned builds)"), "signing", signed ? "LockIcon-On" : "LockIcon", open: false);
            if (signed)
            {
                var key = new TextField(T("Public key")) { value = config.Signing.PublicKey, isReadOnly = true, tooltip = T("Baked into the generated code. Safe to ship.") };
                key.AddToClassList(TextField.alignedFieldUssClassName);
                signing.Add(key);
            }

            signing.Add(PathField(T("Private key file"), config.Signing.PrivateKeyFile ?? "", value => config.Signing.PrivateKeyFile = Optional(value), T("Signs the manifest. Keep it out of version control."), Browse.File));
            signing.Add(Text(T("Private key variable"), config.Signing.PrivateKeyEnv ?? "", value => config.Signing.PrivateKeyEnv = Optional(value), T("Environment variable with the private key text, for CI. Wins over the file.")));

            var buttons = new List<Button>();
            if (signed)
            {
                buttons.Add(new Button(() =>
                {
                    config.Signing.PublicKey = null;
                    config.Signing.PrivateKeyFile = null;
                    config.Signing.PrivateKeyEnv = null;
                    Save();
                    ShowPage();
                }) { text = T("Stop signing") });
            }
            else
            {
                buttons.Add(new Button(CreateKeyPair) { text = T("Create key pair") });
            }

            buttons.Add(new Button(VerifyData) { text = T("Verify data folder"), tooltip = T("Check every data file against the manifest, and the signature if there is one.") });
            signing.Add(Buttons(buttons.ToArray()));
            if (_verifyReport != null)
            {
                Label report = Selectable("ugs-report");
                report.text = _verifyReport;
                signing.Add(report);
            }

            return signing;
        }

        private void CreateKeyPair()
        {
            UgsConfig config = _config!;
            string path = Path.Combine(UgsProject.RootDirectory, "ugs.private.key");
            if (File.Exists(path))
            {
                EditorUtility.DisplayDialog("UGS", "ugs.private.key already exists in the project root.\n\nReplacing a key invalidates every client built with the old one. Delete the file first if that is intended.", "OK");
                return;
            }

            (string privateKey, string publicKey) = SigningKeys.Generate();
            File.WriteAllText(path, privateKey);
            config.Signing.PublicKey = publicKey;
            config.Signing.PrivateKeyFile = "ugs.private.key";
            Save();

            string ignorePath = Path.Combine(UgsProject.RootDirectory, ".gitignore");
            bool ignored = File.Exists(ignorePath) && File.ReadAllText(ignorePath).Contains("ugs.private.key");
            if (!ignored && EditorUtility.DisplayDialog("UGS", "The private key was written to ugs.private.key. It must not be committed.\n\nAdd it to .gitignore?", T("Add"), T("Not now")))
            {
                File.AppendAllText(ignorePath, (File.Exists(ignorePath) && !File.ReadAllText(ignorePath).EndsWith("\n") ? "\n" : "") + "ugs.private.key\n");
            }

            ShowPage();
        }

        private void VerifyData()
        {
            UgsConfig config = _config!;
            string directory = config.Resolve(config.Output.Data);
            var lines = new List<string>();
            try
            {
                TableManifest manifest = TableManifest.Parse(File.ReadAllText(Path.Combine(directory, config.Output.ManifestFile)));
                foreach (TableManifestEntry entry in manifest.Tables)
                {
                    string path = Path.Combine(directory, entry.File);
                    bool ok = File.Exists(path);
                    if (ok)
                    {
                        byte[] bytes = File.ReadAllBytes(path);
                        ok = bytes.Length == entry.Bytes && TableHash.Compute(bytes) == entry.Hash;
                    }

                    if (!ok)
                    {
                        lines.Add(T("Does not match the manifest: ") + entry.File);
                    }
                }

                if (!string.IsNullOrEmpty(config.Signing.PublicKey) && !(manifest.IsSigned && TableSignature.Verify(manifest, config.Signing.PublicKey!)))
                {
                    lines.Add(manifest.IsSigned ? T("The signature does not match the public key.") : T("The manifest is not signed."));
                }

                if (lines.Count == 0)
                {
                    lines.Add(F(manifest.IsSigned && !string.IsNullOrEmpty(config.Signing.PublicKey) ? "{0} files match the manifest; the signature is valid." : "{0} files match the manifest.", manifest.Tables.Count));
                }
            }
            catch (Exception exception) when (exception is IOException || exception is TableLoadException)
            {
                lines.Add(exception.Message);
            }

            _verifyReport = string.Join("\n", lines);
            ShowPage();
        }

        // ------------------------------------------------------------ fields

        private enum Browse
        {
            Folder,
            File,
            WorkbookOrFolder
        }

        private static Foldout Section(string title, string key, string icon, bool open)
        {
            string stateKey = "UGS.Window.Section." + key;
            var foldout = new Foldout { text = title, value = SessionState.GetBool(stateKey, open) };
            foldout.AddToClassList("ugs-section");
            // The header looks like an inspector component header: an icon before the title, a bar behind it.
            VisualElement? input = foldout.Q(className: Foldout.inputUssClassName);
            if (input != null)
            {
                var image = new Image { image = Icon(icon) };
                image.AddToClassList("ugs-icon");
                image.AddToClassList("ugs-section__icon");
                input.Insert(1, image);
            }

            foldout.RegisterValueChangedCallback(change =>
            {
                if (change.target == foldout)
                {
                    SessionState.SetBool(stateKey, change.newValue);
                }
            });
            return foldout;
        }

        private static Label Subheading(string text)
        {
            var label = new Label(text);
            label.AddToClassList("ugs-subheading");
            return label;
        }

        private const string TableIconName = "ugs:table";

        private static Texture2D? _tableIcon;
        private static bool _tableIconProSkin;

        /// <summary>
        /// A small table glyph: frame, filled header row, grid lines. Drawn in code in the editor's text color, because no
        /// built-in icon looks like a plain table and an icon picked by name can be missing on one of the two skins.
        /// </summary>
        private static Texture2D TableIcon()
        {
            if (_tableIcon != null && _tableIconProSkin == EditorGUIUtility.isProSkin)
            {
                return _tableIcon;
            }

            const int size = 32;
            Color ink = EditorGUIUtility.isProSkin ? new Color(0.77f, 0.77f, 0.77f, 1f) : new Color(0.33f, 0.33f, 0.33f, 1f);
            Color clear = new Color(ink.r, ink.g, ink.b, 0f);
            var pixels = new Color[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // Texture rows run bottom to top; "top" is measured from the upper edge.
                    int top = size - 1 - y;
                    bool inside = x >= 3 && x <= 28 && top >= 5 && top <= 26;
                    bool frame = inside && (x <= 4 || x >= 27 || top <= 6 || top >= 25);
                    bool header = inside && top <= 11;
                    bool rowLine = inside && (top == 18 || top == 19);
                    bool columnLine = inside && (x == 12 || x == 13);
                    pixels[y * size + x] = frame || header || rowLine || columnLine ? ink : clear;
                }
            }

            _tableIcon = new Texture2D(size, size, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear };
            _tableIcon.SetPixels(pixels);
            _tableIcon.Apply();
            _tableIconProSkin = EditorGUIUtility.isProSkin;
            return _tableIcon;
        }

        private const string ExcelIconName = "ugs:excel";
        private const string GoogleSheetsIconName = "ugs:google-sheets";

        private static readonly Dictionary<string, Texture2D> DrawnIcons = new Dictionary<string, Texture2D>();

        private static Texture2D TreeIcon(string name)
        {
            switch (name)
            {
                case TableIconName:
                    return TableIcon();
                case ExcelIconName:
                    return DrawnIcon(name, ExcelPixel);
                case GoogleSheetsIconName:
                    return DrawnIcon(name, GoogleSheetsPixel);
                default:
                    return Icon(name);
            }
        }

        private static readonly Color ExcelDark = new Color32(0x10, 0x7C, 0x41, 0xFF);
        private static readonly Color ExcelMid = new Color32(0x21, 0xA3, 0x66, 0xFF);
        private static readonly Color ExcelLight = new Color32(0x33, 0xC4, 0x81, 0xFF);
        private static readonly Color SheetsGreen = new Color32(0x0F, 0x9D, 0x58, 0xFF);
        private static readonly Color SheetsFold = new Color32(0x87, 0xCE, 0xAC, 0xFF);

        // The Excel file look: a green workbook with grid bands, and the dark green tile with a white X in front of it.
        // Coordinates are on a 32 by 32 grid, y down.
        private static Color? ExcelPixel(float x, float y)
        {
            if (InRoundedRect(x, y, 3f, 6f, 19f, 26f, 2f))
            {
                // The X: two strokes across the tile.
                float u = (x - 6.5f) / 9f, v = (y - 9.5f) / 13f;
                bool inside = u >= 0f && u <= 1f && v >= 0f && v <= 1f;
                bool stroke = inside && (Mathf.Abs(u - v) < 0.2f || Mathf.Abs(u + v - 1f) < 0.2f);
                return stroke ? Color.white : ExcelDark;
            }

            if (InRoundedRect(x, y, 10f, 3f, 29.5f, 29f, 2f))
            {
                // Four bands, lighter towards the top, split by a column line.
                if (x > 19.5f && x < 20.5f)
                {
                    return ExcelDark;
                }

                return y < 9.5f ? ExcelLight : y < 16f ? ExcelMid : y < 22.5f ? ExcelDark : new Color32(0x18, 0x5C, 0x37, 0xFF);
            }

            return null;
        }

        // The Google Sheets look: a green page with a folded corner and a white grid.
        private static Color? GoogleSheetsPixel(float x, float y)
        {
            const float left = 7f, top = 2f, right = 25f, bottom = 30f, fold = 7f;
            if (x < left || x > right || y < top || y > bottom)
            {
                return null;
            }

            float intoFold = (x - (right - fold)) + (top + fold - y);
            if (intoFold > fold)
            {
                return null;
            }

            if (x > right - fold && y < top + fold)
            {
                return SheetsFold;
            }

            bool grid = x >= 11f && x <= 21f && y >= 14f && y <= 25f;
            bool line = grid && (x < 12.5f || x > 19.5f || (x > 15.25f && x < 16.75f) || y < 15.5f || y > 23.5f || (y > 18.75f && y < 20.25f));
            return line ? Color.white : SheetsGreen;
        }

        private static bool InRoundedRect(float x, float y, float left, float top, float right, float bottom, float radius)
        {
            if (x < left || x > right || y < top || y > bottom)
            {
                return false;
            }

            float cx = Mathf.Clamp(x, left + radius, right - radius);
            float cy = Mathf.Clamp(y, top + radius, bottom - radius);
            return (x - cx) * (x - cx) + (y - cy) * (y - cy) <= radius * radius;
        }

        // Paints a 32 by 32 icon with 4 by 4 samples per pixel, so the diagonals and corners are smooth at 16 points.
        private static Texture2D DrawnIcon(string name, Func<float, float, Color?> paint)
        {
            if (DrawnIcons.TryGetValue(name, out Texture2D? cached) && cached != null)
            {
                return cached;
            }

            const int size = 32, samples = 4;
            var pixels = new Color[size * size];
            for (int py = 0; py < size; py++)
            {
                for (int px = 0; px < size; px++)
                {
                    Color sum = Color.clear;
                    for (int sy = 0; sy < samples; sy++)
                    {
                        for (int sx = 0; sx < samples; sx++)
                        {
                            Color? color = paint(px + (sx + 0.5f) / samples, py + (sy + 0.5f) / samples);
                            if (color.HasValue)
                            {
                                // Premultiplied, so edges blend towards transparent instead of towards black.
                                sum += new Color(color.Value.r, color.Value.g, color.Value.b, 1f);
                            }
                        }
                    }

                    float alpha = sum.a / (samples * samples);
                    // Texture rows run bottom to top.
                    pixels[(size - 1 - py) * size + px] = alpha <= 0f ? Color.clear : new Color(sum.r / sum.a, sum.g / sum.a, sum.b / sum.a, alpha);
                }
            }

            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear };
            texture.SetPixels(pixels);
            texture.Apply();
            texture.name = name;
            DrawnIcons[name] = texture;
            return texture;
        }

        private static Texture2D Icon(string name)
        {
            // IconContent picks the dark-skin variant itself when there is one.
            return name.Length == 0 ? null! : (Texture2D)EditorGUIUtility.IconContent(name).image;
        }

        private static Button IconButton(string icon, string tooltip, Action clicked)
        {
            var button = new Button(clicked) { iconImage = Background.FromTexture2D(Icon(icon)), tooltip = tooltip };
            button.AddToClassList("ugs-icon-button");
            return button;
        }

        private TextField Text(string label, string value, Action<string> apply, string tooltip = "", bool password = false)
        {
            var field = new TextField(label) { value = value, isDelayed = true, tooltip = tooltip, isPasswordField = password };
            field.AddToClassList(TextField.alignedFieldUssClassName);
            field.RegisterValueChangedCallback(change =>
            {
                apply(change.newValue.Trim());
                Save();
            });
            return field;
        }

        private VisualElement PathField(string label, string value, Action<string> apply, string tooltip, Browse browse)
        {
            var row = new VisualElement();
            row.AddToClassList("ugs-path");
            TextField field = Text(label, value, apply, tooltip);
            row.Add(field);
            var button = new Button { iconImage = Background.FromTexture2D(Icon("Folder Icon")), tooltip = T("Browse") };
            button.AddToClassList("ugs-icon-button");
            button.clicked += () =>
            {
                string start = UgsProject.RootDirectory;
                if (browse == Browse.WorkbookOrFolder)
                {
                    var menu = new GenericMenu();
                    menu.AddItem(new GUIContent(T("File…")), false, () => Pick(EditorUtility.OpenFilePanel(label, start, "xlsx")));
                    menu.AddItem(new GUIContent(T("Folder…")), false, () => Pick(EditorUtility.OpenFolderPanel(label, start, "")));
                    menu.DropDown(button.worldBound);
                    return;
                }

                Pick(browse == Browse.File ? EditorUtility.OpenFilePanel(label, start, "") : EditorUtility.OpenFolderPanel(label, start, ""));
            };
            row.Add(button);
            return row;

            void Pick(string picked)
            {
                if (!string.IsNullOrEmpty(picked))
                {
                    field.value = ToProjectRelative(picked);
                }
            }
        }

        private Toggle Check(string label, bool value, Action<bool> apply, string tooltip)
        {
            var toggle = new Toggle(label) { value = value, tooltip = tooltip };
            toggle.AddToClassList(Toggle.alignedFieldUssClassName);
            toggle.RegisterValueChangedCallback(change =>
            {
                apply(change.newValue);
                Save();
            });
            return toggle;
        }

        private DropdownField Choice(string label, List<string> choices, string value, Action<string> apply, string tooltip = "")
        {
            var field = new DropdownField(label, choices, Math.Max(0, choices.IndexOf(value))) { tooltip = tooltip };
            field.AddToClassList(DropdownField.alignedFieldUssClassName);
            field.RegisterValueChangedCallback(change =>
            {
                apply(change.newValue);
                Save();
            });
            return field;
        }

        private static VisualElement Buttons(params Button[] buttons)
        {
            var row = new VisualElement();
            row.AddToClassList("ugs-buttons");
            foreach (Button button in buttons)
            {
                row.Add(button);
            }

            return row;
        }

        private static Label Selectable(string className)
        {
            var label = new Label();
            label.AddToClassList(className);
            label.selection.isSelectable = true;
            return label;
        }

        private static VisualElement Empty(string text)
        {
            var empty = new Label(text);
            empty.AddToClassList("ugs-empty");
            return empty;
        }

        // ------------------------------------------------------------ helpers

        private static string Plural(int count, string noun) => UgsText.Count(count, noun);

        private static string? Optional(string value) => value.Length == 0 ? null : value;

        private static string ToProjectRelative(string path)
        {
            string root = UgsProject.RootDirectory.Replace('\\', '/').TrimEnd('/') + "/";
            string normalized = path.Replace('\\', '/');
            return normalized.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? normalized.Substring(root.Length) : normalized;
        }

        private static string ExtractSpreadsheetId(string text)
        {
            const string marker = "/spreadsheets/d/";
            int start = text.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0)
            {
                return text.Trim();
            }

            start += marker.Length;
            int end = text.IndexOfAny(new[] { '/', '?', '#' }, start);
            return end < 0 ? text.Substring(start) : text.Substring(start, end - start);
        }

        private static string FormatTabs(List<GoogleSheetRef> tabs) => string.Join(", ", tabs.Select(tab => tab.Name + ":" + tab.Gid));

        /// <summary>Null when any entry is not a Name:gid pair.</summary>
        private static List<GoogleSheetRef>? ParseTabs(string text)
        {
            var tabs = new List<GoogleSheetRef>();
            foreach (string part in text.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (part.Trim().Length == 0)
                {
                    continue;
                }

                int colon = part.LastIndexOf(':');
                if (colon <= 0 || part.Substring(0, colon).Trim().Length == 0 || !long.TryParse(part.Substring(colon + 1).Trim(), out long gid))
                {
                    return null;
                }

                tabs.Add(new GoogleSheetRef(part.Substring(0, colon).Trim(), gid));
            }

            return tabs;
        }

        private static bool IsSerializerInstalled(string serializer)
        {
            string assemblyName = serializer == "messagepack" ? "MessagePack" : "MemoryPack.Core";
            return AppDomain.CurrentDomain.GetAssemblies().Any(assembly => assembly.GetName().Name == assemblyName);
        }

        private static string SerializerPackage(string serializer) => serializer == "messagepack" ? "MessagePack" : "MemoryPack";
    }
}
