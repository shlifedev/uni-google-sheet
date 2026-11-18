#nullable enable
using System.IO;
using UGS.Core.Config;
using UGS.Editor;
using UnityEditor;
using UnityEngine;

namespace UGS.Demo.Editor
{
    /// <summary>
    /// Points the project's ugs.json at this demo, wherever the sample was imported to, so that Build Tables rebuilds
    /// the demo's code and data from Sheets/Game.xlsx.
    /// </summary>
    public static class UgsDemoSetup
    {
        [MenuItem("Tools/UGS/Demo/Use the demo config", priority = 100)]
        public static void UseDemoConfig()
        {
            if (UgsProject.HasConfig && !EditorUtility.DisplayDialog("UGS Demo",
                    "This project already has a ugs.json. Replace it with the demo's config?", "Replace", "Cancel"))
            {
                return;
            }

            UgsConfig config = CreateConfig();
            UgsProject.SaveConfig(config);
            Debug.Log("[UGS Demo] ugs.json now builds the demo tables from " + config.Sources[0].Path + ". Run Tools > UGS > Build Tables.");
        }

        /// <summary>The config that builds the demo tables, with paths relative to the project root.</summary>
        public static UgsConfig CreateConfig()
        {
            string folder = FindDemoFolder();
            var config = new UgsConfig { BaseDirectory = UgsProject.RootDirectory };
            config.Sources.Add(new SourceConfig { Type = SourceType.Xlsx, Path = folder + "/Sheets" });
            config.Sources.Add(new SourceConfig { Type = SourceType.Csv, Path = folder + "/Sheets/Localization" });
            config.Output.Code = folder + "/Generated";
            // A Resources folder, so the demo also runs in a player on every platform without extra setup.
            config.Output.Data = folder + "/Resources/UgsDemo";
            config.Codegen.Namespace = "UGS.Demo.Data";
            config.Codegen.TablesClass = "DemoTables";
            config.Codegen.Serializer = "memorypack";
            return config;
        }

        // The demo folder is two levels above this script: Demo/Scripts/Editor/UgsDemoSetup.cs.
        private static string FindDemoFolder()
        {
            foreach (string guid in AssetDatabase.FindAssets("UgsDemoSetup t:MonoScript"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileName(path) == "UgsDemoSetup.cs")
                {
                    string scripts = Path.GetDirectoryName(Path.GetDirectoryName(path)!)!;
                    return Path.GetDirectoryName(scripts)!.Replace('\\', '/');
                }
            }

            return "Assets/Samples/Demo";
        }
    }
}
