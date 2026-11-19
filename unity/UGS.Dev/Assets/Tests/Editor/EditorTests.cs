using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using NUnit.Framework;
using UGS.Core;
using UGS.Core.Config;
using UGS.Core.Csv;
using UGS.Core.Output;
using UGS.Demo.Data;
using UGS.Demo.Editor;
using UGS.Editor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace UGS.Dev.Tests
{
    /// <summary>
    /// What only a real Unity editor can prove: that the engine DLL loads and builds inside it, that the generated code
    /// compiles with Unity's compiler and reads its data through the serializer, and that the runtime works on Unity's runtime.
    /// The same tests run on Unity 6 (Mono) and Unity 7 (CoreCLR).
    /// </summary>
    public class EditorTests
    {
        private string _temp;

        [SetUp]
        public void SetUp()
        {
            // The window's text is checked in English, whatever language the person running the tests chose.
            UgsText.Override = UgsLanguage.English;
            _temp = Path.Combine(Path.GetTempPath(), "ugs-unity-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_temp);
        }

        [TearDown]
        public void TearDown()
        {
            UgsText.Override = null;
            try
            {
                Directory.Delete(_temp, true);
            }
            catch (IOException)
            {
            }
        }

        private static string DataDirectory => Path.Combine(UgsProject.RootDirectory, "Assets/Samples/Demo/Resources/UgsDemo");

        private static string CodeDirectory => Path.Combine(UgsProject.RootDirectory, "Assets/Samples/Demo/Generated");

        private static string Errors(BuildResult result) => string.Join("\n", result.Diagnostics.Items.Select(item => item.ToString()));

        // The demo workbook written out as CSV, one file per sheet: the same tables through the CSV path.
        private UgsConfig WriteDemoAsCsv(string name, Func<string, string, string> edit = null)
        {
            BuildResult source = TableBuild.Run(UgsDemoSetup.CreateConfig());
            Assert.IsTrue(source.Success, Errors(source));
            string sheets = Path.Combine(_temp, name, "Sheets");
            Directory.CreateDirectory(sheets);
            foreach (Sheet sheet in source.Workbooks.SelectMany(workbook => workbook.Sheets))
            {
                string csv = CsvWriter.Write(sheet.ToGrid());
                File.WriteAllText(Path.Combine(sheets, sheet.Name + ".csv"), edit == null ? csv : edit(sheet.Name, csv));
            }

            UgsConfig config = UgsDemoSetup.CreateConfig();
            config.BaseDirectory = Path.Combine(_temp, name);
            config.Sources.Clear();
            config.Sources.Add(new SourceConfig { Type = SourceType.Csv, Path = "Sheets" });
            config.Output.Code = "Code";
            config.Output.Data = "Data";
            return config;
        }

        private static string Commit(UgsConfig config)
        {
            BuildResult result = TableBuild.Run(config);
            Assert.IsTrue(result.Success, Errors(result));
            TableBuild.Commit(config, result);
            return config.Resolve(config.Output.Data);
        }

        [Test]
        public void The_csv_backend_matches_the_editor_runtime()
        {
            string runtime = RuntimeInformation.FrameworkDescription;
            Debug.Log("[UGS.Dev.Tests] Unity " + Application.unityVersion + " on " + runtime + ", sep available: " + (CsvBackends.Fast != null));
            // "auto" is the built-in parser on every editor.
            Assert.AreEqual("builtin", CsvBackends.Resolve("auto").Name);
#if UNITY_7000_0_OR_NEWER
            // Unity 7 runs the editor on CoreCLR, where the Sep backend loads and can be asked for by name.
            StringAssert.StartsWith(".NET", runtime);
            Assert.IsNotNull(CsvBackends.Fast, CsvBackends.FastUnavailableReason);
            Assert.AreEqual("sep", CsvBackends.Resolve("sep").Name);
            Assert.IsTrue(UgsProject.IsSepAvailable);
#else
            // Unity 6 runs on Mono. The Sep DLLs are excluded by their define constraint, so asking for Sep is an error.
            StringAssert.StartsWith("Mono", runtime);
            Assert.IsNull(CsvBackends.Fast);
            Assert.Throws<NotSupportedException>(() => CsvBackends.Resolve("sep"));
            Assert.IsFalse(UgsProject.IsSepAvailable);
            Assert.IsFalse(AppDomain.CurrentDomain.GetAssemblies().Any(assembly => assembly.GetName().Name == "Sep"), "Sep.dll must not load on Mono.");
#endif
        }

        [Test]
        public void Building_in_this_editor_gives_the_checked_in_files_byte_for_byte()
        {
            // The checked-in demo output was written by the .NET CLI. The engine inside this editor must produce exactly the same.
            BuildResult result = TableBuild.Run(UgsDemoSetup.CreateConfig());
            Assert.IsTrue(result.Success, Errors(result));
            Assert.AreEqual(21, result.Tables.Count);
            foreach (KeyValuePair<string, string> file in result.Output.CodeFiles)
            {
                Assert.AreEqual(File.ReadAllText(Path.Combine(CodeDirectory, file.Key)), file.Value, file.Key);
            }

            foreach (KeyValuePair<string, byte[]> file in result.Output.DataFiles)
            {
                CollectionAssert.AreEqual(File.ReadAllBytes(Path.Combine(DataDirectory, file.Key)), file.Value, file.Key);
            }

            Assert.AreEqual(File.ReadAllText(Path.Combine(DataDirectory, "tables.manifest.txt")), result.Output.ManifestText);
            Assert.AreEqual(DemoTables.SchemaHash, result.Output.SchemaHash);
        }

        [Test]
        public void The_same_tables_through_csv_give_the_same_data_as_through_xlsx()
        {
            UgsConfig config = WriteDemoAsCsv("csv");
            config.CsvBackend = "builtin";
            BuildResult builtin = TableBuild.Run(config);
            Assert.IsTrue(builtin.Success, Errors(builtin));
            Assert.AreEqual("builtin", builtin.CsvBackend);
            Assert.AreEqual(DemoTables.SchemaHash, builtin.Output.SchemaHash);
            Assert.AreEqual(DemoTables.Load(DataDirectory).Manifest.Content, builtin.Output.ContentHash);

#if UNITY_7000_0_OR_NEWER
            // On CoreCLR the Sep backend reads the same files and must give exactly the same data.
            config.CsvBackend = "sep";
            BuildResult sep = TableBuild.Run(config);
            Assert.IsTrue(sep.Success, Errors(sep));
            Assert.AreEqual("sep", sep.CsvBackend);
            Assert.AreEqual(builtin.Output.ContentHash, sep.Output.ContentHash);
#else
            config.CsvBackend = "sep";
            BuildResult sep = TableBuild.Run(config);
            Assert.IsFalse(sep.Success);
            StringAssert.Contains("The 'sep' CSV backend is not available", Errors(sep));
#endif
        }

        [Test]
        public void Generated_tables_load_through_the_serializer_on_this_runtime()
        {
            foreach (TableLoadOptions options in new[] { new TableLoadOptions(), new TableLoadOptions { Parallel = true } })
            {
                DemoTables tables = DemoTables.Load(DataDirectory, options);
                Assert.AreEqual(21, tables.Tables.Count);

                // Items: three sheets make one table, Item first and then the child sheets in name order (Item.Equipment, Item.Material).
                // The row starting with ~ is left out.
                Assert.AreEqual(44, tables.Item.Count);
                ItemRow potion = tables.Item.Get(10001);
                Assert.AreEqual("작은 회복 물약", potion.Name);
                Assert.AreEqual(EItemType.Consumable, potion.Type);
                Assert.AreEqual(EItemTag.Tradable | EItemTag.Stackable | EItemTag.Sellable, potion.Tags);
                Assert.AreEqual(200, potion.MaxStack);
                Assert.IsFalse(tables.Item.Contains(10008));
                Assert.AreSame(tables.Item.Get(30101), tables.Item.GetByCode("iron_ore"));
                Assert.AreEqual(EItemType.Material, tables.Item.GetByCode("iron_ore").Type);
                Assert.AreEqual(EItemType.Equipment, tables.Item.Get(20003).Type);
                CollectionAssert.AreEqual(new[] { 10005, 10007, 10102, 21011, 21012, 21013, 21014, 30103, 30106, 30107 }, tables.Item.ListByTier(3).Select(row => row.Id).ToArray());

                // Equipment: references across workbooks, and columns added by Equipment.Col.Upgrade for some rows only.
                EquipmentRow blade = tables.Equipment.Get(20003);
                Assert.AreSame(tables.Item.Get(20003), tables.Item.Get(blade.Item));
                Assert.AreEqual(EEquipSlot.Weapon, blade.Slot);
                Assert.AreEqual(EWeaponType.Sword, blade.WeaponType);
                Assert.AreEqual("기사", tables.Job.Get(blade.Job).Name);
                Assert.AreEqual(15, blade.MaxUpgrade);
                Assert.AreEqual("blessed_stone", tables.Item.Get(blade.UpgradeStone).Code);
                EquipmentRow cap = tables.Equipment.Get(21001);
                Assert.IsFalse(cap.Job.HasValue);
                Assert.IsFalse(cap.UpgradeStone.HasValue);
                Assert.AreEqual(0, cap.MaxUpgrade);
                CollectionAssert.AreEqual(new[] { EStat.MaxHp, EStat.Def }, tables.Equipment.Get(21012).BonusStats.ToArray());
                CollectionAssert.AreEqual(new[] { 240, 20 }, tables.Equipment.Get(21012).BonusValues.ToArray());
                CollectionAssert.AreEqual(new[] { 21011, 21012, 21013, 21014 }, tables.SetBonus.Get(2).Pieces.ToArray());

                // Jobs: Job.Advanced adds the second tier, which points back at the first; the Mapping column made EJobId.
                JobRow knight = tables.Job.Get((int)EJobId.Knight);
                Assert.AreEqual("기사", knight.Name);
                Assert.AreEqual(2, knight.Tier);
                Assert.AreEqual("전사", tables.Job.Get(knight.BaseJob).Name);
                Assert.IsFalse(tables.Job.Get((int)EJobId.Warrior).BaseJob.HasValue);
                CollectionAssert.AreEqual(new[] { EWeaponType.Sword, EWeaponType.Axe }, tables.Job.Get(100).Weapons.ToArray());
                Assert.AreEqual(100, tables.Level.Count);
                Assert.AreEqual(0, tables.Level.Get(1).StatPoints);
                Assert.AreEqual(tables.Job.Get(210), tables.Job.Get(tables.Skill.GetByCode("meteor").Job));
                CollectionAssert.AreEqual(new[] { 2.0f, 2.15f, 2.3f, 2.45f, 2.6f }, tables.Skill.GetByCode("meteor").Damage.ToArray());

                // Monsters: Monster.Boss adds bosses; a monster's drop table lists items from another workbook.
                MonsterRow lord = tables.Monster.Get((int)EMonsterId.AbyssLord);
                Assert.AreEqual(EMonsterRank.RaidBoss, lord.Rank);
                Assert.AreEqual(EAiType.Caster, lord.Ai);
                DropRow drop = tables.Drop.Get(lord.Drop);
                Assert.AreEqual("레이드 보스", drop.Name);
                Assert.AreEqual("용의 비늘", tables.Item.Get(drop.Items[1]).Name);
                CollectionAssert.AreEqual(new[] { 1f, 0.3f, 1f, 0.2f, 0.2f }, drop.Chances.ToArray());
                Assert.AreEqual("심연의 왕좌", tables.Map.Get(tables.Spawn.Get(20).Map).Name);

                // World: dungeons come from Map.Dungeon; maps point at regions.
                MapRow raid = tables.Map.GetByCode("abyss_throne");
                Assert.AreEqual(EMapType.Raid, raid.Type);
                Assert.AreEqual("가라앉은 유적", tables.Region.Get(raid.Region).Name);
                Assert.AreEqual("상인 미라", tables.Npc.Get(2).Name);
                Assert.AreEqual("미라의 잡화점", tables.Shop.Get(tables.Npc.Get(2).Shop).Name);
                Assert.IsFalse(tables.Npc.Get(1).Shop.HasValue);

                // Quests: a chain through Previous, objectives found by index, dailies from Quest.Daily.
                Assert.AreEqual("고블린 소굴", tables.Quest.Get(tables.Quest.Get(5).Previous).Name);
                CollectionAssert.AreEqual(new[] { 6, 7 }, tables.QuestObjective.ListByQuestId(4).Select(objective => objective.Id).ToArray());
                Assert.AreEqual(EQuestType.Weekly, tables.Quest.Get(201).Type);
                Assert.AreEqual(6, tables.ShopGoods.ListByShopId(2).Length);
                Assert.AreEqual(new DateTime(2027, 2, 28, 23, 59, 59, DateTimeKind.Utc), tables.Gacha.Get(2).EndsAt);

                // The config sheet, read sideways, and the localization CSV.
                GameRow game = tables.Game.Value;
                Assert.AreEqual(100, game.MaxLevel);
                Assert.AreEqual("엘더 마을", tables.Map.Get(game.StartMap).Name);
                CollectionAssert.AreEqual(new[] { 10001, 10002, 30101 }, game.StarterItems.ToArray());
                Assert.AreEqual(new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc), game.SeasonStartsAt);
                Assert.IsTrue(game.PvpEnabled);
                Assert.AreEqual("게임 시작", tables.Text.Get("ui.start").Ko);
                Assert.AreEqual("Welcome to \"Elder Village\",\nadventurer.", tables.Text.Get("msg.welcome").En);
            }
        }

        [Test]
        public void Tables_load_from_a_resources_folder()
        {
            DemoTables tables = DemoTables.Load(new ResourcesTableSource("UgsDemo"));
            Assert.AreEqual(16, tables.Monster.Count);

            // Resources is main-thread only; preloading makes a parallel decode possible.
            MemoryTableSource preloaded = TableSource.Preload(new ResourcesTableSource("UgsDemo"), DemoTables.ManifestFileName);
            Assert.AreEqual(22, preloaded.FileNames.Count());
            Assert.AreEqual(tables.Manifest.Content, DemoTables.Load(preloaded, new TableLoadOptions { Parallel = true }).Manifest.Content);
        }

        [Test]
        public void Tampered_data_is_refused_on_this_runtime()
        {
            string data = Commit(WriteDemoAsCsv("tamper"));
            Assert.AreEqual(44, DemoTables.Load(data).Item.Count);
            string path = Path.Combine(data, "Monster.bytes");
            byte[] bytes = File.ReadAllBytes(path);
            bytes[bytes.Length / 2] ^= 0x01;
            File.WriteAllBytes(path, bytes);
            TableLoadException exception = Assert.Throws<TableLoadException>(() => DemoTables.Load(data));
            Assert.AreEqual(TableLoadError.HashMismatch, exception.Error);
            Assert.AreEqual("Monster", exception.TableName);
        }

        [Test]
        public void Signing_and_verification_work_on_this_runtime()
        {
            (string privateKey, string publicKey) = SigningKeys.Generate();
            UgsConfig config = WriteDemoAsCsv("signed");
            File.WriteAllText(Path.Combine(config.BaseDirectory, "key"), privateKey);
            config.Signing.PrivateKeyFile = "key";
            config.Signing.PublicKey = publicKey;
            string data = Commit(config);

            var options = new TableLoadOptions { PublicKey = publicKey };
            Assert.AreEqual(44, DemoTables.Load(data, options).Item.Count);
            Assert.IsTrue(TableSignature.Verify(TableManifest.Parse(File.ReadAllText(Path.Combine(data, "tables.manifest.txt"))), publicKey));

            // Any edit to the manifest, even to a line the loader does not otherwise use, breaks the signature.
            string manifestPath = Path.Combine(data, "tables.manifest.txt");
            File.WriteAllText(manifestPath, File.ReadAllText(manifestPath).Replace("tool ugs/", "tool xyz/"));
            TableLoadException exception = Assert.Throws<TableLoadException>(() => DemoTables.Load(data, options));
            Assert.AreEqual(TableLoadError.SignatureInvalid, exception.Error);
        }

        [UnityTest]
        public IEnumerator A_watcher_reloads_only_what_changed_and_raises_its_event_on_the_main_thread()
        {
            UgsConfig config = WriteDemoAsCsv("watch");
            string data = Commit(config);
            int mainThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
            TableReloadedEventArgs<DemoTables> change = null;
            Exception failure = null;
            int eventThread = -1;

            using (TableWatcher<DemoTables> watcher = DemoTables.Watch(data, new TableWatchOptions { PollInterval = TimeSpan.FromMilliseconds(100), Debounce = TimeSpan.FromMilliseconds(50) }))
            {
                watcher.Reloaded += args =>
                {
                    change = args;
                    eventThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
                };
                watcher.ReloadFailed += exception => failure = exception;
                DemoTables before = watcher.Current;
                Assert.AreEqual("초록 슬라임", before.Monster.Get(1).Name);

                // A designer renames a monster and the tables are built again.
                string monsterPath = Path.Combine(config.BaseDirectory, "Sheets", "Monster.csv");
                string text = File.ReadAllText(monsterPath);
                StringAssert.Contains(",초록 슬라임,", text);
                File.WriteAllText(monsterPath, text.Replace(",초록 슬라임,", ",왕 초록 슬라임,"));
                Commit(config);

                double deadline = UnityEditor.EditorApplication.timeSinceStartup + 20;
                while (change == null && failure == null && UnityEditor.EditorApplication.timeSinceStartup < deadline)
                {
                    yield return null;
                }

                Assert.IsNull(failure, failure?.ToString());
                Assert.IsNotNull(change, "The watcher did not notice the rebuilt data within 20 seconds.");
                Assert.AreEqual(mainThread, eventThread, "Events must arrive on the thread the watcher was created on.");
                CollectionAssert.AreEqual(new[] { "Monster" }, change.ChangedTables.ToArray());
                Assert.AreEqual("왕 초록 슬라임", watcher.Current.Monster.Get(1).Name);
                Assert.AreEqual("초록 슬라임", before.Monster.Get(1).Name, "The earlier snapshot must not change.");
                Assert.AreSame(before.Item, watcher.Current.Item, "Unchanged tables are shared between snapshots.");
                Assert.AreNotSame(before.Monster, watcher.Current.Monster);
            }
        }

        [Test]
        public void The_editor_runner_builds_and_reports()
        {
            // The dev project's ugs.json builds the demo, which is already up to date: nothing should be rewritten.
            UgsRunResult result = UgsProject.RunBlocking(commit: true);
            Assert.IsNull(result.Failure, result.Failure);
            Assert.IsTrue(result.Build.Success, Errors(result.Build));
            Assert.IsNotNull(result.Commit);
            Assert.IsFalse(result.Commit.HasChanges, "Written: " + string.Join(", ", result.Commit.Written));
            StringAssert.StartsWith("Up to date: 21 tables, 382 rows, 16 enums", UgsProject.Summarize(result));

            Assert.AreSame(result, UgsProject.LastResult);
            // A partial build reads only the selection again and takes the rest from the build above: still nothing to write.
            UgsRunResult partial = UgsProject.RunBlocking(commit: true, only: SheetSelection.Parse(new[] { "Item.xlsx", "Monster.xlsx/Monster.Boss" }));
            Assert.IsNull(partial.Failure, partial.Failure);
            Assert.IsTrue(partial.Build.Success, Errors(partial.Build));
            Assert.IsFalse(partial.Commit.HasChanges, "Written: " + string.Join(", ", partial.Commit.Written));
            Assert.AreEqual(8, partial.Build.RefreshedSheets.Count, string.Join(", ", partial.Build.RefreshedSheets));
            StringAssert.Contains("(partial: 8 sheets read again)", UgsProject.Summarize(partial));
        }

        [UnityTest]
        public IEnumerator The_window_opens_and_shows_every_page()
        {
            UgsProject.RunBlocking(commit: false);
            UgsWindow.Open();
            UgsWindow window = UnityEditor.EditorWindow.GetWindow<UgsWindow>();
            try
            {
                // The window builds its UI and binds list rows on its first frames.
                for (int frame = 0; frame < 3; frame++)
                {
                    yield return null;
                }

                VisualElement root = window.rootVisualElement;
                Assert.Greater(root.styleSheets.count, 0, "The window's style sheet was not found.");
                CollectionAssert.AreEqual(new[] { "Tables", "Settings" }, UgsWindow.PageNames.ToArray());
                List<UnityEditor.UIElements.ToolbarToggle> tabs = root.Query<UnityEditor.UIElements.ToolbarToggle>(className: "ugs-tab").ToList();
                CollectionAssert.AreEqual(UgsWindow.PageNames.ToArray(), tabs.Select(tab => tab.text).ToArray());
                StringAssert.StartsWith("Checked · 21 tables", root.Q<Label>(className: "ugs-status").text);

                // Every page puts something on screen and marks its own tab, and only that one.
                foreach (string page in UgsWindow.PageNames)
                {
                    window.SelectPage(page);
                    CollectionAssert.AreEqual(UgsWindow.PageNames.Select(name => name == page).ToArray(), tabs.Select(tab => tab.value).ToArray());
                    Assert.Greater(root.Q(className: "ugs-fill").childCount, 0, "The " + page + " page is empty.");
                }

                // Tables: a tree of workbook > tables, a search box that finds in it, and a preview of the selected table.
                window.SelectPage("Tables");
                yield return null;
                yield return null;
                // Workbooks are open, tables built from several sheets are closed. The top of the tree:
                CollectionAssert.AreEqual(new[] { "Excel", "Character.xlsx", "Job", "Level", "Skill" }, TreeNames(root).Take(5).ToArray());
                // The demo reads xlsx and CSV, so the tree starts with one node per kind of source.
                // Searching does not filter: the tree keeps every node, scrolls to the match and marks it in yellow.
                TreeView tree = root.Q<TreeView>();
                int rootCount = tree.GetRootIds().Count();
                // Each kind of source is a section: a header row, not a parent, so its workbooks sit at the top level.
                VisualElement excelHeader = root.Query(className: "ugs-tree-row").ToList().First(row => Plain(row.Q<Label>(className: "ugs-tree-row__name").text) == "Excel");
                Assert.IsTrue(excelHeader.ClassListContains("ugs-tree-row--header"));
                Assert.AreEqual(DisplayStyle.None, excelHeader.Q<Image>().resolvedStyle.display, "A header has no icon.");
                Assert.IsFalse(root.Query(className: "ugs-tree-row").ToList().First(row => Plain(row.Q<Label>(className: "ugs-tree-row__name").text) == "Character.xlsx").ClassListContains("ugs-tree-row--header"));
                Assert.Greater(rootCount, 3, "Workbooks are roots, next to the source headers.");
                Label matchCount = root.Q<Label>(className: "ugs-search__count");
                ScrollView treeScroll = tree.Q<ScrollView>();
                // A match already in view does not move the tree; one out of sight is scrolled to.
                root.Q<UnityEditor.UIElements.ToolbarSearchField>(className: "ugs-search").value = "Skill";
                yield return null;
                yield return null;
                Assert.AreEqual(0f, treeScroll.scrollOffset.y, "Skill is at the top, in view.");
                root.Q<UnityEditor.UIElements.ToolbarSearchField>(className: "ugs-search").value = "localization";
                yield return null;
                yield return null;
                Assert.Greater(treeScroll.scrollOffset.y, 0f, "Localization is below the fold.");
                Assert.AreEqual(rootCount, tree.GetRootIds().Count(), "Searching keeps every node.");
                CollectionAssert.Contains(TreeNames(root), "Localization");
                Label markedName = root.Query<Label>(className: "ugs-tree-row__name").ToList().FirstOrDefault(label => label.enableRichText);
                Assert.IsNotNull(markedName, "The match is marked.");
                StringAssert.Contains("<mark=#F2C94CE6>", markedName.text);
                StringAssert.EndsWith(" of 1", matchCount.text);
                root.Q<UnityEditor.UIElements.ToolbarSearchField>(className: "ugs-search").value = "no such sheet";
                yield return null;
                Assert.AreEqual("No match", matchCount.text);
                root.Q<UnityEditor.UIElements.ToolbarSearchField>(className: "ugs-search").value = "";
                yield return null;
                Assert.IsFalse(root.Query<Label>(className: "ugs-tree-row__name").ToList().Any(label => label.enableRichText), "No marks without a search.");
                Assert.AreEqual(DisplayStyle.None, matchCount.resolvedStyle.display);
                MultiColumnListView grid = root.Q<MultiColumnListView>();
                Assert.AreEqual("Row", grid.columns[0].title);
                Assert.Greater(grid.columns.Count, 1);

                // Type colors: the type under each heading is classed by kind, and numbers line up on the right.
                // Enter in the search box selects the match, so its table shows.
                window.SelectPage("Tables");
                root.Q<UnityEditor.UIElements.ToolbarSearchField>(className: "ugs-search").value = "game";
                PressEnterInSearch(root);
                yield return null;
                yield return null;
                StringAssert.StartsWith("Common.xlsx / Game.Config", root.Q(className: "ugs-caption").Q<Label>().text);
                Assert.IsNotNull(root.Q<Label>(className: "ugs-type--int"));
                Assert.IsNotNull(root.Q<Label>(className: "ugs-type--float"));
                Assert.IsNotNull(root.Q<Label>(className: "ugs-type--bool"));
                Assert.IsNotNull(root.Q<Label>(className: "ugs-type--date"));
                Assert.IsNotNull(root.Q<Label>(className: "ugs-type--ref"));

                // A name typed in full is the first match: "Item" goes to the Item table, not to Item.xlsx or EItemTag.
                root.Q<UnityEditor.UIElements.ToolbarSearchField>(className: "ugs-search").value = "Item";
                PressEnterInSearch(root);
                yield return null;
                yield return null;
                StringAssert.StartsWith("Item.xlsx / Item  ·  44 rows", root.Q(className: "ugs-caption").Q<Label>().text);
                Assert.IsNotNull(root.Q<Label>(className: "ugs-type--enum"));
                Assert.IsNotNull(root.Q<Label>(className: "ugs-cell--number"));
                // Values keep the default text color: the type classes are on the headings only.
                Assert.IsTrue(root.Query<Label>(className: "ugs-cell").ToList().All(cell => !cell.ClassListContains("ugs-type--int") && !cell.ClassListContains("ugs-type--enum")));

                // View style: grid lines, alignment and stripes are per user and apply to the grid on screen at once.
                UgsWindow.GridLines savedLines = UgsWindow.ViewGridLines;
                UgsWindow.CellAlignment savedAlignment = UgsWindow.ViewAlignment;
                bool savedStripes = UgsWindow.ViewStripes;
                try
                {
                    // Nothing saved: lines between every cell, text in the middle, striped rows.
                    UgsText.SetSetting("view.gridLines", "");
                    UgsText.SetSetting("view.alignment", "");
                    UgsText.SetSetting("view.stripes", "");
                    Assert.AreEqual(UgsWindow.GridLines.Both, UgsWindow.ViewGridLines);
                    Assert.AreEqual(UgsWindow.CellAlignment.Center, UgsWindow.ViewAlignment);
                    Assert.IsTrue(UgsWindow.ViewStripes);
                    Assert.IsNotNull(root.Q(className: "ugs-caption").Q<Button>(className: "ugs-caption__menu"), "View is a button in the caption.");

                    MultiColumnListView styled = root.Q<MultiColumnListView>(className: "ugs-grid");
                    window.SetViewStyle(UgsWindow.GridLines.Both, UgsWindow.CellAlignment.Center, false);
                    yield return null;
                    Assert.IsTrue(styled.ClassListContains("ugs-grid--rows") && styled.ClassListContains("ugs-grid--columns"));
                    Assert.AreEqual(AlternatingRowBackground.None, styled.showAlternatingRowBackgrounds);
                    VisualElement firstCell = root.Q(className: "unity-multi-column-view__cell");
                    Assert.AreEqual(1f, firstCell.resolvedStyle.borderRightWidth, "Vertical lines");
                    Assert.AreEqual(1f, root.Q(className: "unity-multi-column-view__row-container").resolvedStyle.borderBottomWidth, "Horizontal lines");
                    Assert.AreEqual(TextAnchor.MiddleCenter, root.Q<Label>(className: "ugs-cell--number").resolvedStyle.unityTextAlign, "Numbers follow the chosen alignment");
                    Assert.AreEqual(TextAnchor.MiddleRight, root.Q<Label>(className: "ugs-cell--row").resolvedStyle.unityTextAlign, "The row number stays on the right");
                    Assert.AreEqual("Both", UgsText.GetSetting("view.gridLines"));

                    window.SetViewStyle(UgsWindow.GridLines.None, UgsWindow.CellAlignment.Auto, true);
                    yield return null;
                    Assert.IsFalse(styled.ClassListContains("ugs-grid--rows") || styled.ClassListContains("ugs-grid--columns") || styled.ClassListContains("ugs-grid--center"));
                    Assert.AreEqual(AlternatingRowBackground.ContentOnly, styled.showAlternatingRowBackgrounds);
                    Assert.AreEqual(0f, root.Q(className: "unity-multi-column-view__cell").resolvedStyle.borderRightWidth);
                    Assert.AreEqual(TextAnchor.MiddleRight, root.Q<Label>(className: "ugs-cell--number").resolvedStyle.unityTextAlign);
                }
                finally
                {
                    window.SetViewStyle(savedLines, savedAlignment, savedStripes);
                }

                // Find in the table: Edit > Find (Ctrl+F / Cmd+F) opens the bar on the key column; Esc closes it.
                VisualElement findBar = root.Q(className: "ugs-find");
                Assert.AreEqual(DisplayStyle.None, findBar.resolvedStyle.display);
                using (ExecuteCommandEvent find = ExecuteCommandEvent.GetPooled("Find"))
                {
                    find.target = root;
                    root.SendEvent(find);
                }

                yield return null;
                Assert.AreEqual(DisplayStyle.Flex, findBar.resolvedStyle.display);
                var keyScope = root.Q<UnityEditor.UIElements.ToolbarToggle>(className: "ugs-find__key");
                var allScope = root.Q<UnityEditor.UIElements.ToolbarToggle>(className: "ugs-find__all");
                Assert.IsTrue(keyScope.value, "Find starts on the key column.");
                Assert.IsFalse(allScope.value);
                MultiColumnListView itemGrid = root.Q<MultiColumnListView>();
                Assert.AreEqual(44, itemGrid.itemsSource.Count);
                root.Q<UnityEditor.UIElements.ToolbarSearchField>(className: "ugs-find__field").value = "1000";
                // The filter is applied within a frame or two; on a busy editor allow a few more.
                for (int frame = 0; frame < 30 && itemGrid.itemsSource.Count != 7; frame++)
                {
                    yield return null;
                }

                yield return null;
                Assert.AreEqual(7, itemGrid.itemsSource.Count, "10001 to 10007");
                Assert.AreEqual("7 of 44", root.Q<Label>(className: "ugs-find__count").text);
                // The matched text is highlighted in the key cells.
                List<Label> marked = root.Query<Label>(className: "ugs-cell").ToList().Where(cell => cell.enableRichText && cell.text.Contains("<mark=")).ToList();
                Assert.Greater(marked.Count, 0);
                StringAssert.Contains("<mark=#F2C94CE6><color=#1E1E1E><noparse>1000</noparse></color></mark>", marked[0].text);

                allScope.value = true;
                Assert.IsFalse(keyScope.value, "Key and All are exclusive.");
                root.Q<UnityEditor.UIElements.ToolbarSearchField>(className: "ugs-find__field").value = "강화";
                yield return null;
                Assert.AreEqual(2, itemGrid.itemsSource.Count, "강화석 and 축복받은 강화석");
                yield return null;
                // Rows that Item.Material added carry that sheet's name, so they are not mistaken for rows of Item.
                CollectionAssert.AreEqual(new[] { "Material 13", "Material 14" },
                    root.Query<Label>(className: "ugs-cell--row").ToList().Where(label => label.resolvedStyle.display == DisplayStyle.Flex && label.text.Length > 0 && label.parent.resolvedStyle.display == DisplayStyle.Flex).Select(label => label.text).Take(2).ToArray());
                using (KeyDownEvent escape = KeyDownEvent.GetPooled('\0', KeyCode.Escape, EventModifiers.None))
                {
                    escape.target = root;
                    root.SendEvent(escape);
                }

                yield return null;
                Assert.AreEqual(DisplayStyle.None, findBar.resolvedStyle.display);
                Assert.AreEqual(44, itemGrid.itemsSource.Count);

                // Sorting: a heading sorts the rows. Numbers sort by value, ties keep sheet order, and find keeps the order.
                List<string> ItemColumn(int index) => itemGrid.itemsSource.Cast<Cell[]>().Select(row => row[index].Text).ToList();
                List<string> sheetOrder = ItemColumn(0);
                Assert.AreEqual(ColumnSortingMode.Custom, itemGrid.sortingMode);
                itemGrid.sortColumnDescriptions.Add(new SortColumnDescription("c8", SortDirection.Descending));
                yield return null;
                List<double> sellPrices = ItemColumn(8).Select(text => double.Parse(text, System.Globalization.CultureInfo.InvariantCulture)).ToList();
                Assert.AreEqual(44, sellPrices.Count);
                Assert.AreEqual(2500d, sellPrices[0], "SellPrice by value: 2500 before 900.");
                CollectionAssert.AreEqual(sellPrices.OrderByDescending(price => price).ToList(), sellPrices);
                Assert.IsNotNull(root.Q(className: "unity-multi-column-header__column__sort-indicator"), "The heading shows the sort arrow.");
                // Ties keep sheet order: the items that sell for 0 come in the order of their ids.
                List<string> free = itemGrid.itemsSource.Cast<Cell[]>().Where(row => row[8].Text == "0").Select(row => row[0].Text).ToList();
                CollectionAssert.AreEqual(sheetOrder.Where(free.Contains).ToList(), free);
                // A heading click describes the column by its index rather than its name.
                itemGrid.sortColumnDescriptions.Clear();
                itemGrid.sortColumnDescriptions.Add(new SortColumnDescription(itemGrid.columns.IndexOf(itemGrid.columns["c8"]), SortDirection.Ascending));
                yield return null;
                Assert.AreEqual("0", ItemColumn(8)[0]);
                Assert.AreEqual("2500", ItemColumn(8)[43]);

                itemGrid.sortColumnDescriptions.Clear();
                itemGrid.sortColumnDescriptions.Add(new SortColumnDescription("c0", SortDirection.Descending));
                yield return null;
                Assert.AreEqual("30111", ItemColumn(0)[0]);
                root.Q<UnityEditor.UIElements.ToolbarSearchField>(className: "ugs-find__field").value = "1000";
                for (int frame = 0; frame < 30 && itemGrid.itemsSource.Count != 7; frame++)
                {
                    yield return null;
                }

                CollectionAssert.AreEqual(new[] { "10007", "10006", "10005", "10004", "10003", "10002", "10001" }, ItemColumn(0), "Found rows stay sorted.");
                root.Q<UnityEditor.UIElements.ToolbarSearchField>(className: "ugs-find__field").value = "";
                yield return null;
                Assert.AreEqual(44, itemGrid.itemsSource.Count);
                Assert.AreEqual("30111", ItemColumn(0)[0], "Clearing find keeps the sort.");

                // The Row column is sheet order.
                itemGrid.sortColumnDescriptions.Clear();
                itemGrid.sortColumnDescriptions.Add(new SortColumnDescription("row", SortDirection.Ascending));
                yield return null;
                CollectionAssert.AreEqual(sheetOrder, ItemColumn(0));
                itemGrid.sortColumnDescriptions.Clear();
                yield return null;
                CollectionAssert.AreEqual(sheetOrder, ItemColumn(0), "No sort is sheet order.");

                // Text sorts the way people read it: case ignored, digits by value, Hangul in 가나다 order.
                Assert.Less(UgsWindow.CompareNatural("Item2", "item10"), 0);
                Assert.Less(UgsWindow.CompareNatural("가방", "나무"), 0);
                Assert.Greater(UgsWindow.CompareNatural("b", "A"), 0);
                Assert.AreEqual(0, UgsWindow.CompareNatural("x", "x"));

                // A table built from several sheets lists them under it. Searching for one opens the table and shows only that sheet's rows.
                root.Q<UnityEditor.UIElements.ToolbarSearchField>(className: "ugs-search").value = "advanced";
                PressEnterInSearch(root);
                yield return null;
                yield return null;
                CollectionAssert.Contains(TreeNames(root), "Job.Advanced", "The table opens to show the matching sheet.");
                StringAssert.StartsWith("Character.xlsx / Job.Advanced  ·  part of Job  ·  8 rows", root.Q(className: "ugs-caption").Q<Label>().text);

                root.Q<UnityEditor.UIElements.ToolbarSearchField>(className: "ugs-search").value = "monster.xlsx";
                PressEnterInSearch(root);
                yield return null;
                yield return null;
                // The workbook is selected; its tables follow it (Drop, Monster, Spawn), which the selections below rely on.
                string[] names = TreeNames(root);
                int monsterBook = Array.IndexOf(names, "Monster.xlsx");
                Assert.GreaterOrEqual(monsterBook, 0, string.Join(", ", names));
                Assert.AreEqual("Drop", names.ElementAtOrDefault(monsterBook + 1), string.Join(", ", names));
                // xlsx workbooks carry the Excel icon.
                Assert.AreEqual("ugs:excel", root.Query(className: "ugs-tree-row").ToList().First(row => Plain(row.Q<Label>(className: "ugs-tree-row__name").text) == "Monster.xlsx").Q<Image>().image.name);

                // Several nodes can be selected; the bar under the tree builds just those.
                tree = root.Q<TreeView>();
                int at = tree.selectedIndex;
                Label selectionLabel = root.Q<Label>(className: "ugs-selection__label");
                // The build button counts the tables the selection builds again.
                var selectionBuild = root.Q<UnityEditor.UIElements.ToolbarButton>(className: "ugs-selection__build");
                tree.SetSelection(new[] { at + 1, at + 3 });
                yield return null;
                Assert.AreEqual("2 sheets", selectionLabel.text);
                Assert.AreEqual("Build (2)", selectionBuild.text);
                StringAssert.Contains("Drop, Spawn", selectionBuild.tooltip);
                StringAssert.Contains("Monster.xlsx / Drop", root.Q(className: "ugs-selection").tooltip);
                tree.SetSelection(new[] { at, at + 2 });
                yield return null;
                Assert.AreEqual("Monster.xlsx", selectionLabel.text, "A selected workbook covers its tables.");
                Assert.AreEqual("Build (3 · enum 1)", selectionBuild.text, "Drop, Monster and Spawn. The workbook's Battle.Enums sheet is read again too.");
                tree.SetSelection(new[] { 0 });
                yield return null;
                StringAssert.IsMatch(@"^Excel · \d+ workbooks$", selectionLabel.text, "A source node covers the workbooks under it.");
                tree.SetSelection(new[] { at + 2 });
                yield return null;
                // Monster is built from Monster and Monster.Boss.
                Assert.AreEqual("Monster · 2 sheets", selectionLabel.text);
                Assert.AreEqual("Build (1)", selectionBuild.text, "Two sheets, one table.");

                // Enums are in the tree too: those of an Enums sheet under its workbook, Mapping enums under their table.
                root.Q<UnityEditor.UIElements.ToolbarSearchField>(className: "ugs-search").value = "EStat";
                PressEnterInSearch(root);
                yield return null;
                yield return null;
                CollectionAssert.Contains(TreeNames(root), "EStat");
                StringAssert.StartsWith("Common.xlsx / Enums  ·  EStat  ·  byte  ·  12 members", root.Q(className: "ugs-caption").Q<Label>().text);
                Assert.AreEqual(12, root.Q<MultiColumnListView>().itemsSource.Count);
                Assert.AreEqual(new[] { "Member", "Value", "Comment" }, root.Q<MultiColumnListView>().columns.Select(column => column.title).ToArray());
                // An Enums sheet builds no table, but its enums are read again: the whole sheet, so all 12 of them.
                Assert.AreEqual("Build (enum 12)", root.Q<UnityEditor.UIElements.ToolbarButton>(className: "ugs-selection__build").text);

                root.Q<UnityEditor.UIElements.ToolbarSearchField>(className: "ugs-search").value = "EItemTag";
                PressEnterInSearch(root);
                yield return null;
                yield return null;
                StringAssert.Contains("Flags", root.Q(className: "ugs-caption").Q<Label>().text);
                CollectionAssert.Contains(((List<string[]>)root.Q<MultiColumnListView>().itemsSource).Select(row => row[2]).ToList(), "8  (1 << 3)");
                // Enum members sort too; Value by its number, so 16 comes before 8.
                MultiColumnListView enumGrid = root.Q<MultiColumnListView>();
                enumGrid.sortColumnDescriptions.Add(new SortColumnDescription("f2", SortDirection.Descending));
                yield return null;
                CollectionAssert.AreEqual(new[] { "Event", "Bound", "Sellable", "Stackable", "Tradable", "None" },
                    ((List<string[]>)enumGrid.itemsSource).Select(row => row[1]).ToArray());

                root.Q<UnityEditor.UIElements.ToolbarSearchField>(className: "ugs-search").value = "EJobId";
                PressEnterInSearch(root);
                yield return null;
                yield return null;
                CollectionAssert.Contains(TreeNames(root), "EJobId");
                StringAssert.Contains("Mapping", root.Q(className: "ugs-caption").Q<Label>().text);
                Assert.AreEqual(12, root.Q<MultiColumnListView>().itemsSource.Count);

                // An Enums sheet lists every enum it defines.
                root.Q<UnityEditor.UIElements.ToolbarSearchField>(className: "ugs-search").value = "battle";
                PressEnterInSearch(root);
                yield return null;
                yield return null;
                CollectionAssert.Contains(TreeNames(root), "Battle.Enums");
                StringAssert.StartsWith("Monster.xlsx / Battle.Enums  ·  1 enum", root.Q(className: "ugs-caption").Q<Label>().text);
                Assert.AreEqual(4, root.Q<MultiColumnListView>().itemsSource.Count);
                Assert.AreEqual("Enum", root.Q<MultiColumnListView>().columns[0].title);

                root.Q<UnityEditor.UIElements.ToolbarSearchField>(className: "ugs-search").value = "";
                yield return null;

                // Problems open in a panel under the tables, behind a bar that counts them.
                window.SelectPage("Tables");
                Assert.AreEqual("No problems.", root.Q<Label>(className: "ugs-problems-bar__text").text);
                window.SelectPage("Problems");
                CollectionAssert.AreEqual(new[] { true, false }, root.Query<UnityEditor.UIElements.ToolbarToggle>(className: "ugs-tab").ToList().Select(tab => tab.value).ToArray(), "Problems open on the Tables page.");
                Assert.AreEqual("No problems.", root.Q(className: "ugs-problems").Q<Label>(className: "ugs-empty").text);

                window.SelectPage("Settings");
                // Sources, Output, Sheet and build options, Signing, Editor (the language of this window).
                Assert.AreEqual(5, root.Query<Foldout>(className: "ugs-section").ToList().Count);
                Assert.AreEqual(2, root.Query(className: "ugs-card").ToList().Count);
                Assert.IsNotNull(root.Query<DropdownField>().ToList().FirstOrDefault(field => field.label == "Language"));

                // In Korean: page content, the status, counts. Page names stay the same for code that selects pages.
                UgsText.Override = UgsLanguage.Korean;
                window.SelectPage("Settings");
                Assert.AreEqual("소스", root.Query<Foldout>(className: "ugs-section").First().text);
                StringAssert.StartsWith("검사 완료 · 테이블 21개", root.Q<Label>(className: "ugs-status").text);
                window.SelectPage("Tables");
                Assert.AreEqual("문제가 없습니다.", root.Q<Label>(className: "ugs-problems-bar__text").text);
                Assert.AreEqual("시트 3개", UgsText.Count(3, "sheet"));
                Assert.AreEqual("Not in the table yet", UgsText.T("Not in the table yet"), "A missing translation shows the English text.");
            }
            finally
            {
                window.Close();
            }
        }

        [Test]
        public void Tree_nodes_and_problems_open_the_file_they_came_from()
        {
            UgsProject.RunBlocking(commit: false);
            string item = UgsWindow.FindSourceFile("Item.xlsx");
            Assert.IsTrue(File.Exists(item), item);
            StringAssert.EndsWith("Demo/Sheets/Item.xlsx", item.Replace('\\', '/'));
            // A CSV workbook opens at the sheet's own file.
            StringAssert.EndsWith("Sheets/Localization/Text.csv", UgsWindow.FindSourceFile("Localization", "Text").Replace('\\', '/'));
            Assert.IsNull(UgsWindow.FindSourceFile("Nope.xlsx"));
        }

        [UnityTest]
        public IEnumerator The_window_reads_the_sheets_by_itself_after_a_restart()
        {
            // An editor restart or a script reload leaves no result in memory.
            foreach (UgsWindow open in Resources.FindObjectsOfTypeAll<UgsWindow>())
            {
                open.Close();
            }

            typeof(UgsProject).GetProperty(nameof(UgsProject.LastResult)).SetValue(null, null);
            UgsWindow.Open();
            UgsWindow window = UnityEditor.EditorWindow.GetWindow<UgsWindow>();
            try
            {
                window.SelectPage("Tables");
                // The window builds its UI on the first frame it is shown (on Unity 7 a frame later than on 6), and the check starts there.
                DateTime deadline = DateTime.UtcNow.AddSeconds(60);
                while ((UgsProject.LastResult == null || UgsProject.IsRunning) && DateTime.UtcNow < deadline)
                {
                    yield return null;
                }

                Assert.IsNotNull(UgsProject.LastResult, "Opening the window with nothing read did not check the sheets within 60 seconds.");
                Assert.IsTrue(UgsProject.LastResult.Build.Success, Errors(UgsProject.LastResult.Build));
                Assert.IsNull(UgsProject.LastResult.Commit, "The automatic run only checks; it writes nothing.");
                yield return null;
                yield return null;
                CollectionAssert.AreEqual(new[] { "Excel", "Character.xlsx", "Job", "Level", "Skill" }, TreeNames(window.rootVisualElement).Take(5).ToArray());
            }
            finally
            {
                window.Close();
            }
        }

        private static string[] TreeNames(VisualElement root)
        {
            return root.Query<Label>(className: "ugs-tree-row__name").ToList()
                .Where(label => label.resolvedStyle.display == DisplayStyle.Flex && label.text.Length > 0 && label.parent.parent.resolvedStyle.display == DisplayStyle.Flex)
                .Select(label => Plain(label.text)).ToArray();
        }

        // A tree name without the rich text that marks a search match.
        private static string Plain(string text) => System.Text.RegularExpressions.Regex.Replace(text, "<[^>]*>", "");

        // Enter in the tree's search box: selects the next match.
        private static void PressEnterInSearch(VisualElement root)
        {
            var search = root.Q<UnityEditor.UIElements.ToolbarSearchField>(className: "ugs-search");
            VisualElement input = search.Q<TextField>() ?? (VisualElement)search;
            using (KeyDownEvent enter = KeyDownEvent.GetPooled('\0', KeyCode.Return, EventModifiers.None))
            {
                enter.target = input;
                input.SendEvent(enter);
            }
        }
    }
}
