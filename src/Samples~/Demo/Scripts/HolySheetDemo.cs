#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Collections;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HolySheet.Demo.Data;
using UnityEngine;

namespace HolySheet.Demo
{
    /// <summary>
    /// A test bench for the generated tables. It loads them, shows every table, and lets you try the things that matter:
    /// reloading while the game runs, loading in parallel, and what happens when a data file was tampered with.
    /// <para>
    /// In the editor it watches the data folder: leave Play Mode running, edit Sheets/Game.xlsx, build the tables
    /// (Tools &gt; HolySheet &gt; Build Tables) and the new values appear here without restarting.
    /// In a player it reads the same files through Resources.
    /// </para>
    /// </summary>
    public sealed class HolySheetDemo : MonoBehaviour
    {
        [Tooltip("Folder under a Resources folder that holds the built tables.")]
        [SerializeField] private string resourcesFolder = "HolySheetDemo";

        [Tooltip("Decode tables on several threads. The files are read on the main thread first, because Resources requires that.")]
        [SerializeField] private bool parallelLoad;

        [Tooltip("In the editor, reload automatically when the data files change.")]
        [SerializeField] private bool watchInEditor = true;

        private readonly List<string> _log = new List<string>();
        private TableWatcher<DemoTables>? _watcher;
        private Vector2 _rowsScroll;
        private Vector2 _tablesScroll;
        private Vector2 _logScroll;
        private int _selected;
        private GUIStyle? _title;
        private GUIStyle? _text;
        private GUIStyle? _dim;
        private GUIStyle? _cell;
        private GUIStyle? _head;
        private GUIStyle? _panel;
        private GUIStyle? _button;
        private Texture2D? _panelTexture;
        private Texture2D? _rowTexture;

        /// <summary>The tables currently in use, or null when loading failed.</summary>
        public DemoTables? Tables { get; private set; }

        /// <summary>Why the last load failed, or null.</summary>
        public string? LastError { get; private set; }

        /// <summary>How many times a reload replaced the tables since this component started.</summary>
        public int ReloadCount { get; private set; }

        public bool IsWatching => _watcher != null;

        public double LastLoadMilliseconds { get; private set; }

        private void Start()
        {
            Load();
        }

        private void OnDestroy()
        {
            StopWatching();
            if (_panelTexture != null)
            {
                Destroy(_panelTexture);
            }

            if (_rowTexture != null)
            {
                Destroy(_rowTexture);
            }
        }

        /// <summary>Loads the tables, replacing the current ones only if the load succeeds.</summary>
        public void Load()
        {
            StopWatching();
            var options = new TableLoadOptions { Parallel = parallelLoad };
            Stopwatch stopwatch = Stopwatch.StartNew();
            try
            {
                string? directory = watchInEditor ? FindDataDirectory() : null;
                if (directory != null)
                {
                    // Watch loads once, then keeps Current up to date. Its events arrive on the main thread.
                    _watcher = DemoTables.Watch(directory, new TableWatchOptions { LoadOptions = options });
                    _watcher.Reloaded += OnReloaded;
                    _watcher.ReloadFailed += OnReloadFailed;
                    Tables = _watcher.Current;
                }
                else
                {
                    // Resources can only be read on the main thread. Preload reads every file here;
                    // the decoding that follows is then free to run on other threads.
                    TableSource source = TableSource.Preload(new ResourcesTableSource(resourcesFolder), DemoTables.ManifestFileName);
                    Tables = DemoTables.Load(source, options);
                }

                stopwatch.Stop();
                LastLoadMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
                LastError = null;
                Log("Loaded " + Tables.Tables.Count + " tables in " + LastLoadMilliseconds.ToString("F1") + " ms (" + (parallelLoad ? "parallel" : "sequential") + (IsWatching ? ", watching for changes" : "") + ")");
            }
            catch (TableLoadException exception)
            {
                // The tables that were in use stay in use: a failed load never leaves a half-loaded set behind.
                LastError = exception.Error + ": " + exception.Message;
                Log("Load failed. " + LastError);
            }
        }

        /// <summary>
        /// Shows tamper detection without touching any file: takes the real data, changes one byte of one table in memory
        /// and tries to load it. Returns the error the loader reported, or null if (wrongly) nothing was detected.
        /// </summary>
        public string? RunTamperTest()
        {
            MemoryTableSource original = TableSource.Preload(CurrentSource(), DemoTables.ManifestFileName);
            var files = new Dictionary<string, byte[]>();
            foreach (string name in original.FileNames)
            {
                files[name] = (byte[])original.Read(name)!.Clone();
            }

            const string target = ItemTable.TableName + ".bytes";
            if (!files.TryGetValue(target, out byte[]? bytes) || bytes.Length == 0)
            {
                Log("Tamper test could not run: " + target + " was not found.");
                return null;
            }

            bytes[bytes.Length / 2] ^= 0x01;
            try
            {
                DemoTables.Load(TableSource.FromMemory(files));
                Log("Tamper test FAILED: a modified data file was loaded without complaint.");
                return null;
            }
            catch (TableLoadException exception)
            {
                Log("Tamper test passed. One changed byte in " + target + " was refused: " + exception.Error);
                return exception.Error + ": " + exception.Message;
            }
        }

        private TableSource CurrentSource()
        {
            string? directory = FindDataDirectory();
            return directory != null ? TableSource.FromDirectory(directory) : new ResourcesTableSource(resourcesFolder);
        }

        private void OnReloaded(TableReloadedEventArgs<DemoTables> change)
        {
            Tables = change.Current;
            ReloadCount++;
            // Tables that are not listed are the same objects as before: only what changed was decoded again.
            Log("Reloaded: " + string.Join(", ", change.ChangedTables) + " changed. Content " + change.Current.Manifest.Content.Substring(0, 12));
        }

        private void OnReloadFailed(Exception exception)
        {
            Log("The data changed but could not be loaded, so the current tables stay: " + exception.Message);
        }

        private void StopWatching()
        {
            if (_watcher != null)
            {
                _watcher.Reloaded -= OnReloaded;
                _watcher.ReloadFailed -= OnReloadFailed;
                _watcher.Dispose();
                _watcher = null;
            }
        }

        // In the editor the Resources folder is a real folder on disk, which is what a watcher needs.
        private string? FindDataDirectory()
        {
#if UNITY_EDITOR
            TextAsset manifest = Resources.Load<TextAsset>(resourcesFolder + "/" + Path.GetFileNameWithoutExtension(DemoTables.ManifestFileName));
            if (manifest != null)
            {
                string assetPath = UnityEditor.AssetDatabase.GetAssetPath(manifest);
                if (!string.IsNullOrEmpty(assetPath))
                {
                    return Path.GetDirectoryName(Path.GetFullPath(assetPath));
                }
            }
#endif
            return null;
        }

        private void Log(string message)
        {
            _log.Add(DateTime.Now.ToString("HH:mm:ss") + "  " + message);
            if (_log.Count > 100)
            {
                _log.RemoveAt(0);
            }

            _logScroll.y = float.MaxValue;
            UnityEngine.Debug.Log("[HolySheet Demo] " + message);
        }

        // ------------------------------------------------------------------ drawing

        private void OnGUI()
        {
            EnsureStyles();
            // Lay out for a 1280x720 canvas and scale it to the real screen.
            float scale = Mathf.Min(Screen.width / 1280f, Screen.height / 720f);
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
            float width = Screen.width / scale;
            float height = Screen.height / scale;
            const float margin = 16f;

            DrawHeader(new Rect(margin, margin, width - margin * 2, 72f));
            float top = margin + 72f + 12f;
            float logHeight = 132f;
            float bodyHeight = height - top - logHeight - margin - 12f;
            DrawTableList(new Rect(margin, top, 230f, bodyHeight));
            DrawRows(new Rect(margin + 230f + 12f, top, width - margin * 2 - 230f - 12f, bodyHeight));
            DrawLog(new Rect(margin, top + bodyHeight + 12f, width - margin * 2, logHeight));
        }

        private void DrawHeader(Rect rect)
        {
            GUI.Box(rect, GUIContent.none, _panel);
            GUI.Label(new Rect(rect.x + 16f, rect.y + 10f, 500f, 28f), "HolySheet Demo", _title);
            string status = LastError != null
                ? "Load failed: " + LastError
                : Tables == null
                    ? "Not loaded"
                    : Summary(Tables) + "  ·  content " + Tables.Manifest.Content.Substring(0, 12) + "  ·  " + LastLoadMilliseconds.ToString("F1") + " ms  ·  " + (IsWatching ? "watching, " + ReloadCount + " reloads" : "not watching");
            GUI.Label(new Rect(rect.x + 16f, rect.y + 40f, rect.width - 480f, 22f), status, LastError != null ? _text : _dim);

            float x = rect.xMax - 16f;
            x -= 150f;
            if (GUI.Button(new Rect(x, rect.y + 20f, 150f, 32f), "Tamper test", _button))
            {
                RunTamperTest();
            }

            x -= 160f;
            if (GUI.Button(new Rect(x, rect.y + 20f, 150f, 32f), parallelLoad ? "Parallel: on" : "Parallel: off", _button))
            {
                parallelLoad = !parallelLoad;
                Load();
            }

            x -= 130f;
            if (GUI.Button(new Rect(x, rect.y + 20f, 120f, 32f), "Reload", _button))
            {
                Load();
            }
        }

        private void DrawTableList(Rect rect)
        {
            GUI.Box(rect, GUIContent.none, _panel);
            if (Tables == null)
            {
                return;
            }

            IReadOnlyList<ITable> tables = Tables.Tables;
            _selected = Mathf.Clamp(_selected, 0, tables.Count - 1);
            // A real project has dozens of tables, so the list scrolls.
            var view = new Rect(rect.x + 4f, rect.y + 6f, rect.width - 8f, rect.height - 12f);
            var content = new Rect(0f, 0f, view.width - 16f, tables.Count * 46f);
            _tablesScroll = GUI.BeginScrollView(view, _tablesScroll, content);
            for (int index = 0; index < tables.Count; index++)
            {
                var row = new Rect(4f, index * 46f, content.width - 4f, 42f);
                if (index == _selected)
                {
                    GUI.DrawTexture(row, _rowTexture!);
                }

                if (GUI.Button(row, GUIContent.none, GUIStyle.none))
                {
                    _selected = index;
                    _rowsScroll = Vector2.zero;
                }

                GUI.Label(new Rect(row.x + 10f, row.y + 3f, row.width - 20f, 20f), tables[index].Name, _text);
                GUI.Label(new Rect(row.x + 10f, row.y + 21f, row.width - 20f, 18f), tables[index].Count + " rows  ·  " + tables[index].Entry.Bytes + " bytes  ·  " + tables[index].Entry.Hash.Substring(0, 8), _dim);
            }

            GUI.EndScrollView();
        }

        private void DrawRows(Rect rect)
        {
            GUI.Box(rect, GUIContent.none, _panel);
            if (Tables == null)
            {
                GUI.Label(new Rect(rect.x + 16f, rect.y + 16f, rect.width - 32f, 60f), "Build the tables first: Tools > HolySheet > Build Tables.", _text);
                return;
            }

            (string[] columns, float[] widths, List<string[]> rows) = Describe(Tables, Tables.Tables[_selected].Name);
            float totalWidth = widths.Sum() + 16f;
            var header = new Rect(rect.x + 8f, rect.y + 8f, rect.width - 16f, 26f);
            var view = new Rect(rect.x + 8f, header.yMax, rect.width - 16f, rect.height - 16f - 26f);
            var content = new Rect(0f, 0f, Mathf.Max(totalWidth, view.width - 16f), rows.Count * 26f);

            // The header scrolls sideways with the rows but stays at the top.
            GUI.BeginGroup(header);
            float x = -_rowsScroll.x;
            for (int column = 0; column < columns.Length; column++)
            {
                GUI.Label(new Rect(x + 6f, 3f, widths[column] - 8f, 20f), columns[column], _head);
                x += widths[column];
            }

            GUI.EndGroup();

            _rowsScroll = GUI.BeginScrollView(view, _rowsScroll, content);
            for (int index = 0; index < rows.Count; index++)
            {
                if (index % 2 == 0)
                {
                    GUI.DrawTexture(new Rect(0f, index * 26f, content.width, 26f), _rowTexture!, ScaleMode.StretchToFill, true, 0f, new Color(1f, 1f, 1f, 0.35f), 0f, 0f);
                }

                x = 0f;
                for (int column = 0; column < columns.Length; column++)
                {
                    GUI.Label(new Rect(x + 6f, index * 26f + 4f, widths[column] - 8f, 20f), rows[index][column], _cell);
                    x += widths[column];
                }
            }

            GUI.EndScrollView();
        }

        private void DrawLog(Rect rect)
        {
            GUI.Box(rect, GUIContent.none, _panel);
            var view = new Rect(rect.x + 12f, rect.y + 8f, rect.width - 24f, rect.height - 16f);
            var content = new Rect(0f, 0f, view.width - 20f, _log.Count * 20f);
            _logScroll = GUI.BeginScrollView(view, _logScroll, content);
            for (int index = 0; index < _log.Count; index++)
            {
                GUI.Label(new Rect(0f, index * 20f, content.width, 20f), _log[index], _dim);
            }

            GUI.EndScrollView();
        }

        /// <summary>
        /// A line built the way game code reads tables: typed fields, and references followed through the table they point at.
        /// </summary>
        public static string Summary(DemoTables tables)
        {
            GameRow game = tables.Game.Value;
            MapRow start = tables.Map.Get(game.StartMap);
            string region = tables.Region.Get(start.Region).Name;
            string starter = string.Join(", ", game.StarterItems.Select(id => tables.Item.Get(id).Name));
            return "v" + game.Version + "  ·  max level " + game.MaxLevel + "  ·  start: " + start.Name + " (" + region + ")  ·  " + starter;
        }

        /// <summary>
        /// The rows of any table as text: one column per property of the generated row class, in declaration order.
        /// Arrays are joined with " | ", references show as Table(key), enums by name.
        /// </summary>
        public static (string[] Columns, float[] Widths, List<string[]> Rows) Describe(DemoTables tables, string tableName)
        {
            ITable table = tables.Tables.First(candidate => candidate.Name == tableName);
            var rows = (IEnumerable)table.GetType().GetProperty("Rows").GetValue(table);
            PropertyInfo[] properties = null;
            var result = new List<string[]>();
            foreach (object row in rows)
            {
                properties ??= row.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(property => property.MetadataToken).ToArray();
                result.Add(properties.Select(property => Format(property.GetValue(row))).ToArray());
            }

            properties ??= Array.Empty<PropertyInfo>();
            string[] columns = properties.Select(property => property.Name).ToArray();
            float[] widths = properties.Select(property => property.Name == "Name" || property.Name == "Description" ? 180f : 110f).ToArray();
            return (columns, widths, result);
        }

        private static string Format(object value)
        {
            switch (value)
            {
                case null:
                    return "";
                case string text:
                    return text.Replace('\n', ' ');
                case float number:
                    return number.ToString("0.###", CultureInfo.InvariantCulture);
                case double number:
                    return number.ToString("0.###", CultureInfo.InvariantCulture);
                case DateTime date:
                    return date.TimeOfDay == TimeSpan.Zero ? date.ToString("yyyy-MM-dd") : date.ToString("yyyy-MM-dd HH:mm");
                case IEnumerable items:
                    return string.Join(" | ", items.Cast<object>().Select(Format));
                default:
                    return value.ToString();
            }
        }

        private void EnsureStyles()
        {
            if (_title != null && _panelTexture != null)
            {
                return;
            }

            _panelTexture = Solid(new Color(0.16f, 0.17f, 0.20f, 0.96f));
            _rowTexture = Solid(new Color(0.26f, 0.40f, 0.62f, 0.55f));
            _panel = new GUIStyle { normal = { background = _panelTexture } };
            _title = new GUIStyle(GUI.skin.label) { fontSize = 20, fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };
            _text = new GUIStyle(GUI.skin.label) { fontSize = 14, wordWrap = false, clipping = TextClipping.Clip, normal = { textColor = new Color(0.92f, 0.93f, 0.95f) } };
            _dim = new GUIStyle(_text) { fontSize = 12, normal = { textColor = new Color(0.62f, 0.66f, 0.72f) } };
            _cell = new GUIStyle(_text) { fontSize = 13 };
            _head = new GUIStyle(_text) { fontSize = 12, fontStyle = FontStyle.Bold, normal = { textColor = new Color(0.45f, 0.70f, 1f) } };
            _button = new GUIStyle(GUI.skin.button) { fontSize = 13 };
        }

        private static Texture2D Solid(Color color)
        {
            var texture = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
            texture.SetPixel(0, 0, color);
            texture.Apply();
            return texture;
        }
    }
}
