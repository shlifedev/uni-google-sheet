#nullable enable
using System.IO;
using HolySheet.Core.Config;
using HolySheet.Editor;
using UnityEditor;
using UnityEngine;

namespace HolySheet.Demo.Editor
{
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

        public static HolySheetConfig CreateConfig()
        {
            string folder = FindDemoFolder();
            var config = new HolySheetConfig { BaseDirectory = HolySheetProject.RootDirectory };
            config.Sources.Add(new SourceConfig { Type = SourceType.Xlsx, Path = folder + "/Sheets" });
            config.Sources.Add(new SourceConfig { Type = SourceType.Csv, Path = folder + "/Sheets/Localization" });
            config.Output.Code = folder + "/Generated";
            config.Output.Data = folder + "/Resources/HolySheetDemo";
            config.Codegen.Namespace = "HolySheet.Demo.Data";
            config.Codegen.TablesClass = "DemoTables";
            config.Codegen.Serializer = "memorypack";
            return config;
        }

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
