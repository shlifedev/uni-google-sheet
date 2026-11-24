using HolySheet.Demo;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace HolySheet.Dev
{
    public static class DemoSceneBuilder
    {
        public const string ScenePath = "Assets/Samples/Demo/Demo.unity";

        public static void Build()
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var cameraObject = new GameObject("Main Camera") { tag = "MainCamera" };
            var camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.09f, 0.10f, 0.12f);
            camera.orthographic = true;
            cameraObject.transform.position = new Vector3(0f, 0f, -10f);

            var demo = new GameObject("HolySheet Demo");
            demo.AddComponent<HolySheetDemo>();

            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            AssetDatabase.SaveAssets();
            Debug.Log("[HolySheet.Dev] wrote " + ScenePath);
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(0);
            }
        }
    }
}
