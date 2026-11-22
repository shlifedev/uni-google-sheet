#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HolySheet.Core;
using HolySheet.Core.Config;
using HolySheet.Core.Output;
using HolySheet.Core.Schema;
using HolySheet.Core.Sources;
using HolySheet.Core.Transform;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using static HolySheet.Editor.HolySheetText;

namespace HolySheet.Editor
{
    /// <summary>
    /// Tools > HolySheet > Tables. Build, look at the tables, read the problems, change holysheet.json.
    /// Everything runs through <see cref="HolySheetProject"/>, so the window can do nothing the command line cannot.
    /// </summary>
    public sealed class HolySheetWindow : EditorWindow
    {
        private const string StyleSheetPath = "Packages/com.lumera.holysheet/Editor/HolySheetWindow.uss";

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
        private static List<string> AuthNames => new List<string> { T("Public link"), T("API key"), T("Service account"), T("Access token"), T("Custom provider"), T("Command") };

        private HolySheetConfig? _config;
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

        [MenuItem("Tools/HolySheet/Tables", priority = 0)]
        public static void Open()
        {
            HolySheetWindow window = GetWindow<HolySheetWindow>();
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
            // Set here, not in Open: a window that Unity restores from a saved layout never goes through Open, and would
            // keep whatever title the layout was saved with.
            titleContent = new GUIContent("HolySheet");
            HolySheetProject.RunStarted += OnRunStarted;
            HolySheetProject.RunFinished += OnRunFinished;
        }

        private void OnDisable()
        {
            HolySheetProject.RunStarted -= OnRunStarted;
            HolySheetProject.RunFinished -= OnRunFinished;
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

            root.AddToClassList("holysheet");
            root.AddToClassList(EditorGUIUtility.isProSkin ? "holysheet--dark" : "holysheet--light");
            // Unity targets the panel root when no control has focus. Follow the window when it is docked or moved.
            root.RegisterCallback<AttachToPanelEvent>(evt =>
                evt.destinationPanel.visualTree.RegisterCallback<KeyDownEvent>(OnRefreshKey, TrickleDown.TrickleDown));
            root.RegisterCallback<DetachFromPanelEvent>(evt =>
                evt.originPanel.visualTree.UnregisterCallback<KeyDownEvent>(OnRefreshKey, TrickleDown.TrickleDown));
            root.panel?.visualTree.RegisterCallback<KeyDownEvent>(OnRefreshKey, TrickleDown.TrickleDown);
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
            _banner.AddToClassList("holysheet-banner");
            root.Add(_banner);
            _content = new VisualElement();
            _content.AddToClassList("holysheet-fill");
            root.Add(_content);

            ReloadConfig();
            CheckIfNothingWasRead();
            // Errors from a run before the window opened are shown too, not only those that arrive while it is open.
            HolySheetRunResult? last = HolySheetProject.LastResult;
            _problemsOpen |= last != null && (last.Failure != null || last.Build.Diagnostics.HasErrors);
            Refresh();
        }

        // After a restart or script reload, restore the preview from local files and snapshots when possible.
        // Reading from Google waits for an explicit refresh or build.
        private void CheckIfNothingWasRead()
        {
            if (_config == null || HolySheetProject.LastResult != null || HolySheetProject.IsRunning)
            {
                return;
            }

            if (_config.CanPreviewOffline())
            {
                HolySheetProject.RunAsync(commit: false, offline: true);
            }
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
                tab.AddToClassList("holysheet-tab");
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
            _statusIcon.AddToClassList("holysheet-status-icon");
            toolbar.Add(_statusIcon);
            _statusText = new Label();
            _statusText.AddToClassList("holysheet-status");
            // The status says how many problems there are; clicking it shows them.
            _statusText.RegisterCallback<ClickEvent>(_ => SelectPage("Problems"));
            _statusIcon.RegisterCallback<ClickEvent>(_ => SelectPage("Problems"));
            toolbar.Add(_statusText);

            var offline = new ToolbarToggle { text = T("Offline"), value = _offline, tooltip = T("Build Google sources from the saved snapshot instead of the network.") };
            offline.RegisterValueChangedCallback(change => _offline = change.newValue);
            toolbar.Add(offline);

            _checkButton = new ToolbarButton(() => Run(commit: false)) { text = T("Check"), tooltip = T("Read and validate the sheets. Writes nothing.") };
            toolbar.Add(_checkButton);
            _buildButton = new ToolbarButton(() => Run(commit: true)) { text = T("Build all"), tooltip = T("Write generated code and data (Ctrl+Shift+Alt+B).") };
            _buildButton.AddToClassList("holysheet-build");
            toolbar.Add(_buildButton);
            _selectionBuild = new ToolbarButton(() => RunSelection(commit: true));
            _selectionBuild.AddToClassList("holysheet-selection__build");
            toolbar.Add(_selectionBuild);
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

            bool canRun = _config != null && !HolySheetProject.IsRunning;
            _checkButton.SetEnabled(canRun);
            _buildButton.SetEnabled(canRun);
            RefreshSelectionBuild();

            HolySheetRunResult? result = _config == null ? null : HolySheetProject.LastResult;
            string icon = "";
            string text = "";
            if (HolySheetProject.IsRunning)
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
            _statusText.tooltip = result == null || HolySheetProject.IsRunning ? "" : HolySheetProject.Summarize(result);
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

        private void OnRefreshKey(KeyDownEvent evt)
        {
            if (focusedWindow != this || evt.altKey || evt.shiftKey)
            {
                return;
            }

            bool refreshKey = (evt.keyCode == KeyCode.F5 && !evt.ctrlKey && !evt.commandKey)
                || (evt.keyCode == KeyCode.R && evt.ctrlKey != evt.commandKey);
            if (refreshKey)
            {
                evt.StopPropagation();
                Reload();
            }
        }

        private void Reload()
        {
            if (HolySheetProject.IsRunning)
            {
                return;
            }

            ReloadConfig();
            if (_config == null)
            {
                Refresh();
                return;
            }

            HolySheetProject.RefreshAsync(_offline);
        }

        private void ReloadConfig()
        {
            _config = null;
            _configError = null;
            if (!HolySheetProject.HasConfig)
            {
                return;
            }

            try
            {
                _config = HolySheetProject.LoadConfig();
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
                HolySheetProject.SaveConfig(_config);
            }
            catch (IOException exception)
            {
                Debug.LogError("[HolySheet] holysheet.json could not be saved: " + exception.Message);
            }

            RefreshBanner();
        }

        private void Run(bool commit)
        {
            if (_config != null && !HolySheetProject.IsRunning)
            {
                HolySheetProject.RunAsync(commit, _offline);
            }
        }

        private void OnRunStarted()
        {
            if (_content != null)
            {
                RefreshToolbar();
            }
        }

        private void OnRunFinished(HolySheetRunResult result)
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
            page.AddToClassList("holysheet-page");
            if (_configError == null)
            {
                page.Add(new HelpBox(T("This project has no holysheet.json yet. It holds the sources and output folders; the holysheet command line reads the same file."), HelpBoxMessageType.Info));
                page.Add(Buttons(new Button(() =>
                {
                    HolySheetProject.CreateDefaultConfig();
                    ReloadConfig();
                    _page = "Settings";
                    Refresh();
                }) { text = T("Create holysheet.json") }));
            }
            else
            {
                page.Add(new HelpBox(T("holysheet.json could not be read: ") + _configError, HelpBoxMessageType.Error));
                page.Add(Buttons(
                    new Button(() => EditorUtility.OpenWithDefaultApp(HolySheetProject.ConfigPath)) { text = T("Open") },
                    new Button(Reload) { text = T("Reload") }));
            }

            return page;
        }

        private VisualElement BuildProblems()
        {
            HolySheetRunResult? result = HolySheetProject.LastResult;
            if (result == null)
            {
                return Empty(HolySheetProject.IsRunning ? T("Reading the sheets…") : T("Nothing has run yet."));
            }

            List<Diagnostic> items = result.Failure != null
                ? new List<Diagnostic> { new Diagnostic(Severity.Error, "holysheet", null, result.Failure, "") }
                : result.Build.Diagnostics.Items.OrderBy(item => item.Severity == Severity.Error ? 0 : 1).ToList();
            if (items.Count == 0)
            {
                return Empty(T("No problems."));
            }

            var container = new VisualElement();
            container.AddToClassList("holysheet-fill");

            var list = new ListView(items, -1, MakeProblemRow, (element, index) => BindProblemRow(element, items[index]))
            {
                virtualizationMethod = CollectionVirtualizationMethod.DynamicHeight,
                showAlternatingRowBackgrounds = AlternatingRowBackground.ContentOnly,
                selectionType = SelectionType.Single
            };
            list.AddToClassList("holysheet-fill");
            list.tooltip = T("Double-click a problem to open its file. Right-click to copy it.");
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
            if (problem.Location == "config" || problem.Location == "holysheet")
            {
                EditorUtility.OpenWithDefaultApp(HolySheetProject.ConfigPath);
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
                Debug.LogWarning(F("[HolySheet] The file of '{0}' was not found among the sources.", workbook));
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
            HolySheetConfig config;
            try
            {
                if (!HolySheetProject.HasConfig)
                {
                    return null;
                }

                config = HolySheetProject.LoadConfig();
            }
            catch (Exception exception) when (exception is IOException || exception is JsonFormatException)
            {
                return null;
            }

            WorkbookOrigin? origin = null;
            HolySheetProject.LastResult?.Build.WorkbookOrigins.TryGetValue(workbook, out origin);
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
            row.AddToClassList("holysheet-problem");
            var icon = new Image();
            icon.AddToClassList("holysheet-problem__icon");
            row.Add(icon);
            // Plain labels, not selectable ones: a selectable label takes the clicks, so a double-click would select a
            // word instead of opening the file. Copying is on the right-click menu.
            var text = new VisualElement();
            text.AddToClassList("holysheet-fill");
            text.Add(Plain("holysheet-problem__message"));
            var detail = new VisualElement();
            detail.AddToClassList("holysheet-problem__detail");
            detail.Add(Plain("holysheet-problem__location"));
            detail.Add(Plain("holysheet-problem__hint"));
            text.Add(detail);
            row.Add(text);
            row.AddManipulator(new ContextualMenuManipulator(menu =>
            {
                if (row.userData is not Diagnostic problem)
                {
                    return;
                }

                menu.menu.AppendAction(T("Open file"), _ => OpenProblem(problem));
                menu.menu.AppendSeparator();
                menu.menu.AppendAction(T("Copy"), _ => EditorGUIUtility.systemCopyBuffer = problem.ToString());
                menu.menu.AppendAction(T("Copy message"), _ => EditorGUIUtility.systemCopyBuffer = problem.Message);
                menu.menu.AppendAction(T("Copy location"), _ => EditorGUIUtility.systemCopyBuffer = problem.Location);
                if (problem.Hint.Length > 0)
                {
                    menu.menu.AppendAction(T("Copy fix"), _ => EditorGUIUtility.systemCopyBuffer = problem.Hint);
                }
            }));
            return row;

            static Label Plain(string className)
            {
                var label = new Label();
                label.AddToClassList(className);
                return label;
            }
        }

        private static void BindProblemRow(VisualElement row, Diagnostic diagnostic)
        {
            row.userData = diagnostic;
            row.Q<Image>().image = EditorGUIUtility.IconContent(diagnostic.Severity == Severity.Error ? "console.erroricon.sml" : "console.warnicon.sml").image;
            row.Q<Label>(className: "holysheet-problem__message").text = diagnostic.Message;
            row.Q<Label>(className: "holysheet-problem__location").text = diagnostic.Location;
            Label hint = row.Q<Label>(className: "holysheet-problem__hint");
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

            /// <summary>Set on source nodes (one per connected source): the workbooks shown under it.</summary>
            public string[]? GroupWorkbooks;

            /// <summary>Set on an enum node: one enum from an Enums sheet or a Mapping column.</summary>
            public EnumDefinition? Enum;

            /// <summary>Set on an Enums sheet node: the enums the sheet defines.</summary>
            public EnumDefinition[]? EnumSheet;

            public bool IsEnum => Enum != null || EnumSheet != null;
        }

        /// <summary>The kind of source a workbook came from; the tree's top level groups by connection.</summary>
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

        // The connection a workbook came from: its kind and the path, spreadsheet id or folder id in holysheet.json.
        private (SourceGroup Kind, string Source) ConnectionOf(string workbook) =>
            (GroupOf(workbook), _origins.TryGetValue(workbook, out WorkbookOrigin? origin) ? origin.Source : "");

        // The last folder of a path, or the start of a Google id.
        private static string ShortSource(string source)
        {
            string trimmed = source.TrimEnd('/', '\\');
            int slash = trimmed.LastIndexOfAny(new[] { '/', '\\' });
            if (slash >= 0)
            {
                return trimmed.Substring(slash + 1);
            }

            return trimmed.Length > 10 ? trimmed.Substring(0, 8) + "…" : trimmed;
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

        // The keys of the selected tree nodes. Several can be selected (Ctrl/Cmd or Shift click) for a partial build.
        private readonly HashSet<string> _selectedKeys = new HashSet<string>();

        // What the selected nodes cover: a workbook node is the whole workbook, a table node every sheet it was built from.
        private SheetSelection _treeSelection = new SheetSelection();

        private string? _selectedSheet;

        // What the tree's search box holds. It does not filter: matching names are marked yellow, as in the table's
        // find, and scrolled to.
        private string _tableFilter = "";
        private List<TreeViewItemData<TableNode>> _treeRoots = new List<TreeViewItemData<TableNode>>();
        // The ids of the nodes whose name matches, best first, and which of them Enter went to last (-1: none yet).
        private readonly List<int> _treeMatches = new List<int>();
        private int _treeMatchIndex = -1;
        private Label? _treeMatchCount;

        // The sheets each table was assembled from. Counting them walks every row, so it is done once per build.
        private readonly Dictionary<RawTable, (string Workbook, string Sheet)[]> _sheetsByTable = new Dictionary<RawTable, (string Workbook, string Sheet)[]>();

        private VisualElement BuildTables() => BuildTableView();

        // The data on top, the problems panel under it (closed to a one-line bar unless opened), split so it can be resized.
        // It sits under the data only: the tree beside it keeps the full height.
        private VisualElement WithProblems(VisualElement data)
        {
            var page = new VisualElement();
            page.AddToClassList("holysheet-fill");
            var split = new TwoPaneSplitView(1, _problemsHeight, TwoPaneSplitViewOrientation.Vertical);
            split.AddToClassList("holysheet-fill");
            split.Add(data);
            VisualElement problems = BuildProblems();
            problems.AddToClassList("holysheet-problems");
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
            HolySheetRunResult? result = HolySheetProject.LastResult;
            List<Diagnostic> items = result == null ? new List<Diagnostic>()
                : result.Failure != null ? new List<Diagnostic> { new Diagnostic(Severity.Error, "holysheet", null, result.Failure, "") }
                : result.Build.Diagnostics.Items.ToList();
            int errors = items.Count(item => item.Severity == Severity.Error);
            int warnings = items.Count - errors;

            var bar = new Toolbar();
            bar.AddToClassList("holysheet-problems-bar");
            var arrow = new Label(_problemsOpen ? "▼" : "▲");
            arrow.AddToClassList("holysheet-problems-bar__arrow");
            bar.Add(arrow);
            if (errors > 0)
            {
                bar.Add(CountBadge("console.erroricon.sml", errors));
            }

            if (warnings > 0)
            {
                bar.Add(CountBadge("console.warnicon.sml", warnings));
            }

            string text = result == null ? (HolySheetProject.IsRunning ? T("Reading the sheets…") : T("Nothing has run yet."))
                : items.Count == 0 ? T("No problems.")
                : T("Problems");
            var label = new Label(text);
            label.AddToClassList("holysheet-problems-bar__text");
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
            badge.AddToClassList("holysheet-problems-bar__count");
            var image = new Image { image = Icon(icon) };
            image.AddToClassList("holysheet-icon");
            badge.Add(image);
            badge.Add(new Label(count.ToString("N0")));
            return badge;
        }

        private VisualElement BuildTableView()
        {
            BuildResult? build = HolySheetProject.LastResult?.Build;
            if (build == null || build.RawTables.Count == 0)
            {
                // No tables, but there may be problems that explain why.
                return WithProblems(Empty(HolySheetProject.IsRunning ? T("Reading the sheets…") : T("Run Check to see the tables as the build reads them.")));
            }

            var split = new TwoPaneSplitView(0, 170, TwoPaneSplitViewOrientation.Horizontal);
            split.AddToClassList("holysheet-fill");

            var side = new VisualElement();
            side.AddToClassList("holysheet-side");
            var search = new ToolbarSearchField { value = _tableFilter, tooltip = T("Find a table, sheet, enum or workbook by name. Enter selects the next match, Shift+Enter the one before.") };
            search.AddToClassList("holysheet-search");
            var searchBar = new Toolbar();
            searchBar.Add(search);
            var matchCount = new Label();
            matchCount.AddToClassList("holysheet-search__count");
            searchBar.Add(matchCount);
            _treeMatchCount = matchCount;
            side.Add(searchBar);

            var tree = new TreeView
            {
                fixedItemHeight = 20,
                selectionType = SelectionType.Multiple,
                makeItem = MakeTreeRow
            };
            tree.AddToClassList("holysheet-fill");
            tree.bindItem = (element, index) =>
            {
                TableNode node = tree.GetItemDataForIndex<TableNode>(index);
                element.Q<Image>().image = TreeIcon(node.Icon);
                Label name = element.Q<Label>(className: "holysheet-tree-row__name");
                string query = _tableFilter.Trim();
                string marked = query.Length > 0 ? Highlight(node.Label, query) : "";
                // Rich text only while a match is marked, so a name with "<b>" in it shows as written.
                name.enableRichText = marked.Length > 0;
                name.text = marked.Length > 0 ? marked : node.Label;
                Label note = element.Q<Label>(className: "holysheet-tree-row__note");
                note.text = node.Note;
                note.style.display = node.Note.Length > 0 ? DisplayStyle.Flex : DisplayStyle.None;
                element.tooltip = node.Tooltip;
            };
            side.Add(tree);

            // What is selected in the tree: the toolbar's "Build selection" builds only that. Checking it is in the context menu.
            var selectionBar = new Toolbar();
            selectionBar.AddToClassList("holysheet-selection");
            var selectionLabel = new Label();
            selectionLabel.AddToClassList("holysheet-selection__label");
            selectionBar.Add(selectionLabel);
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

                DropdownMenuAction.Status status = _config != null && !HolySheetProject.IsRunning ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled;
                menu.menu.AppendAction(F("Build {0}", DescribeSelection()), _ => RunSelection(commit: true), status);
                menu.menu.AppendAction(F("Check {0}", DescribeSelection()), _ => RunSelection(commit: false), status);
            }));

            var preview = new VisualElement();
            preview.AddToClassList("holysheet-fill");
            tree.selectedIndicesChanged += indices =>
            {
                TableNode? shown = null;
                _selectedKeys.Clear();
                foreach (int index in indices)
                {
                    TableNode node = tree.GetItemDataForIndex<TableNode>(index);
                    _selectedKeys.Add(node.Key);
                    shown = node.Table != null || node.IsEnum ? node : shown;
                }

                UpdateTreeSelection(tree);
                if (shown != null && shown.IsEnum)
                {
                    if (shown.Key != _selectedEnumKey || preview.childCount == 0)
                    {
                        _selectedEnumKey = shown.Key;
                        ShowEnumPreview(preview, shown);
                    }

                    return;
                }

                if (shown != null && _selectedEnumKey != null)
                {
                    // Back from an enum to a table: draw the table even if it was the one shown before.
                    _selectedEnumKey = null;
                    preview.Clear();
                }
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
                ApplyTreeSearch(tree, scroll: true);
            });
            // Enter goes to the next match and selects it, so its table shows; Shift+Enter goes back.
            search.RegisterCallback<KeyDownEvent>(key =>
            {
                if (key.keyCode != KeyCode.Return && key.keyCode != KeyCode.KeypadEnter)
                {
                    return;
                }

                StepTreeSearch(tree, key.shiftKey ? -1 : 1);
                key.StopPropagation();
            }, TrickleDown.TrickleDown);

            split.Add(side);
            split.Add(WithProblems(preview));
            FillTree(tree, build.RawTables, preview);
            return split;
        }

        // Workbook > table > sheet. A table built from one sheet has no sheet nodes; one built from several
        // (Item, Item.Rare, Item.Col.Balance) lists them under it, collapsed. The search box does not filter the tree;
        // it highlights the names that match and scrolls to them (see ApplyTreeSearch).
        // Selects what was selected before, else the first table.
        private void FillTree(TreeView tree, IReadOnlyList<RawTable> tables, VisualElement preview)
        {
            var roots = new List<TreeViewItemData<TableNode>>();
            var expand = new List<int>();
            int nextId = 0;
            (int Id, RawTable Table, string? Sheet)? previous = null, first = null;
            var keyed = new List<(string Key, int Id)>();
            _origins = HolySheetProject.LastResult?.Build.WorkbookOrigins ?? new Dictionary<string, WorkbookOrigin>();
            // Enums: those of an Enums sheet go under their workbook, those a Mapping column makes go under its table.
            var sheetEnums = new Dictionary<string, List<(string Sheet, List<EnumDefinition> Enums)>>(StringComparer.Ordinal);
            var mappingEnums = new Dictionary<string, List<EnumDefinition>>(StringComparer.Ordinal);
            foreach (EnumDefinition definition in HolySheetProject.LastResult?.Build.Enums ?? (IReadOnlyList<EnumDefinition>)Array.Empty<EnumDefinition>())
            {
                (string enumWorkbook, string enumSheet) = SplitLocation(definition.Location);
                if (IsEnumsSheet(enumSheet))
                {
                    if (!sheetEnums.TryGetValue(enumWorkbook, out List<(string Sheet, List<EnumDefinition> Enums)>? sheetsOfWorkbook))
                    {
                        sheetsOfWorkbook = new List<(string Sheet, List<EnumDefinition> Enums)>();
                        sheetEnums[enumWorkbook] = sheetsOfWorkbook;
                    }

                    int at = sheetsOfWorkbook.FindIndex(entry => entry.Sheet == enumSheet);
                    if (at < 0)
                    {
                        sheetsOfWorkbook.Add((enumSheet, new List<EnumDefinition>()));
                        at = sheetsOfWorkbook.Count - 1;
                    }

                    sheetsOfWorkbook[at].Enums.Add(definition);
                }
                else
                {
                    string at = enumWorkbook + " / " + enumSheet;
                    if (!mappingEnums.TryGetValue(at, out List<EnumDefinition>? list))
                    {
                        list = new List<EnumDefinition>();
                        mappingEnums[at] = list;
                    }

                    list.Add(definition);
                }
            }

            (int Id, TableNode Node)? enumPrevious = null;
            ILookup<string, RawTable> tablesByWorkbook = tables.ToLookup(WorkbookOf);
            List<string> workbooks = tablesByWorkbook.Select(group => group.Key).Concat(sheetEnums.Keys).Distinct()
                .OrderBy(name => (int)GroupOf(name)).ThenBy(name => ConnectionOf(name).Source, StringComparer.OrdinalIgnoreCase)
                .ThenBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
            // One node per connected source (each entry under "sources" in holysheet.json), once there is more than one.
            bool showGroups = workbooks.Select(ConnectionOf).Distinct().Count() > 1;
            var workbookNodes = new List<((SourceGroup Kind, string Source) Group, string Workbook, TreeViewItemData<TableNode> Node)>();
            foreach (string workbookName in workbooks)
            {
                var children = new List<TreeViewItemData<TableNode>>();
                foreach (RawTable table in tablesByWorkbook[workbookName].OrderBy(table => table.Name, StringComparer.Ordinal))
                {
                    string[] sheets = SheetsOf(table);
                    var sheetNodes = new List<TreeViewItemData<TableNode>>();
                    int tableId = nextId++;
                    if (sheets.Length > 1)
                    {
                        foreach (string sheet in sheets)
                        {
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
                            }
                        }
                    }

                    // Enums a Mapping column of this table makes.
                    foreach ((string mappingWorkbook, string mappingSheet) in SheetRefsOf(table))
                    {
                        if (!mappingEnums.TryGetValue(mappingWorkbook + " / " + mappingSheet, out List<EnumDefinition>? made))
                        {
                            continue;
                        }

                        foreach (EnumDefinition definition in made)
                        {
                            int enumId = nextId++;
                            TableNode enumNode = EnumNode(definition, mapping: true);
                            sheetNodes.Add(new TreeViewItemData<TableNode>(enumId, enumNode));
                            keyed.Add((enumNode.Key, enumId));
                            if (enumNode.Key == _selectedEnumKey)
                            {
                                enumPrevious = (enumId, enumNode);
                            }
                        }
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
                    }

                    first ??= (tableId, table, null);
                    if ((previous?.Table == table && previous?.Sheet != null) || (enumPrevious != null && sheetNodes.Any(node => node.data == enumPrevious.Value.Node)))
                    {
                        expand.Add(tableId);
                    }
                }

                // Enums sheets, after the tables.
                if (sheetEnums.TryGetValue(workbookName, out List<(string Sheet, List<EnumDefinition> Enums)>? enumSheets))
                {
                    foreach ((string enumSheet, List<EnumDefinition> definitions) in enumSheets)
                    {
                        var enumNodes = new List<TreeViewItemData<TableNode>>();
                        foreach (EnumDefinition definition in definitions)
                        {
                            int enumId = nextId++;
                            TableNode enumNode = EnumNode(definition, mapping: false);
                            enumNodes.Add(new TreeViewItemData<TableNode>(enumId, enumNode));
                            keyed.Add((enumNode.Key, enumId));
                            if (enumNode.Key == _selectedEnumKey)
                            {
                                enumPrevious = (enumId, enumNode);
                            }
                        }

                        int enumSheetId = nextId++;
                        var sheetNode = new TableNode
                        {
                            Label = enumSheet,
                            Workbook = workbookName,
                            Sheet = enumSheet,
                            Key = "enumsheet:" + workbookName + "/" + enumSheet,
                            Icon = EnumIconName,
                            Note = definitions.Count.ToString(),
                            EnumSheet = definitions.ToArray(),
                            Tooltip = Plural(definitions.Count, "enum") + "\n" + string.Join("\n", definitions.Select(definition => definition.Name))
                        };
                        children.Add(new TreeViewItemData<TableNode>(enumSheetId, sheetNode, enumNodes));
                        keyed.Add((sheetNode.Key, enumSheetId));
                        if (sheetNode.Key == _selectedEnumKey)
                        {
                            enumPrevious = (enumSheetId, sheetNode);
                        }

                        if ((enumPrevious != null && enumNodes.Any(node => node.data == enumPrevious.Value.Node)))
                        {
                            expand.Add(enumSheetId);
                        }
                    }
                }

                if (children.Count > 0)
                {
                    int workbookId = nextId++;
                    workbookNodes.Add((ConnectionOf(workbookName), workbookName, new TreeViewItemData<TableNode>(workbookId, new TableNode
                    {
                        Label = workbookName,
                        Workbook = workbookName,
                        Key = "workbook:" + workbookName,
                        Icon = WorkbookIcon(workbookName),
                        Tooltip = _origins.TryGetValue(workbookName, out WorkbookOrigin? origin) && origin.Source.Length > 0
                            ? workbookName + "\n" + GroupLabel(GroupOf(workbookName)) + ": " + origin.Source
                            : workbookName
                    }, children)));
                    keyed.Add(("workbook:" + workbookName, workbookId));
                    expand.Add(workbookId);
                }
            }

            if (!showGroups)
            {
                roots.AddRange(workbookNodes.Select(entry => entry.Node));
            }
            else
            {
                // A source with more than one connection of its kind (two Excel folders) is told apart by its folder or id.
                var kinds = workbookNodes.Select(entry => entry.Group).Distinct().GroupBy(group => group.Kind)
                    .ToDictionary(kind => kind.Key, kind => kind.Count());
                foreach (IGrouping<(SourceGroup Kind, string Source), ((SourceGroup Kind, string Source) Group, string Workbook, TreeViewItemData<TableNode> Node)> group in workbookNodes.GroupBy(entry => entry.Group))
                {
                    int groupId = nextId++;
                    string label = GroupLabel(group.Key.Kind);
                    if (kinds[group.Key.Kind] > 1 && group.Key.Source.Length > 0)
                    {
                        label += " · " + ShortSource(group.Key.Source);
                    }

                    string key = "group:" + group.Key.Kind + ":" + group.Key.Source;
                    string[] names = group.Select(entry => entry.Workbook).ToArray();
                    roots.Add(new TreeViewItemData<TableNode>(groupId, new TableNode
                    {
                        Label = label,
                        Key = key,
                        Icon = GroupIcon(group.Key.Kind),
                        Note = names.Length.ToString(),
                        GroupWorkbooks = names,
                        Tooltip = (group.Key.Source.Length > 0 ? GroupLabel(group.Key.Kind) + ": " + group.Key.Source + "\n" : "")
                            + Plural(names.Length, "workbook") + "\n" + string.Join("\n", names)
                    }, group.Select(entry => entry.Node).ToList()));
                    keyed.Add((key, groupId));
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

            _treeRoots = roots;
            ApplyTreeSearch(tree, scroll: false);

            preview.Clear();
            // Keep the earlier selection; an enum that was selected stays selected.
            (int Id, RawTable Table, string? Sheet)? selected = previous ?? first;
            (int Id, TableNode Node)? enumSelected = enumPrevious;
            if (enumSelected != null)
            {
                _selectedEnumKey = enumSelected.Value.Node.Key;
                _selectedKeys.Clear();
                _selectedKeys.Add(_selectedEnumKey);
                tree.SetSelectionByIdWithoutNotify(new[] { enumSelected.Value.Id });
                UpdateTreeSelection(tree);
                ShowEnumPreview(preview, enumSelected.Value.Node);
                return;
            }

            _selectedEnumKey = null;
            if (selected == null)
            {
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

        /// <summary>
        /// Marks the tree names that contain the search text, opens the nodes above them and, when <paramref name="scroll"/>
        /// is set and no match is in view, scrolls to the best one. The tree keeps every node and the selection stays.
        /// </summary>
        private void ApplyTreeSearch(TreeView tree, bool scroll)
        {
            string query = _tableFilter.Trim();
            _treeMatches.Clear();
            _treeMatchIndex = -1;
            if (query.Length > 0)
            {
                var exact = new List<int>();
                var partial = new List<int>();
                var parents = new List<int>();
                var open = new HashSet<int>();
                void Walk(IEnumerable<TreeViewItemData<TableNode>> items)
                {
                    foreach (TreeViewItemData<TableNode> item in items)
                    {
                        if (Contains(item.data.Label, query))
                        {
                            (string.Equals(item.data.Label, query, StringComparison.OrdinalIgnoreCase) ? exact : partial).Add(item.id);
                            open.UnionWith(parents);
                        }

                        if (item.hasChildren)
                        {
                            parents.Add(item.id);
                            Walk(item.children);
                            parents.RemoveAt(parents.Count - 1);
                        }
                    }
                }

                Walk(_treeRoots);
                foreach (int parent in open)
                {
                    if (!tree.IsExpanded(parent))
                    {
                        tree.ExpandItem(parent);
                    }
                }

                // A name typed in full comes first, then the others in tree order.
                _treeMatches.AddRange(exact);
                _treeMatches.AddRange(partial);
            }

            tree.RefreshItems();
            UpdateTreeMatchCount(query);
            // A match already in view is enough: the tree only moves when every match is out of sight.
            if (scroll && _treeMatches.Count > 0 && !_treeMatches.Any(id => IsInView(tree, id)))
            {
                tree.ScrollToItemById(_treeMatches[0]);
            }
        }

        // Whether the row of a node is within the visible part of the tree.
        private static bool IsInView(TreeView tree, int id)
        {
            int index = tree.viewController.GetIndexForId(id);
            ScrollView? scroll = tree.Q<ScrollView>();
            if (index < 0 || scroll == null || float.IsNaN(scroll.contentViewport.layout.height))
            {
                return false;
            }

            float top = index * tree.fixedItemHeight;
            float offset = scroll.scrollOffset.y;
            return top >= offset && top + tree.fixedItemHeight <= offset + scroll.contentViewport.layout.height;
        }

        /// <summary>Selects the next (or, with -1, the previous) match, scrolls to it and shows it.</summary>
        private void StepTreeSearch(TreeView tree, int step)
        {
            if (_treeMatches.Count == 0)
            {
                return;
            }

            // The first Enter goes to the match the search scrolled to; the next ones move on from there.
            _treeMatchIndex = _treeMatchIndex < 0
                ? (step > 0 ? 0 : _treeMatches.Count - 1)
                : (_treeMatchIndex + step + _treeMatches.Count) % _treeMatches.Count;
            int id = _treeMatches[_treeMatchIndex];
            tree.SetSelectionById(id);
            tree.ScrollToItemById(id);
            UpdateTreeMatchCount(_tableFilter.Trim());
        }

        private void UpdateTreeMatchCount(string query)
        {
            if (_treeMatchCount == null)
            {
                return;
            }

            _treeMatchCount.text = query.Length == 0 ? ""
                : _treeMatches.Count == 0 ? T("No match")
                : F("{0} of {1}", Math.Max(_treeMatchIndex, 0) + 1, _treeMatches.Count);
            _treeMatchCount.style.display = query.Length == 0 ? DisplayStyle.None : DisplayStyle.Flex;
        }

        private Toolbar? _selectionBar;
        private Label? _selectionLabel;
        private ToolbarButton? _selectionBuild;

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
            if (_selectionBar != null && _selectionLabel != null)
            {
                _selectionBar.style.display = selection.IsEmpty ? DisplayStyle.None : DisplayStyle.Flex;
                _selectionLabel.text = DescribeSelection();
                _selectionBar.tooltip = T("Reads these again; every other sheet comes from the last build on this machine.\n") + string.Join("\n", SelectionLines());
            }

            RefreshSelectionBuild();
        }

        // The toolbar's "Build selection (2)": shown on the Tables page while something is selected.
        private void RefreshSelectionBuild()
        {
            if (_selectionBuild == null)
            {
                return;
            }

            SheetSelection selection = _treeSelection;
            bool shown = _config != null && _page == "Tables" && !selection.IsEmpty;
            _selectionBuild.style.display = shown ? DisplayStyle.Flex : DisplayStyle.None;
            _selectionBuild.SetEnabled(_config != null && !HolySheetProject.IsRunning);
            if (!shown)
            {
                return;
            }

            // The number of tables the selection actually builds again: every table built from a selected sheet or workbook.
            List<string> tables = (HolySheetProject.LastResult?.Build.RawTables ?? Array.Empty<RawTable>())
                .Where(table => SheetRefsOf(table).Any(entry => selection.Includes(entry.Workbook, entry.Sheet)))
                .Select(table => table.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();
            // Enums sheets build no table but do change the generated enums, so they are counted too.
            List<string> enums = (HolySheetProject.LastResult?.Build.Enums ?? (IReadOnlyList<EnumDefinition>)Array.Empty<EnumDefinition>())
                .Where(definition => SplitLocation(definition.Location) is var at && IsEnumsSheet(at.Sheet) && selection.Includes(at.Workbook, at.Sheet))
                .Select(definition => definition.Name)
                .ToList();
            string built = enums.Count == 0 ? tables.Count.ToString() : (tables.Count > 0 ? tables.Count + " · " : "") + "enum " + enums.Count;
            _selectionBuild.text = F("Build selection ({0})", built);
            _selectionBuild.tooltip = T("Read only the selected sheets again and write the result.") + "\n" + F("Tables built again: {0}", tables.Count > 0 ? string.Join(", ", tables) : "-")
                + (enums.Count > 0 ? "\n" + F("Enums read again: {0}", string.Join(", ", enums)) : "") + "\n\n" + DescribeSelection();
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
            if (_config != null && !HolySheetProject.IsRunning && !_treeSelection.IsEmpty)
            {
                HolySheetProject.RunAsync(commit, _offline, _treeSelection);
            }
        }

        private string? _selectedEnumKey;

        private const string EnumIconName = "holysheet:enum";

        // "Book / Sheet!B2" or "Book / Sheet" into its workbook and sheet.
        private static (string Workbook, string Sheet) SplitLocation(string location)
        {
            int separator = location.IndexOf(" / ", StringComparison.Ordinal);
            if (separator < 0)
            {
                return (location, "");
            }

            string sheet = location.Substring(separator + 3);
            int cell = sheet.LastIndexOf('!');
            return (location.Substring(0, separator), cell >= 0 ? sheet.Substring(0, cell) : sheet);
        }

        // The sheet rule: "Enums", or a name ending in ".Enums" (Battle.Enums).
        private static bool IsEnumsSheet(string sheet) => sheet == "Enums" || sheet.EndsWith(".Enums", StringComparison.Ordinal);

        private TableNode EnumNode(EnumDefinition definition, bool mapping)
        {
            (string workbook, string sheet) = SplitLocation(definition.Location);
            return new TableNode
            {
                Label = definition.Name,
                Workbook = workbook,
                Sheet = sheet,
                Key = "enum:" + definition.Name,
                Icon = EnumIconName,
                Note = definition.Members.Count.ToString(),
                Enum = definition,
                Tooltip = DescribeEnum(definition, mapping) + (definition.Comment.Length > 0 ? "\n" + definition.Comment : "")
            };
        }

        // "byte · Flags · 6 members" / "int · Mapping · 12 members".
        private static string DescribeEnum(EnumDefinition definition, bool mapping)
        {
            return definition.Underlying.ToString().ToLowerInvariant()
                + (definition.IsFlags ? " · Flags" : "")
                + (mapping ? " · Mapping" : "")
                + " · " + Plural(definition.Members.Count, "member");
        }

        /// <summary>An enum's members, or every enum of an Enums sheet: name, value, comment.</summary>
        private void ShowEnumPreview(VisualElement container, TableNode node)
        {
            container.Clear();
            // Find works on tables only.
            _findBar = null;
            _findField = null;
            EnumDefinition[] definitions = node.Enum != null ? new[] { node.Enum } : node.EnumSheet ?? Array.Empty<EnumDefinition>();
            bool several = node.Enum == null;
            bool mapping = node.Enum != null && !IsEnumsSheet(node.Sheet ?? "");

            var caption = new VisualElement();
            caption.AddToClassList("holysheet-caption");
            string where = (node.Workbook ?? "") + " / " + (node.Sheet ?? "");
            var captionText = new Label(several
                ? where + "  ·  " + Plural(definitions.Length, "enum")
                : where + "  ·  " + node.Enum!.Name + "  ·  " + DescribeEnum(node.Enum, mapping).Replace(" · ", "  ·  "));
            captionText.AddToClassList("holysheet-fill");
            caption.Add(captionText);
            caption.Add(ViewMenu());
            container.Add(caption);

            var rows = new List<string[]>();
            foreach (EnumDefinition definition in definitions)
            {
                foreach (EnumMember member in definition.Members)
                {
                    rows.Add(new[] { definition.Name, member.Name, FormatEnumValue(definition, member.Value), member.Comment });
                }
            }

            var columns = new List<(string Title, int Field, float Width, bool Number)>();
            if (several)
            {
                columns.Add((T("Enum"), 0, 160f, false));
            }

            columns.Add((T("Member"), 1, 180f, false));
            columns.Add((T("Value"), 2, 110f, true));
            columns.Add((T("Comment"), 3, 260f, false));

            // The rows in the order on screen; sorting replaces it, member order is kept in rows.
            List<string[]> shown = rows;
            var grid = new MultiColumnListView
            {
                itemsSource = shown,
                fixedItemHeight = 20,
                showAlternatingRowBackgrounds = AlternatingRowBackground.ContentOnly,
                selectionType = SelectionType.Multiple,
                reorderable = false,
                sortingMode = ColumnSortingMode.Custom
            };
            grid.AddToClassList("holysheet-fill");
            grid.AddToClassList("holysheet-grid");
            ApplyViewStyle(grid);
            grid.columnSortingChanged += () =>
            {
                shown = SortRows(rows, grid.sortedColumns, name =>
                {
                    int field = name.Length == 2 && name[0] == 'f' ? name[1] - '0' : -1;
                    if (field < 0 || field > 3)
                    {
                        return null;
                    }

                    return (field == 2 ? SortKind.Number : SortKind.Text, row => row[field]);
                });
                grid.ClearSelection();
                grid.itemsSource = shown;
                grid.Rebuild();
            };
            foreach ((string title, int field, float width, bool number) in columns)
            {
                grid.columns.Add(new Column
                {
                    name = "f" + field,
                    title = title,
                    width = width,
                    makeHeader = () => Header(title, "", ""),
                    makeCell = () =>
                    {
                        var label = new Label();
                        label.AddToClassList("holysheet-cell");
                        if (number)
                        {
                            label.AddToClassList("holysheet-cell--number");
                        }

                        return label;
                    },
                    bindCell = (element, index) => ((Label)element).text = shown[index][field]
                });
            }

            container.Add(grid);
        }

        // Flags read better as bits: "8  (1 << 3)".
        private static string FormatEnumValue(EnumDefinition definition, long value)
        {
            if (definition.IsFlags && value > 0 && (value & (value - 1)) == 0)
            {
                int bit = 0;
                while ((1L << bit) != value)
                {
                    bit++;
                }

                return value + "  (1 << " + bit + ")";
            }

            return value.ToString();
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
            row.AddToClassList("holysheet-tree-row");
            var icon = new Image();
            icon.AddToClassList("holysheet-icon");
            row.Add(icon);
            var name = new Label();
            name.AddToClassList("holysheet-tree-row__name");
            row.Add(name);
            var note = new Label();
            note.AddToClassList("holysheet-tree-row__note");
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
            captionRow.AddToClassList("holysheet-caption");
            var caption = new Label(where + "  ·  " + Plural(rows.Count, "row") + "  ·  " + Plural(columns.Count, "column"));
            caption.AddToClassList("holysheet-fill");
            captionRow.Add(caption);
            var shortcut = new Label(Application.platform == RuntimePlatform.OSXEditor ? T("⌘F to find") : T("Ctrl+F to find"));
            shortcut.AddToClassList("holysheet-caption__hint");
            captionRow.Add(ViewMenu());
            captionRow.Add(shortcut);
            container.Add(captionRow);

            // Find bar: search the key or every column, type, and only matching rows stay, with the matches highlighted.
            var findBar = new Toolbar();
            findBar.AddToClassList("holysheet-find");
            findBar.style.display = DisplayStyle.None;
            int keyColumn = table.FindKeyColumn();
            bool hasKey = keyColumn >= 0 && columns.Contains(keyColumn);
            var keyToggle = new ToolbarToggle { text = T("Key"), tooltip = hasKey ? F("Search {0}", table.Columns[keyColumn].Name) : T("This table has no key") };
            var allToggle = new ToolbarToggle { text = T("All"), tooltip = T("Search every column") };
            keyToggle.AddToClassList("holysheet-find__key");
            allToggle.AddToClassList("holysheet-find__all");
            keyToggle.SetEnabled(hasKey);
            bool searchAll = _findInAllColumns || !hasKey;
            keyToggle.SetValueWithoutNotify(!searchAll);
            allToggle.SetValueWithoutNotify(searchAll);
            findBar.Add(keyToggle);
            findBar.Add(allToggle);
            var findField = new ToolbarSearchField { tooltip = T("Rows whose cell contains this text. Esc closes.") };
            findField.AddToClassList("holysheet-find__field");
            findBar.Add(findField);
            var matches = new Label();
            matches.AddToClassList("holysheet-find__count");
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
                // Clicking a heading sorts by it, Shift+click adds a second column. Unity draws the arrows; the rows are
                // ordered here, after the find filter, so find and sort work together.
                sortingMode = ColumnSortingMode.Custom
            };
            grid.AddToClassList("holysheet-fill");
            grid.AddToClassList("holysheet-grid");
            ApplyViewStyle(grid);
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
                makeCell = () => Cell("holysheet-cell--row"),
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
                bool number = (typeClass == "holysheet-type--int" || typeClass == "holysheet-type--float") && !IsArray(raw.TypeText);
                grid.columns.Add(new Column
                {
                    name = "c" + index,
                    title = raw.Name,
                    width = 120,
                    makeHeader = () => Header(raw.Name, raw.TypeText, raw.Comment),
                    // Values keep the default text color; only the type under the heading is colored.
                    // Numbers line up on the right, as in a spreadsheet.
                    makeCell = () => Cell(number ? "holysheet-cell--number" : ""),
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
            noMatch.AddToClassList("holysheet-empty");
            noMatch.style.display = DisplayStyle.None;
            container.Add(noMatch);

            // How each column compares when sorted. The Row column is sheet order, which SortRows knows by itself.
            var sortKinds = new Dictionary<string, (int Column, SortKind Kind)>();
            foreach (int index in columns)
            {
                sortKinds["c" + index] = (index, SortKindOf(table.Columns[index].TypeText));
            }

            (SortKind Kind, Func<Cell[], string> Text)? SortColumn(string name)
            {
                if (!sortKinds.TryGetValue(name, out (int Column, SortKind Kind) found))
                {
                    return null;
                }

                int column = found.Column;
                return (found.Kind, row => column < row.Length ? row[column].Text : "");
            }

            // Filter, then sort. Rows keep their sheet order through the filter, so sorting by Row puts that order back.
            void ApplyView()
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

                rows = SortRows(rows, grid.sortedColumns, SortColumn);
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
                ApplyView();
            }

            findField.RegisterValueChangedCallback(_ => ApplyView());
            keyToggle.RegisterValueChangedCallback(change => SetScope(!change.newValue));
            allToggle.RegisterValueChangedCallback(change => SetScope(change.newValue));
            grid.columnSortingChanged += ApplyView;
        }

        /// <summary>How a column's text is compared when the data view is sorted.</summary>
        internal enum SortKind
        {
            Text,
            Number,
            Bool
        }

        // Single numbers sort by value and booleans false before true. Everything else, arrays included, sorts as text.
        private static SortKind SortKindOf(string typeText)
        {
            if (IsArray(typeText))
            {
                return SortKind.Text;
            }

            switch (TypeClass(typeText))
            {
                case "holysheet-type--int":
                case "holysheet-type--float":
                    return SortKind.Number;
                case "holysheet-type--bool":
                    return SortKind.Bool;
                default:
                    return SortKind.Text;
            }
        }

        // A cell's sort value, read once per sort. Missing: empty, or not a number in a number column.
        private readonly struct SortKey
        {
            public readonly bool Missing;
            public readonly double Number;
            public readonly string Text;

            private SortKey(bool missing, double number, string text)
            {
                Missing = missing;
                Number = number;
                Text = text;
            }

            public static SortKey Of(string text, SortKind kind)
            {
                text = text.Trim();
                if (text.Length == 0)
                {
                    return new SortKey(true, 0, "");
                }

                switch (kind)
                {
                    case SortKind.Number:
                        // Only the leading number counts, so an enum value shown as "8  (1 << 3)" sorts as 8.
                        int end = 0;
                        while (end < text.Length && !char.IsWhiteSpace(text[end]))
                        {
                            end++;
                        }

                        return double.TryParse(text.Substring(0, end), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double number)
                            ? new SortKey(false, number, text)
                            : new SortKey(true, 0, text);
                    case SortKind.Bool:
                        // As the build reads booleans: 1 / true and 0 / false.
                        if (text == "1" || string.Equals(text, "true", StringComparison.OrdinalIgnoreCase))
                        {
                            return new SortKey(false, 1, text);
                        }

                        return text == "0" || string.Equals(text, "false", StringComparison.OrdinalIgnoreCase)
                            ? new SortKey(false, 0, text)
                            : new SortKey(true, 0, text);
                    default:
                        return new SortKey(false, 0, text);
                }
            }
        }

        /// <summary>
        /// Orders <paramref name="rows"/> by the grid's sorted columns, first column first. A column named "row" is the
        /// order the rows came in; others are looked up with <paramref name="columnFor"/>. Empty cells go last in either
        /// direction, as in a spreadsheet, and ties keep the order the rows came in.
        /// </summary>
        internal static List<T> SortRows<T>(List<T> rows, IEnumerable<SortColumnDescription> sorted, Func<string, (SortKind Kind, Func<T, string> Text)?> columnFor)
        {
            var keys = new List<(SortKey[]? Keys, SortKind Kind, bool Descending)>();
            foreach (SortColumnDescription description in sorted)
            {
                // A heading click describes the column by index, code by name; the resolved column has both.
                string name = description.column?.name ?? "";
                if (name.Length == 0)
                {
                    name = description.columnName ?? "";
                }
                bool descending = description.direction == SortDirection.Descending;
                if (name == "row")
                {
                    keys.Add((null, SortKind.Number, descending));
                    continue;
                }

                (SortKind Kind, Func<T, string> Text)? column = columnFor(name);
                if (column == null)
                {
                    continue;
                }

                // Parse each cell once here, not on every comparison: a table can have a few hundred thousand rows.
                var values = new SortKey[rows.Count];
                for (int i = 0; i < rows.Count; i++)
                {
                    values[i] = SortKey.Of(column.Value.Text(rows[i]) ?? "", column.Value.Kind);
                }

                keys.Add((values, column.Value.Kind, descending));
            }

            if (keys.Count == 0)
            {
                return rows;
            }

            int[] order = Enumerable.Range(0, rows.Count).ToArray();
            Array.Sort(order, (a, b) =>
            {
                foreach ((SortKey[]? values, SortKind kind, bool descending) in keys)
                {
                    int result;
                    if (values == null)
                    {
                        result = a.CompareTo(b);
                    }
                    else
                    {
                        SortKey x = values[a];
                        SortKey y = values[b];
                        if (x.Missing || y.Missing)
                        {
                            // Not flipped by the direction: blanks stay at the bottom.
                            result = x.Missing == y.Missing ? 0 : x.Missing ? 1 : -1;
                            if (result != 0)
                            {
                                return result;
                            }

                            continue;
                        }

                        result = kind == SortKind.Text ? CompareNatural(x.Text, y.Text) : x.Number.CompareTo(y.Number);
                    }

                    if (result != 0)
                    {
                        return descending ? -result : result;
                    }
                }

                // Array.Sort is not stable; the original position breaks ties.
                return a.CompareTo(b);
            });

            var sortedRows = new List<T>(rows.Count);
            foreach (int index in order)
            {
                sortedRows.Add(rows[index]);
            }

            return sortedRows;
        }

        /// <summary>
        /// Text order for people: case is ignored and runs of digits compare as numbers, so "Item2" comes before "Item10".
        /// Other characters compare by code point, which puts Hangul in 가나다 order.
        /// </summary>
        public static int CompareNatural(string a, string b)
        {
            int i = 0;
            int j = 0;
            while (i < a.Length && j < b.Length)
            {
                if (IsAsciiDigit(a[i]) && IsAsciiDigit(b[j]))
                {
                    int endA = i;
                    while (endA < a.Length && IsAsciiDigit(a[endA]))
                    {
                        endA++;
                    }

                    int endB = j;
                    while (endB < b.Length && IsAsciiDigit(b[endB]))
                    {
                        endB++;
                    }

                    // Leading zeros do not change the value; "007" equals "7" here, and the final ordinal check splits them.
                    int startA = i;
                    while (startA < endA - 1 && a[startA] == '0')
                    {
                        startA++;
                    }

                    int startB = j;
                    while (startB < endB - 1 && b[startB] == '0')
                    {
                        startB++;
                    }

                    int lengthA = endA - startA;
                    int lengthB = endB - startB;
                    if (lengthA != lengthB)
                    {
                        return lengthA < lengthB ? -1 : 1;
                    }

                    for (int k = 0; k < lengthA; k++)
                    {
                        if (a[startA + k] != b[startB + k])
                        {
                            return a[startA + k] < b[startB + k] ? -1 : 1;
                        }
                    }

                    i = endA;
                    j = endB;
                    continue;
                }

                char x = char.ToUpperInvariant(a[i]);
                char y = char.ToUpperInvariant(b[j]);
                if (x != y)
                {
                    return x < y ? -1 : 1;
                }

                i++;
                j++;
            }

            int rest = (a.Length - i).CompareTo(b.Length - j);
            return rest != 0 ? rest : string.CompareOrdinal(a, b);
        }

        private static bool IsAsciiDigit(char c) => c >= '0' && c <= '9';

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
                    return "holysheet-type--enum";
                case TypeExpressionKind.Reference:
                    return "holysheet-type--ref";
                case TypeExpressionKind.Mapping:
                    return "holysheet-type--mapping";
            }

            if (!Scalars.TryFind(expression.Name, out ScalarKind kind))
            {
                // A bare name that is not a value type is an enum.
                return "holysheet-type--enum";
            }

            switch (kind)
            {
                case ScalarKind.Bool:
                    return "holysheet-type--bool";
                case ScalarKind.Float:
                case ScalarKind.Double:
                    return "holysheet-type--float";
                case ScalarKind.String:
                    return "";
                case ScalarKind.DateTime:
                    return "holysheet-type--date";
                default:
                    return "holysheet-type--int";
            }
        }

        private static bool IsArray(string typeText) => TypeExpression.TryParse(typeText, out TypeExpression? expression, out _) && expression != null && expression.IsArray;

        private static VisualElement Header(string name, string type, string comment)
        {
            var header = new VisualElement { tooltip = comment };
            header.AddToClassList("holysheet-header");
            header.Add(new Label(name));
            if (type.Length > 0)
            {
                var typeLabel = new Label(type);
                typeLabel.AddToClassList("holysheet-header__type");
                string typeClass = TypeClass(type);
                if (typeClass.Length > 0)
                {
                    typeLabel.AddToClassList(typeClass);
                }
                header.Add(typeLabel);
            }

            return header;
        }

        /// <summary>Lines drawn between the cells of the data view.</summary>
        public enum GridLines
        {
            None,
            Rows,
            Columns,
            Both
        }

        /// <summary>How text sits in the cells of the data view. Auto puts numbers on the right and the rest on the left.</summary>
        public enum CellAlignment
        {
            Auto,
            Left,
            Center,
            Right
        }

        // How the data view looks is a matter of taste, so it is kept per user in UserSettings, next to the language.
        // Out of the box: lines between every cell, text in the middle, striped rows.
        public static GridLines ViewGridLines
        {
            get => Enum.TryParse(HolySheetText.GetSetting("view.gridLines"), true, out GridLines lines) ? lines : GridLines.Both;
            set => HolySheetText.SetSetting("view.gridLines", value.ToString());
        }

        public static CellAlignment ViewAlignment
        {
            get => Enum.TryParse(HolySheetText.GetSetting("view.alignment"), true, out CellAlignment alignment) ? alignment : CellAlignment.Center;
            set => HolySheetText.SetSetting("view.alignment", value.ToString());
        }

        public static bool ViewStripes
        {
            get => HolySheetText.GetSetting("view.stripes") != "false";
            set => HolySheetText.SetSetting("view.stripes", value ? "true" : "false");
        }

        /// <summary>Gives a data grid the user's view style: grid lines, alignment and striped rows.</summary>
        private static void ApplyViewStyle(MultiColumnListView grid)
        {
            GridLines lines = ViewGridLines;
            CellAlignment alignment = ViewAlignment;
            grid.EnableInClassList("holysheet-grid--rows", lines == GridLines.Rows || lines == GridLines.Both);
            grid.EnableInClassList("holysheet-grid--columns", lines == GridLines.Columns || lines == GridLines.Both);
            grid.EnableInClassList("holysheet-grid--left", alignment == CellAlignment.Left);
            grid.EnableInClassList("holysheet-grid--center", alignment == CellAlignment.Center);
            grid.EnableInClassList("holysheet-grid--right", alignment == CellAlignment.Right);
            AlternatingRowBackground stripes = ViewStripes ? AlternatingRowBackground.ContentOnly : AlternatingRowBackground.None;
            if (grid.showAlternatingRowBackgrounds != stripes)
            {
                grid.showAlternatingRowBackgrounds = stripes;
            }
        }

        /// <summary>Saves the view style for this user and applies it to the grids on screen.</summary>
        public void SetViewStyle(GridLines lines, CellAlignment alignment, bool stripes)
        {
            ViewGridLines = lines;
            ViewAlignment = alignment;
            ViewStripes = stripes;
            rootVisualElement.Query<MultiColumnListView>(className: "holysheet-grid").ForEach(ApplyViewStyle);
        }

        /// <summary>A small "View" dropdown button in a caption: grid lines, text alignment and striped rows.</summary>
        private Button ViewMenu()
        {
            var view = new Button { tooltip = T("How the table looks. Kept for you only, in UserSettings.") };
            view.AddToClassList("holysheet-caption__menu");
            // A label and an arrow side by side; the button's own text would be drawn under the arrow.
            var label = new Label(T("View"));
            label.AddToClassList("holysheet-caption__menu-label");
            view.Add(label);
            // The arrow of Unity's own popup fields, so it reads as a dropdown.
            var arrow = new VisualElement();
            arrow.AddToClassList("unity-base-popup-field__arrow");
            arrow.AddToClassList("holysheet-caption__menu-arrow");
            view.Add(arrow);
            view.clicked += () =>
            {
                var menu = new GenericMenu();
                GridLines lines = ViewGridLines;
                foreach ((GridLines value, string name) in new[] { (GridLines.None, T("None")), (GridLines.Rows, T("Between rows")), (GridLines.Columns, T("Between columns")), (GridLines.Both, T("Both")) })
                {
                    menu.AddItem(new GUIContent(T("Grid lines") + "/" + name), lines == value, () => SetViewStyle(value, ViewAlignment, ViewStripes));
                }

                CellAlignment alignment = ViewAlignment;
                foreach ((CellAlignment value, string name) in new[] { (CellAlignment.Auto, T("Auto (numbers right)")), (CellAlignment.Left, T("Left")), (CellAlignment.Center, T("Center")), (CellAlignment.Right, T("Right")) })
                {
                    menu.AddItem(new GUIContent(T("Text alignment") + "/" + name), alignment == value, () => SetViewStyle(ViewGridLines, value, ViewStripes));
                }

                bool stripes = ViewStripes;
                menu.AddItem(new GUIContent(T("Striped rows")), stripes, () => SetViewStyle(ViewGridLines, ViewAlignment, !stripes));
                menu.DropDown(view.worldBound);
            };
            return view;
        }

        private static Label Cell(string className)
        {
            var label = new Label();
            label.AddToClassList("holysheet-cell");
            if (className.Length > 0)
            {
                label.AddToClassList(className);
            }

            return label;
        }

        private VisualElement BuildSettings()
        {
            HolySheetConfig config = _config!;
            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.AddToClassList("holysheet-fill");
            var form = new VisualElement();
            form.AddToClassList("holysheet-form");
            scroll.Add(form);

            Foldout sources = Section(T("Sources"), "sources", "Folder Icon", open: true);
            for (int index = 0; index < config.Sources.Count; index++)
            {
                sources.Add(BuildSource(config.Sources[index], index));
            }

            var add = new Button { text = T("Add source"), iconImage = Background.FromTexture2D(Icon("Toolbar Plus")) };
            add.AddToClassList("holysheet-add");
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
                HolySheetProject.IsSepAvailable ? T("auto and builtin use the built-in reader; sep uses Sep. Same results.") : T("auto and builtin use the built-in reader. sep needs Unity 7 (.NET 8+).")));
            build.Add(Check(T("Parallel"), config.Parallel, value => config.Parallel = value, T("Convert and encode tables on several threads. The output is the same either way.")));
            build.Add(Text(T("Build label"), config.BuildLabel ?? "", value => config.BuildLabel = Optional(value), T("Optional text written to the manifest, such as a version.")));
            form.Add(build);

            form.Add(BuildSigning(config));
            form.Add(BuildEditorSettings());

            var footer = Buttons(
                new Button(() => EditorUtility.OpenWithDefaultApp(HolySheetProject.ConfigPath)) { text = T("Open holysheet.json") },
                new Button(() => EditorUtility.RevealInFinder(HolySheetProject.ConfigPath)) { text = T("Reveal") });
            footer.AddToClassList("holysheet-footer");
            form.Add(footer);
            return scroll;
        }

        private VisualElement BuildSource(SourceConfig source, int index)
        {
            var card = new VisualElement();
            card.AddToClassList("holysheet-card");
            var head = new VisualElement();
            head.AddToClassList("holysheet-card__head");
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
            icon.AddToClassList("holysheet-icon");
            head.Add(icon);
            var title = new Label(source.Type switch
            {
                SourceType.Google => "Google Sheets",
                SourceType.GoogleFolder => T("Google Drive folder"),
                SourceType.Csv => T("CSV folder"),
                _ => T("Excel workbook")
            });
            title.AddToClassList("holysheet-card__title");
            head.Add(title);
            var summary = new Label(source.Type switch
            {
                SourceType.Google => source.Name ?? source.SpreadsheetId ?? "",
                SourceType.GoogleFolder => source.Name ?? source.FolderId ?? "",
                _ => source.Path ?? ""
            });
            summary.AddToClassList("holysheet-card__summary");
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
                if (source.Auth.Type == GoogleAuthType.Custom && string.IsNullOrEmpty(source.Auth.Provider) && HolySheetProject.CredentialProviderNames() is { Count: 1 } only)
                {
                    // With a single provider in the project there is nothing to choose.
                    source.Auth.Provider = only[0];
                }

                ShowPage();
            }, T("Public link and API key read spreadsheets shared as 'anyone with the link'. A service account reads private ones shared with its e-mail.")));
            switch (source.Auth.Type)
            {
                case GoogleAuthType.ApiKey:
                    card.Add(Text(T("Key variable"), source.Auth.ApiKeyEnv ?? "", value => source.Auth.ApiKeyEnv = Optional(value), T("Environment variable that holds the API key.")));
                    card.Add(Text(T("Key"), source.Auth.ApiKey ?? "", value => source.Auth.ApiKey = Optional(value), T("Or the key itself. Saved to holysheet.json as plain text."), password: true));
                    break;
                case GoogleAuthType.ServiceAccount:
                    card.Add(PathField(T("Key file"), source.Auth.KeyFile ?? "", value => source.Auth.KeyFile = Optional(value), T("The service account's JSON key. Keep it out of version control."), Browse.File));
                    break;
                case GoogleAuthType.AccessToken:
                    card.Add(Text(T("Token variable"), source.Auth.AccessTokenEnv ?? "", value => source.Auth.AccessTokenEnv = Optional(value), T("Environment variable that holds an OAuth access token.")));
                    break;
                case GoogleAuthType.Custom:
                    AddProviderChoice(card, source);
                    break;
                case GoogleAuthType.Command:
                    card.Add(Text(T("Command"), source.Auth.Command ?? "", value => source.Auth.Command = Optional(value),
                        T("Runs in the project folder; what it prints is used: an access token, JSON with access_token, or a service account's JSON key. Works in the CLI and CI too. Use full paths: the editor may not see your terminal's PATH.")));
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
                    Debug.LogWarning("[HolySheet] Tabs must be Name:gid pairs separated by commas, e.g. Item:0, Monster:123456.");
                    return;
                }

                source.Sheets = parsed;
                Save();
            });
            card.Add(tabs);
            card.Add(PathField(T("Snapshot folder"), source.Snapshot ?? "", value => source.Snapshot = Optional(value), T("Optional. The fetched CSV is saved here, for diffs and offline builds."), Browse.Folder));
            return card;
        }

        // Custom credentials: the provider classes this editor found, by name.
        private void AddProviderChoice(VisualElement card, SourceConfig source)
        {
            List<string> names = HolySheetProject.CredentialProviderNames().ToList();
            string? current = source.Auth.Provider;
            if (names.Count == 0)
            {
                card.Add(new HelpBox(T("No credential provider was found. Add a class that implements HolySheet.Core.Sources.IGoogleCredentialProvider to an editor assembly; it is found by itself and listed here by its class name, or by the name in [HolySheetCredentialProvider(\"name\")]."), HelpBoxMessageType.Info));
                if (string.IsNullOrEmpty(current))
                {
                    return;
                }
            }

            bool missing = !string.IsNullOrEmpty(current) && !names.Contains(current!, StringComparer.OrdinalIgnoreCase);
            if (missing)
            {
                // Kept in the list so the choice in holysheet.json is shown as it is, not silently replaced.
                names.Add(current!);
            }

            string selected = names.FirstOrDefault(name => string.Equals(name, current, StringComparison.OrdinalIgnoreCase)) ?? "";
            if (selected.Length == 0)
            {
                names.Insert(0, "");
            }

            card.Add(Choice(T("Provider"), names, selected, value => source.Auth.Provider = Optional(value),
                T("The class that supplies the credentials. Only the editor runs it: the CLI and CI need auth type \"command\", or an offline build.")));
            if (missing)
            {
                card.Add(new HelpBox(string.Format(T("No credential provider named '{0}' is in the project."), current), HelpBoxMessageType.Warning));
            }
        }

        private void AddSource(SourceConfig source)
        {
            _config!.Sources.Add(source);
            Save();
            ShowPage();
        }

        // Settings of this window for this user, not of the project: kept in UserSettings, not in holysheet.json.
        private Foldout BuildEditorSettings()
        {
            Foldout editor = Section(T("Editor"), "editor", "d_UnityEditor.SceneHierarchyWindow", open: true);
            string systemName = Application.systemLanguage == SystemLanguage.Korean ? "한국어" : "English";
            var names = new List<string> { F("System ({0})", systemName), "English", "한국어" };
            // Not through Choice(): that saves holysheet.json, and the language is not part of it.
            var language = new DropdownField(T("Language"), names, (int)HolySheetText.Language) { tooltip = T("The language of this window. Kept for you only, in UserSettings.") };
            language.AddToClassList(DropdownField.alignedFieldUssClassName);
            language.RegisterValueChangedCallback(change =>
            {
                HolySheetText.Language = (HolySheetLanguage)names.IndexOf(change.newValue);
                // Every label is made in the language of the moment, so the window is built again.
                rootVisualElement.schedule.Execute(CreateGUI);
            });
            editor.Add(language);
            return editor;
        }

        private Foldout BuildSigning(HolySheetConfig config)
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
                Label report = Selectable("holysheet-report");
                report.text = _verifyReport;
                signing.Add(report);
            }

            return signing;
        }

        private void CreateKeyPair()
        {
            HolySheetConfig config = _config!;
            string path = Path.Combine(HolySheetProject.RootDirectory, "holysheet.private.key");
            if (File.Exists(path))
            {
                EditorUtility.DisplayDialog("HolySheet", "holysheet.private.key already exists in the project root.\n\nReplacing a key invalidates every client built with the old one. Delete the file first if that is intended.", "OK");
                return;
            }

            (string privateKey, string publicKey) = SigningKeys.Generate();
            File.WriteAllText(path, privateKey);
            config.Signing.PublicKey = publicKey;
            config.Signing.PrivateKeyFile = "holysheet.private.key";
            Save();

            string ignorePath = Path.Combine(HolySheetProject.RootDirectory, ".gitignore");
            bool ignored = File.Exists(ignorePath) && File.ReadAllText(ignorePath).Contains("holysheet.private.key");
            if (!ignored && EditorUtility.DisplayDialog("HolySheet", "The private key was written to holysheet.private.key. It must not be committed.\n\nAdd it to .gitignore?", T("Add"), T("Not now")))
            {
                File.AppendAllText(ignorePath, (File.Exists(ignorePath) && !File.ReadAllText(ignorePath).EndsWith("\n") ? "\n" : "") + "holysheet.private.key\n");
            }

            ShowPage();
        }

        private void VerifyData()
        {
            HolySheetConfig config = _config!;
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
            string stateKey = "HolySheet.Window.Section." + key;
            var foldout = new Foldout { text = title, value = SessionState.GetBool(stateKey, open) };
            foldout.AddToClassList("holysheet-section");
            // The header looks like an inspector component header: an icon before the title, a bar behind it.
            VisualElement? input = foldout.Q(className: Foldout.inputUssClassName);
            if (input != null)
            {
                var image = new Image { image = Icon(icon) };
                image.AddToClassList("holysheet-icon");
                image.AddToClassList("holysheet-section__icon");
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
            label.AddToClassList("holysheet-subheading");
            return label;
        }

        private const string TableIconName = "holysheet:table";

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

        private const string ExcelIconName = "holysheet:excel";
        private const string GoogleSheetsIconName = "holysheet:google-sheets";

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
                case EnumIconName:
                    // In the enum type color of the current skin.
                    return EditorGUIUtility.isProSkin ? DrawnIcon(name + ":dark", (x, y) => EnumPixel(x, y, EnumDark)) : DrawnIcon(name + ":light", (x, y) => EnumPixel(x, y, EnumLight));
                default:
                    return Icon(name);
            }
        }

        private static readonly Color ExcelDark = new Color32(0x10, 0x7C, 0x41, 0xFF);
        private static readonly Color ExcelMid = new Color32(0x21, 0xA3, 0x66, 0xFF);
        private static readonly Color ExcelLight = new Color32(0x33, 0xC4, 0x81, 0xFF);
        private static readonly Color EnumDark = new Color32(0xD8, 0xC5, 0x78, 0xFF);
        private static readonly Color EnumLight = new Color32(0x85, 0x70, 0x0F, 0xFF);

        // A short list: three bullets with lines, the look of a set of named values.
        private static Color? EnumPixel(float x, float y, Color ink)
        {
            for (int row = 0; row < 3; row++)
            {
                float top = 6.5f + row * 7.5f;
                if (InRoundedRect(x, y, 4f, top, 9f, top + 5f, 1f) || InRoundedRect(x, y, 12f, top + 1f, 28f, top + 4f, 1.2f))
                {
                    return ink;
                }
            }

            return null;
        }

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
            button.AddToClassList("holysheet-icon-button");
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
            row.AddToClassList("holysheet-path");
            TextField field = Text(label, value, apply, tooltip);
            row.Add(field);
            var button = new Button { iconImage = Background.FromTexture2D(Icon("Folder Icon")), tooltip = T("Browse") };
            button.AddToClassList("holysheet-icon-button");
            button.clicked += () =>
            {
                string start = HolySheetProject.RootDirectory;
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
            row.AddToClassList("holysheet-buttons");
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
            empty.AddToClassList("holysheet-empty");
            return empty;
        }

        // ------------------------------------------------------------ helpers

        private static string Plural(int count, string noun) => HolySheetText.Count(count, noun);

        private static string? Optional(string value) => value.Length == 0 ? null : value;

        private static string ToProjectRelative(string path)
        {
            string root = HolySheetProject.RootDirectory.Replace('\\', '/').TrimEnd('/') + "/";
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
