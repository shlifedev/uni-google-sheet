using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using HolySheet.Core.Csv;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace HolySheet.Dev
{
    public static class DevTools
    {
        public static void ConfigurePlugins()
        {
            const string package = "Packages/com.lumera.holysheet/Editor/Plugins/";
            foreach (string name in new[] { "HolySheet.Csv.Sep.dll", "Sep.dll", "csFastFloat.dll" })
            {
                Configure(package + "CoreClr/" + name, editor: true, autoReference: false, constraints: new[] { "UNITY_7000_0_OR_NEWER" });
            }

            const string generator = "Assets/Plugins/MemoryPack/Analyzers/MemoryPack.Generator.dll";
            Configure(generator, editor: false, autoReference: false, constraints: new string[0]);
            AssetDatabase.SetLabels(AssetDatabase.LoadMainAssetAtPath(generator), new[] { "RoslynAnalyzer" });
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[HolySheet.Dev] plugins configured");
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(0);
            }
        }

        private static void Configure(string path, bool editor, bool autoReference, string[] constraints)
        {
            var importer = (PluginImporter)AssetImporter.GetAtPath(path);
            if (importer == null)
            {
                throw new FileNotFoundException("No plugin at " + path);
            }

            importer.SetCompatibleWithAnyPlatform(false);
            importer.SetCompatibleWithEditor(editor);
            importer.DefineConstraints = constraints;
            var serialized = new SerializedObject(importer);
            SerializedProperty explicitly = serialized.FindProperty("m_IsExplicitlyReferenced");
            if (explicitly != null)
            {
                explicitly.boolValue = !autoReference;
            }

            SerializedProperty validate = serialized.FindProperty("m_ValidateReferences");
            if (validate != null)
            {
                validate.boolValue = autoReference;
            }

            serialized.ApplyModifiedPropertiesWithoutUndo();
            importer.SaveAndReimport();
            Debug.Log("[HolySheet.Dev] " + path + " explicit=" + (explicitly != null) + " validate=" + (validate != null));
        }

        public static void CaptureWindow()
        {
            string directory = Environment.GetEnvironmentVariable("HOLYSHEET_CAPTURE_DIR") ?? "Captures";
            if (Environment.GetEnvironmentVariable("HOLYSHEET_CAPTURE_EXPAND") == "1")
            {
                foreach (string section in new[] { "sources", "output", "build", "signing" })
                {
                    SessionState.SetBool("HolySheet.Window.Section." + section, true);
                }
            }

            Directory.CreateDirectory(directory);
            HolySheet.Editor.HolySheetProject.RunBlocking(commit: false);
            HolySheet.Editor.HolySheetWindow.Open();
            var window = EditorWindow.GetWindow<HolySheet.Editor.HolySheetWindow>();
            string[] size = (Environment.GetEnvironmentVariable("HOLYSHEET_CAPTURE_SIZE") ?? "1040x680").Split('x');
            window.position = new Rect(80, 80, float.Parse(size[0]), float.Parse(size[1]));
            var pages = HolySheet.Editor.HolySheetWindow.PageNames;
            int index = 0;
            int frames = 0;
            window.SelectPage(pages[0]);
            string filter = Environment.GetEnvironmentVariable("HOLYSHEET_CAPTURE_FILTER");

            void Tick()
            {
                window.Repaint();
                if (frames == 10 && index == 0 && !string.IsNullOrEmpty(filter))
                {
                    var search = window.rootVisualElement.Q<UnityEditor.UIElements.ToolbarSearchField>(className: "holysheet-search");
                    if (search != null)
                    {
                        search.value = filter;
                    }
                }

                string findText = Environment.GetEnvironmentVariable("HOLYSHEET_CAPTURE_FIND");
                if (frames == 20 && index == 0 && !string.IsNullOrEmpty(findText) && window.OpenFind())
                {
                    if (Environment.GetEnvironmentVariable("HOLYSHEET_CAPTURE_FIND_ALL") == "1")
                    {
                        window.rootVisualElement.Q<UnityEditor.UIElements.ToolbarToggle>(className: "holysheet-find__all").value = true;
                    }

                    window.rootVisualElement.Q<UnityEditor.UIElements.ToolbarSearchField>(className: "holysheet-find__field").value = findText;
                }

                if (++frames < 40)
                {
                    return;
                }

                try
                {
                    File.WriteAllBytes(Path.Combine(directory, "window-" + (index + 1) + "-" + pages[index].ToLowerInvariant() + ".png"), Grab(window));
                }
                catch (Exception exception)
                {
                    Debug.LogError("[HolySheet.Dev] capture failed: " + exception);
                    EditorApplication.update -= Tick;
                    EditorApplication.Exit(1);
                    return;
                }

                frames = 0;
                index++;
                if (index >= pages.Count)
                {
                    EditorApplication.update -= Tick;
                    EditorApplication.Exit(0);
                    return;
                }

                window.SelectPage(pages[index]);
            }

            EditorApplication.update += Tick;
        }

        private static byte[] Grab(EditorWindow window)
        {
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
            object view = typeof(EditorWindow).GetField("m_Parent", flags).GetValue(window);
            System.Reflection.MethodInfo grab = view.GetType().GetMethod("GrabPixels", flags);
            float scale = EditorGUIUtility.pixelsPerPoint;
            int width = Mathf.RoundToInt(window.position.width * scale);
            int height = Mathf.RoundToInt(window.position.height * scale);
            var target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            grab.Invoke(view, new object[] { target, new Rect(0, 0, width, height) });

            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = target;
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            texture.Apply();
            RenderTexture.active = previous;

            Color32[] pixels = texture.GetPixels32();
            var flipped = new Color32[pixels.Length];
            for (int row = 0; row < height; row++)
            {
                Array.Copy(pixels, row * width, flipped, (height - 1 - row) * width, width);
            }

            texture.SetPixels32(flipped);
            texture.Apply();
            byte[] png = texture.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(texture);
            target.Release();
            return png;
        }

        public static void ListIcons()
        {
            var names = new System.Collections.Generic.SortedSet<string>();
            var method = typeof(EditorGUIUtility).GetMethod("GetEditorAssetBundle", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var bundle = (AssetBundle)method.Invoke(null, null);
            foreach (string path in bundle.GetAllAssetNames())
            {
                if (path.StartsWith("icons/"))
                {
                    names.Add(path);
                }
            }

            File.WriteAllLines("icons.txt", names);
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(0);
            }
        }

        public static void DrawIconSheet()
        {
            string[] names = (Environment.GetEnvironmentVariable("HOLYSHEET_ICON_CANDIDATES") ?? "").Split(',');
            const int cell = 40;
            var sheet = new Texture2D(cell * names.Length, cell, TextureFormat.RGBA32, false);
            Color[] background = Enumerable.Repeat(new Color(0.22f, 0.22f, 0.22f, 1f), cell * names.Length * cell).ToArray();
            sheet.SetPixels(background);
            for (int index = 0; index < names.Length; index++)
            {
                var texture = EditorGUIUtility.IconContent(names[index].Trim()).image as Texture2D;
                if (texture == null)
                {
                    continue;
                }

                var target = RenderTexture.GetTemporary(32, 32, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(texture, target);
                RenderTexture previous = RenderTexture.active;
                RenderTexture.active = target;
                var copy = new Texture2D(32, 32, TextureFormat.RGBA32, false);
                copy.ReadPixels(new Rect(0, 0, 32, 32), 0, 0);
                copy.Apply();
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(target);
                for (int y = 0; y < 32; y++)
                {
                    for (int x = 0; x < 32; x++)
                    {
                        Color pixel = copy.GetPixel(x, y);
                        Color under = sheet.GetPixel(index * cell + 4 + x, 4 + y);
                        sheet.SetPixel(index * cell + 4 + x, 4 + y, Color.Lerp(under, pixel, pixel.a));
                    }
                }
            }

            sheet.Apply();
            File.WriteAllBytes(Environment.GetEnvironmentVariable("HOLYSHEET_ICON_SHEET") ?? "icon-sheet.png", sheet.EncodeToPNG());
            EditorApplication.Exit(0);
        }

        public static void BenchWindow()
        {
            string directory = Environment.GetEnvironmentVariable("HOLYSHEET_CAPTURE_DIR") ?? "Captures";
            string tableName = Environment.GetEnvironmentVariable("HOLYSHEET_BENCH_TABLE") ?? "Huge";
            Directory.CreateDirectory(directory);
            var report = new StringBuilder();
            report.AppendLine("unity=" + Application.unityVersion + " runtime=" + RuntimeInformation.FrameworkDescription);

            long before = GC.GetTotalMemory(true);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            HolySheet.Editor.HolySheetRunResult result = HolySheet.Editor.HolySheetProject.RunBlocking(commit: false);
            watch.Stop();
            long after = GC.GetTotalMemory(true);
            report.AppendLine($"check: {watch.Elapsed.TotalMilliseconds:F0} ms (read {result.Build.ReadTime.TotalMilliseconds:F0}, build {result.Build.BuildTime.TotalMilliseconds:F0}), rows {result.Build.RawTables.Sum(t => t.Rows.Count)}, csv {result.Build.CsvBackend}");
            report.AppendLine($"managed heap: {before / 1048576.0:F0} MB before, {after / 1048576.0:F0} MB after the check (the window keeps the last result)");

            HolySheet.Editor.HolySheetWindow.Open();
            var window = EditorWindow.GetWindow<HolySheet.Editor.HolySheetWindow>();
            window.position = new Rect(80, 80, 1040, 640);
            window.SelectPage("Tables");

            var steps = new System.Collections.Generic.List<(string Name, Action Act)>();
            MultiColumnListView grid = null;
            int rowCount = 0;
            System.Diagnostics.Stopwatch selectWatch = null;
            steps.Add(("select table", () =>
            {
                var search = window.rootVisualElement.Q<UnityEditor.UIElements.ToolbarSearchField>();
                selectWatch = System.Diagnostics.Stopwatch.StartNew();
                search.value = tableName;
            }));
            steps.Add(("first frame after selecting", () =>
            {
                grid = window.rootVisualElement.Q<MultiColumnListView>();
                rowCount = grid.itemsSource.Count;
                report.AppendLine($"table '{tableName}': {rowCount} rows, {grid.columns.Count - 1} columns; shown {selectWatch.Elapsed.TotalMilliseconds:F0} ms after selecting");
            }));
            foreach (double fraction in new[] { 0.25, 0.5, 0.75, 1.0, 0.0, 1.0 })
            {
                double f = fraction;
                steps.Add(("scroll to " + f, () => grid.ScrollToItem(Math.Max(0, (int)(rowCount * f) - 1))));
            }

            var frameTimes = new System.Collections.Generic.List<double>();
            double last = EditorApplication.timeSinceStartup;
            int step = 0;
            int wait = 0;

            void Tick()
            {
                window.Repaint();
                double now = EditorApplication.timeSinceStartup;
                if (step >= 2)
                {
                    frameTimes.Add((now - last) * 1000);
                }

                last = now;
                if (++wait < 6)
                {
                    return;
                }

                wait = 0;
                if (step < steps.Count)
                {
                    var stepWatch = System.Diagnostics.Stopwatch.StartNew();
                    try
                    {
                        steps[step].Act();
                    }
                    catch (Exception exception)
                    {
                        EditorApplication.update -= Tick;
                        report.AppendLine("FAILED at '" + steps[step].Name + "': " + exception);
                        File.WriteAllText(Path.Combine(directory, "bench.txt"), report.ToString());
                        EditorApplication.Exit(1);
                        return;
                    }

                    if (step >= 2)
                    {
                        report.AppendLine($"{steps[step].Name}: {stepWatch.Elapsed.TotalMilliseconds:F1} ms in the call");
                    }

                    step++;
                    return;
                }

                EditorApplication.update -= Tick;
                frameTimes.Sort();
                report.AppendLine($"frames while scrolling: {frameTimes.Count}, median {frameTimes[frameTimes.Count / 2]:F1} ms, worst {frameTimes[frameTimes.Count - 1]:F1} ms");
                var visible = grid.Query<Label>(className: "holysheet-cell--row").ToList().Where(label => label.resolvedStyle.display == DisplayStyle.Flex && label.text.Length > 0).Select(label => label.text).ToList();
                report.AppendLine("row numbers on screen at the end: " + string.Join(",", visible.Take(3)) + " … " + string.Join(",", visible.Skip(Math.Max(0, visible.Count - 3))));
                report.AppendLine($"row cells alive: {grid.Query<Label>(className: "holysheet-cell--row").ToList().Count} (virtualized: only rows on screen exist)");
                File.WriteAllText(Path.Combine(directory, "bench.txt"), report.ToString());
                File.WriteAllBytes(Path.Combine(directory, "bench-last-rows.png"), Grab(window));
                EditorApplication.Exit(0);
            }

            EditorApplication.update += Tick;
        }

        public static void Report()
        {
            var report = new StringBuilder();
            report.AppendLine("unity=" + Application.unityVersion);
            report.AppendLine("runtime=" + RuntimeInformation.FrameworkDescription);
            report.AppendLine("csvSep=" + (CsvBackends.Fast?.Name ?? "none"));
            report.AppendLine("csvSepReason=" + CsvBackends.FastUnavailableReason);
            report.AppendLine("csvAuto=" + CsvBackends.Resolve("auto").Name);
            report.AppendLine("assemblies=" + string.Join(",", AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name).Where(n => n.StartsWith("HolySheet") || n == "Sep" || n.StartsWith("MemoryPack") || n == "csFastFloat").OrderBy(n => n)));
            File.WriteAllText("dev-report.txt", report.ToString());
            Debug.Log("[HolySheet.Dev]\n" + report);
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(0);
            }
        }
    }
}
