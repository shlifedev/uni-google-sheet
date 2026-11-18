using System.Collections;
using NUnit.Framework;
using UGS.Demo;
using UGS.Demo.Data;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace UGS.Dev.Tests
{
    /// <summary>Plays the demo scene and checks that it did what a person pressing Play would see.</summary>
    public class DemoSceneTests
    {
        private const string ScenePath = "Assets/Samples/Demo/Demo.unity";

        [UnityTest]
        public IEnumerator The_demo_scene_loads_the_tables_and_detects_tampering()
        {
#if UNITY_EDITOR
            yield return UnityEditor.SceneManagement.EditorSceneManager.LoadSceneAsyncInPlayMode(ScenePath, new LoadSceneParameters(LoadSceneMode.Single));
#else
            yield return SceneManager.LoadSceneAsync("Demo");
#endif
            // Start runs on the first frame after the scene loads.
            yield return null;
            yield return null;

            UgsDemo demo = Object.FindFirstObjectByType<UgsDemo>();
            Assert.IsNotNull(demo, "The scene has no UgsDemo component.");
            Assert.IsNull(demo.LastError, demo.LastError);
            DemoTables tables = demo.Tables;
            Assert.IsNotNull(tables);

            Assert.AreEqual(21, tables.Tables.Count);
            Assert.AreEqual(44, tables.Item.Count);
            Assert.AreEqual("작은 회복 물약", tables.Item.Get(10001).Name);
            Assert.AreEqual(EItemType.Material, tables.Item.GetByCode("iron_ore").Type);
            Assert.AreEqual("들판 일반", tables.Drop.Get(tables.Monster.Get((int)EMonsterId.Goblin).Drop).Name);
            Assert.AreEqual(100, tables.Game.Value.MaxLevel);
            Assert.AreEqual("게임 시작", tables.Text.Get("ui.start").Ko);
            StringAssert.Contains("start: 엘더 마을 (엘더우드)", UgsDemo.Summary(tables));
#if UNITY_EDITOR
            Assert.IsTrue(demo.IsWatching, "In the editor the demo should be watching its data folder.");
#endif
            // The demo's own description of each table is what its UI draws.
            Assert.AreEqual(44, UgsDemo.Describe(tables, ItemTable.TableName).Rows.Count);
            Assert.AreEqual(16, UgsDemo.Describe(tables, MonsterTable.TableName).Rows.Count);
            foreach (ITable table in tables.Tables)
            {
                Assert.AreEqual(table.Count, UgsDemo.Describe(tables, table.Name).Rows.Count, table.Name);
            }

            string refused = demo.RunTamperTest();
            Assert.IsNotNull(refused, "A modified data file was loaded without complaint.");
            StringAssert.StartsWith("HashMismatch", refused);

            // Loading again in parallel gives the same data.
            string content = tables.Manifest.Content;
            demo.Load();
            Assert.IsNull(demo.LastError, demo.LastError);
            Assert.AreEqual(content, demo.Tables.Manifest.Content);

            // Let the UI draw for a few frames; any exception in OnGUI fails the test through the log.
            for (int frame = 0; frame < 5; frame++)
            {
                yield return null;
            }
        }
    }
}
