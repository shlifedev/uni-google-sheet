#nullable enable
using System.IO;
using HolySheet.Core.Config;
using HolySheet.Editor;
using UnityEditor;
using UnityEngine;

namespace HolySheet.Demo.Editor
{
    /// <summary>
    /// Points the project's holysheet.json at this demo, wherever the sample was imported to, so that Build Tables rebuilds
    /// the demo's code and data from Sheets/Game.xlsx.
    /// </summary>
    public static class HolySheetDemoSetup
    {
        [MenuItem("Tools/HolySheet/Demo/Use the demo config", priority = 100)]
        public static void UseDemoConfig()
        {
            if (HolySheetProject.HasConfig && !EditorUtility.DisplayDialog("HolySheet Demo",
                    "This project already has a holysheet.json. Replace it with the demo's config?", "Replace", "Cancel"))
            {
                return;
            }

            HolySheetConfig config = CreateConfig();
            HolySheetProject.SaveConfig(config);
            Debug.Log("[HolySheet Demo] holysheet.json now builds the demo tables from " + config.Sources[0].Path + ". Run Tools > HolySheet > Build Tables.");
        }

        /// <summary>The config that builds the demo tables, with paths relative to the project root.</summary>
        public static HolySheetConfig CreateConfig()
        {
            string folder = FindDemoFolder();
            var config = new HolySheetConfig { BaseDirectory = HolySheetProject.RootDirectory };
            config.Sources.Add(new SourceConfig { Type = SourceType.Xlsx, Path = folder + "/Sheets" });
            config.Sources.Add(new SourceConfig { Type = SourceType.Csv, Path = folder + "/Sheets/Localization" });
            config.Output.Code = folder + "/Generated";
            // A Resources folder, so the demo also runs in a player on every platform without extra setup.
            config.Output.Data = folder + "/Resources/HolySheetDemo";
            config.Codegen.Namespace = "HolySheet.Demo.Data";
            config.Codegen.TablesClass = "DemoTables";
            config.Codegen.Serializer = "memorypack";
            return config;
        }

        // The demo folder is two levels above this script: Demo/Scripts/Editor/HolySheetDemoSetup.cs.
        private static string FindDemoFolder()
        {
            foreach (string guid in AssetDatabase.FindAssets("HolySheetDemoSetup t:MonoScript"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileName(path) == "HolySheetDemoSetup.cs")
                {
                    string scripts = Path.GetDirectoryName(Path.GetDirectoryName(path)!)!;
                    return Path.GetDirectoryName(scripts)!.Replace('\\', '/');
                }
            }

            return "Assets/Samples/Demo";
        }
    }
}
