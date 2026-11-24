using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using NUnit.Framework;
using HolySheet.Core;
using HolySheet.Core.Config;
using HolySheet.Core.Csv;
using HolySheet.Core.Output;
using HolySheet.Core.Sources;
using HolySheet.Demo.Data;
using HolySheet.Demo.Editor;
using HolySheet.Editor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace HolySheet.Dev.Tests
{
    public class EditorTests
    {
        private string _temp;

        [SetUp]
        public void SetUp()
        {
            HolySheetText.Override = HolySheetLanguage.English;
            _temp = Path.Combine(Path.GetTempPath(), "holysheet-unity-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_temp);
        }

        [TearDown]
        public void TearDown()
        {
            HolySheetText.Override = null;
            try
            {
                Directory.Delete(_temp, true);
            }
            catch (IOException)
            {
            }
        }

        private static string DataDirectory => Path.Combine(HolySheetProject.RootDirectory, "Assets/Samples/Demo/Resources/HolySheetDemo");

        private static string CodeDirectory => Path.Combine(HolySheetProject.RootDirectory, "Assets/Samples/Demo/Generated");

        private static string Errors(BuildResult result) => string.Join("\n", result.Diagnostics.Items.Select(item => item.ToString()));

        private HolySheetConfig WriteDemoAsCsv(string name, Func<string, string, string> edit = null)
        {
            BuildResult source = TableBuild.Run(HolySheetProject.LoadConfig(), new TableBuildOptions { Offline = true });
            Assert.IsTrue(source.Success, Errors(source));
            string sheets = Path.Combine(_temp, name, "Sheets");
            Directory.CreateDirectory(sheets);
            foreach (Sheet sheet in source.Workbooks.SelectMany(workbook => workbook.Sheets))
            {
                string csv = CsvWriter.Write(sheet.ToGrid());
                File.WriteAllText(Path.Combine(sheets, sheet.Name + ".csv"), edit == null ? csv : edit(sheet.Name, csv));
            }

            HolySheetConfig config = HolySheetDemoSetup.CreateConfig();
            config.BaseDirectory = Path.Combine(_temp, name);
            config.Sources.Clear();
            config.Sources.Add(new SourceConfig { Type = SourceType.Csv, Path = "Sheets" });
            config.Output.Code = "Code";
            config.Output.Data = "Data";
            return config;
        }

        private static string Commit(HolySheetConfig config)
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
            Debug.Log("[HolySheet.Dev.Tests] Unity " + Application.unityVersion + " on " + runtime + ", sep available: " + (CsvBackends.Fast != null));
            Assert.AreEqual("builtin", CsvBackends.Resolve("auto").Name);
#if UNITY_7000_0_OR_NEWER
            StringAssert.StartsWith(".NET", runtime);
            Assert.IsNotNull(CsvBackends.Fast, CsvBackends.FastUnavailableReason);
            Assert.AreEqual("sep", CsvBackends.Resolve("sep").Name);
            Assert.IsTrue(HolySheetProject.IsSepAvailable);
#else
            StringAssert.StartsWith("Mono", runtime);
            Assert.IsNull(CsvBackends.Fast);
            Assert.Throws<NotSupportedException>(() => CsvBackends.Resolve("sep"));
            Assert.IsFalse(HolySheetProject.IsSepAvailable);
            Assert.IsFalse(AppDomain.CurrentDomain.GetAssemblies().Any(assembly => assembly.GetName().Name == "Sep"), "Sep.dll must not load on Mono.");
#endif
        }

        [Test]
        public void Building_in_this_editor_gives_the_checked_in_files_byte_for_byte()
        {
            BuildResult result = TableBuild.Run(HolySheetProject.LoadConfig(), new TableBuildOptions { Offline = true });
            Assert.IsTrue(result.Success, Errors(result));
            Assert.AreEqual(DemoTables.Definition.Tables.Length, result.Tables.Count);
            foreach (KeyValuePair<string, string> file in result.Output.CodeFiles)
            {
                Assert.AreEqual(File.ReadAllText(Path.Combine(CodeDirectory, file.Key)), file.Value, file.Key);
            }

            foreach (KeyValuePair<string, byte[]> file in result.Output.DataFiles)
            {
                CollectionAssert.AreEqual(File.ReadAllBytes(Path.Combine(DataDirectory, file.Key)), file.Value, file.Key);
            }

            Assert.AreEqual(File.ReadAllText(Path.Combine(DataDirectory, "tables.manifest.json")), result.Output.ManifestText);
            Assert.AreEqual(DemoTables.SchemaHash, result.Output.SchemaHash);
        }

        [Test]
        public void The_same_tables_through_csv_give_the_same_data_as_through_xlsx()
        {
            HolySheetConfig config = WriteDemoAsCsv("csv");
            config.CsvBackend = "builtin";
            BuildResult builtin = TableBuild.Run(config);
            Assert.IsTrue(builtin.Success, Errors(builtin));
            Assert.AreEqual("builtin", builtin.CsvBackend);
            Assert.AreEqual(DemoTables.SchemaHash, builtin.Output.SchemaHash);
            Assert.AreEqual(DemoTables.Load(DataDirectory).Manifest.Content, builtin.Output.ContentHash);

#if UNITY_7000_0_OR_NEWER
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
                Assert.AreEqual(DemoTables.Definition.Tables.Length, tables.Tables.Count);

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

                MonsterRow lord = tables.Monster.Get((int)EMonsterId.AbyssLord);
                Assert.AreEqual(EMonsterRank.RaidBoss, lord.Rank);
                Assert.AreEqual(EAiType.Caster, lord.Ai);
                DropRow drop = tables.Drop.Get(lord.Drop);
                Assert.AreEqual("레이드 보스", drop.Name);
                Assert.AreEqual("용의 비늘", tables.Item.Get(drop.Items[1]).Name);
                CollectionAssert.AreEqual(new[] { 1f, 0.3f, 1f, 0.2f, 0.2f }, drop.Chances.ToArray());
                Assert.AreEqual("심연의 왕좌", tables.Map.Get(tables.Spawn.Get(20).Map).Name);

                MapRow raid = tables.Map.GetByCode("abyss_throne");
                Assert.AreEqual(EMapType.Raid, raid.Type);
                Assert.AreEqual("가라앉은 유적", tables.Region.Get(raid.Region).Name);
                Assert.AreEqual("상인 미라", tables.Npc.Get(2).Name);
                Assert.AreEqual("미라의 잡화점", tables.Shop.Get(tables.Npc.Get(2).Shop).Name);
                Assert.IsFalse(tables.Npc.Get(1).Shop.HasValue);

                Assert.AreEqual("고블린 소굴", tables.Quest.Get(tables.Quest.Get(5).Previous).Name);
                CollectionAssert.AreEqual(new[] { 6, 7 }, tables.QuestObjective.ListByQuestId(4).Select(objective => objective.Id).ToArray());
                Assert.AreEqual(EQuestType.Weekly, tables.Quest.Get(201).Type);
                Assert.AreEqual(6, tables.ShopGoods.ListByShopId(2).Length);
                Assert.AreEqual(new DateTime(2027, 2, 28, 23, 59, 59, DateTimeKind.Utc), tables.Gacha.Get(2).EndsAt);

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
            DemoTables tables = DemoTables.Load(new ResourcesTableSource("HolySheetDemo"));
            Assert.AreEqual(16, tables.Monster.Count);

            MemoryTableSource preloaded = TableSource.Preload(new ResourcesTableSource("HolySheetDemo"), DemoTables.ManifestFileName);
            Assert.AreEqual(tables.Manifest.Tables.Count + 1, preloaded.FileNames.Count());
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
            HolySheetConfig config = WriteDemoAsCsv("signed");
            File.WriteAllText(Path.Combine(config.BaseDirectory, "key"), privateKey);
            config.Signing.PrivateKeyFile = "key";
            config.Signing.PublicKey = publicKey;
            string data = Commit(config);

            var options = new TableLoadOptions { PublicKey = publicKey };
            Assert.AreEqual(44, DemoTables.Load(data, options).Item.Count);
            Assert.IsTrue(TableSignature.Verify(TableManifest.Parse(File.ReadAllText(Path.Combine(data, "tables.manifest.json"))), publicKey));

            string manifestPath = Path.Combine(data, "tables.manifest.json");
            File.WriteAllText(manifestPath, File.ReadAllText(manifestPath).Replace("\"tool\": \"holysheet/", "\"tool\": \"xyz/"));
            TableLoadException exception = Assert.Throws<TableLoadException>(() => DemoTables.Load(data, options));
            Assert.AreEqual(TableLoadError.SignatureInvalid, exception.Error);
        }

        [UnityTest]
        public IEnumerator A_watcher_reloads_only_what_changed_and_raises_its_event_on_the_main_thread()
        {
            HolySheetConfig config = WriteDemoAsCsv("watch");
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
            var baseline = TableManifest.Parse(File.ReadAllText(Path.Combine(DataDirectory, DemoTables.ManifestFileName)));
            HolySheetRunResult result = HolySheetProject.RunBlocking(commit: true, offline: true);
            Assert.IsNull(result.Failure, result.Failure);
            Assert.IsTrue(result.Build.Success, Errors(result.Build));
            Assert.IsNotNull(result.Commit);
            Assert.IsFalse(result.Commit.HasChanges, "Written: " + string.Join(", ", result.Commit.Written));
            StringAssert.StartsWith($"Up to date: {baseline.Tables.Count} tables, {baseline.Tables.Sum(t => t.Rows)} rows, 16 enums", HolySheetProject.Summarize(result));

            Assert.AreSame(result, HolySheetProject.LastResult);
            HolySheetRunResult partial = HolySheetProject.RunBlocking(commit: true, offline: true, only: SheetSelection.Parse(new[] { "Item.xlsx", "Monster.xlsx/Monster.Boss" }));
            Assert.IsNull(partial.Failure, partial.Failure);
            Assert.IsTrue(partial.Build.Success, Errors(partial.Build));
            Assert.IsFalse(partial.Commit.HasChanges, "Written: " + string.Join(", ", partial.Commit.Written));
            Assert.AreEqual(8, partial.Build.RefreshedSheets.Count, string.Join(", ", partial.Build.RefreshedSheets));
            StringAssert.Contains("(partial: 8 sheets read again)", HolySheetProject.Summarize(partial));
        }

        [UnityTest]
        public IEnumerator The_window_opens_and_shows_every_page()
        {
            HolySheetProject.RunBlocking(commit: false, offline: true);
            HolySheetWindow.Open();
            HolySheetWindow window = UnityEditor.EditorWindow.GetWindow<HolySheetWindow>();
            try
            {
                for (int frame = 0; frame < 3; frame++)
                {
                    yield return null;
                }

                VisualElement root = window.rootVisualElement;
                Assert.Greater(root.styleSheets.count, 0, "The window's style sheet was not found.");
                CollectionAssert.AreEqual(new[] { "Tables", "Settings" }, HolySheetWindow.PageNames.ToArray());
                List<UnityEditor.UIElements.ToolbarToggle> tabs = root.Query<UnityEditor.UIElements.ToolbarToggle>(className: "holysheet-tab").ToList();
                CollectionAssert.AreEqual(HolySheetWindow.PageNames.ToArray(), tabs.Select(tab => tab.text).ToArray());
                StringAssert.StartsWith($"Checked · {DemoTables.Definition.Tables.Length} tables", root.Q<Label>(className: "holysheet-status").text);

                foreach (string page in HolySheetWindow.PageNames)
                {
                    window.SelectPage(page);
                    CollectionAssert.AreEqual(HolySheetWindow.PageNames.Select(name => name == page).ToArray(), tabs.Select(tab => tab.value).ToArray());
                    Assert.Greater(root.Q(className: "holysheet-fill").childCount, 0, "The " + page + " page is empty.");
                }

                window.SelectPage("Tables");
                yield return null;
                yield return null;
                CollectionAssert.AreEqual(new[] { "Excel", "Character.xlsx", "Job", "Level", "Skill" }, TreeNames(root).Take(5).ToArray());
                TreeView tree = root.Q<TreeView>();
                int rootCount = tree.GetRootIds().Count();
                Assert.That(rootCount, Is.InRange(2, 3), "One root per connected source.");
                Assert.AreEqual("holysheet:excel", root.Query(className: "holysheet-tree-row").ToList().First(row => Plain(row.Q<Label>(className: "holysheet-tree-row__name").text) == "Excel").Q<Image>().image.name, "A source node carries its kind's icon.");
                Label matchCount = root.Q<Label>(className: "holysheet-search__count");
                ScrollView treeScroll = tree.Q<ScrollView>();
                root.Q<UnityEditor.UIElements.ToolbarSearchField>(className: "holysheet-search").value = "Skill";
                yield return null;
                yield return null;
                Assert.AreEqual(0f, treeScroll.scrollOffset.y, "Skill is at the top, in view.");
                root.Q<UnityEditor.UIElements.ToolbarSearchField>(className: "holysheet-search").value = "localization";
                yield return null;
                yield return null;
                Assert.Greater(treeScroll.scrollOffset.y, 0f, "Localization is below the fold.");
                Assert.AreEqual(rootCount, tree.GetRootIds().Count(), "Searching keeps every node.");
                CollectionAssert.Contains(TreeNames(root), "Localization");
                Label markedName = root.Query<Label>(className: "holysheet-tree-row__name").ToList().FirstOrDefault(label => label.enableRichText);
                Assert.IsNotNull(markedName, "The match is marked.");
                StringAssert.Contains("<mark=#F2C94CE6>", markedName.text);
                StringAssert.EndsWith(" of 1", matchCount.text);
                root.Q<UnityEditor.UIElements.ToolbarSearchField>(className: "holysheet-search").value = "no such sheet";
                yield return null;
                Assert.AreEqual("No match", matchCount.text);
                root.Q<UnityEditor.UIElements.ToolbarSearchField>(className: "holysheet-search").value = "";
                yield return null;
                Assert.IsFalse(root.Query<Label>(className: "holysheet-tree-row__name").ToList().Any(label => label.enableRichText), "No marks without a search.");
                Assert.AreEqual(DisplayStyle.None, matchCount.resolvedStyle.display);
                MultiColumnListView grid = root.Q<MultiColumnListView>();
                Assert.AreEqual("Row", grid.columns[0].title);
                Assert.Greater(grid.columns.Count, 1);

                window.SelectPage("Tables");
                root.Q<UnityEditor.UIElements.ToolbarSearchField>(className: "holysheet-search").value = "game";
                PressEnterInSearch(root);
                yield return null;
                yield return null;
                StringAssert.StartsWith("Common.xlsx / Game.Config", root.Q(className: "holysheet-caption").Q<Label>().text);
                Assert.IsNotNull(root.Q<Label>(className: "holysheet-type--int"));
                Assert.IsNotNull(root.Q<Label>(className: "holysheet-type--float"));
                Assert.IsNotNull(root.Q<Label>(className: "holysheet-type--bool"));
                Assert.IsNotNull(root.Q<Label>(className: "holysheet-type--date"));
                Assert.IsNotNull(root.Q<Label>(className: "holysheet-type--ref"));

                root.Q<UnityEditor.UIElements.ToolbarSearchField>(className: "holysheet-search").value = "Item";
                PressEnterInSearch(root);
                yield return null;
                yield return null;
                StringAssert.StartsWith("Item.xlsx / Item  ·  44 rows", root.Q(className: "holysheet-caption").Q<Label>().text);
                Assert.IsNotNull(root.Q<Label>(className: "holysheet-type--enum"));
                Assert.IsNotNull(root.Q<Label>(className: "holysheet-cell--number"));
                Assert.IsTrue(root.Query<Label>(className: "holysheet-cell").ToList().All(cell => !cell.ClassListContains("holysheet-type--int") && !cell.ClassListContains("holysheet-type--enum")));

                HolySheetWindow.GridLines savedLines = HolySheetWindow.ViewGridLines;
                HolySheetWindow.CellAlignment savedAlignment = HolySheetWindow.ViewAlignment;
                bool savedStripes = HolySheetWindow.ViewStripes;
                try
                {
                    HolySheetText.SetSetting("view.gridLines", "");
                    HolySheetText.SetSetting("view.alignment", "");
                    HolySheetText.SetSetting("view.stripes", "");
                    Assert.AreEqual(HolySheetWindow.GridLines.Both, HolySheetWindow.ViewGridLines);
                    Assert.AreEqual(HolySheetWindow.CellAlignment.Center, HolySheetWindow.ViewAlignment);
                    Assert.IsTrue(HolySheetWindow.ViewStripes);
                    Assert.IsNotNull(root.Q(className: "holysheet-caption").Q<Button>(className: "holysheet-caption__menu"), "View is a button in the caption.");

                    MultiColumnListView styled = root.Q<MultiColumnListView>(className: "holysheet-grid");
                    window.SetViewStyle(HolySheetWindow.GridLines.Both, HolySheetWindow.CellAlignment.Center, false);
                    yield return null;
                    Assert.IsTrue(styled.ClassListContains("holysheet-grid--rows") && styled.ClassListContains("holysheet-grid--columns"));
                    Assert.AreEqual(AlternatingRowBackground.None, styled.showAlternatingRowBackgrounds);
                    VisualElement firstCell = root.Q(className: "unity-multi-column-view__cell");
                    Assert.AreEqual(1f, firstCell.resolvedStyle.borderRightWidth, "Vertical lines");
                    Assert.AreEqual(1f, root.Q(className: "unity-multi-column-view__row-container").resolvedStyle.borderBottomWidth, "Horizontal lines");
                    Assert.AreEqual(TextAnchor.MiddleCenter, root.Q<Label>(className: "holysheet-cell--number").resolvedStyle.unityTextAlign, "Numbers follow the chosen alignment");
                    Assert.AreEqual(TextAnchor.MiddleRight, root.Q<Label>(className: "holysheet-cell--row").resolvedStyle.unityTextAlign, "The row number stays on the right");
                    Assert.AreEqual("Both", HolySheetText.GetSetting("view.gridLines"));

                    window.SetViewStyle(HolySheetWindow.GridLines.None, HolySheetWindow.CellAlignment.Auto, true);
                    yield return null;
                    Assert.IsFalse(styled.ClassListContains("holysheet-grid--rows") || styled.ClassListContains("holysheet-grid--columns") || styled.ClassListContains("holysheet-grid--center"));
                    Assert.AreEqual(AlternatingRowBackground.ContentOnly, styled.showAlternatingRowBackgrounds);
                    Assert.AreEqual(0f, root.Q(className: "unity-multi-column-view__cell").resolvedStyle.borderRightWidth);
                    Assert.AreEqual(TextAnchor.MiddleRight, root.Q<Label>(className: "holysheet-cell--number").resolvedStyle.unityTextAlign);
                }
                finally
                {
                    window.SetViewStyle(savedLines, savedAlignment, savedStripes);
                }

                VisualElement findBar = root.Q(className: "holysheet-find");
                Assert.AreEqual(DisplayStyle.None, findBar.resolvedStyle.display);
                using (ExecuteCommandEvent find = ExecuteCommandEvent.GetPooled("Find"))
                {
                    find.target = root;
                    root.SendEvent(find);
                }

                yield return null;
                Assert.AreEqual(DisplayStyle.Flex, findBar.resolvedStyle.display);
                var keyScope = root.Q<UnityEditor.UIElements.ToolbarToggle>(className: "holysheet-find__key");
                var allScope = root.Q<UnityEditor.UIElements.ToolbarToggle>(className: "holysheet-find__all");
                Assert.IsTrue(keyScope.value, "Find starts on the key column.");
                Assert.IsFalse(allScope.value);
                MultiColumnListView itemGrid = root.Q<MultiColumnListView>();
                Assert.AreEqual(44, itemGrid.itemsSource.Count);
                root.Q<UnityEditor.UIElements.ToolbarSearchField>(className: "holysheet-find__field").value = "1000";
                for (int frame = 0; frame < 30 && itemGrid.itemsSource.Count != 7; frame++)
                {
                    yield return null;
                }

                yield return null;
                Assert.AreEqual(7, itemGrid.itemsSource.Count, "10001 to 10007");
                Assert.AreEqual("7 of 44", root.Q<Label>(className: "holysheet-find__count").text);
                List<Label> marked = root.Query<Label>(className: "holysheet-cell").ToList().Where(cell => cell.enableRichText && cell.text.Contains("<mark=")).ToList();
                Assert.Greater(marked.Count, 0);
                StringAssert.Contains("<mark=#F2C94CE6><color=#1E1E1E><noparse>1000</noparse></color></mark>", marked[0].text);

                allScope.value = true;
                Assert.IsFalse(keyScope.value, "Key and All are exclusive.");
                root.Q<UnityEditor.UIElements.ToolbarSearchField>(className: "holysheet-find__field").value = "강화";
                yield return null;
                Assert.AreEqual(2, itemGrid.itemsSource.Count, "강화석 and 축복받은 강화석");
                yield return null;
                CollectionAssert.AreEqual(new[] { "Material 13", "Material 14" },
                    root.Query<Label>(className: "holysheet-cell--row").ToList().Where(label => label.resolvedStyle.display == DisplayStyle.Flex && label.text.Length > 0 && label.parent.resolvedStyle.display == DisplayStyle.Flex).Select(label => label.text).Take(2).ToArray());
                using (KeyDownEvent escape = KeyDownEvent.GetPooled('\0', KeyCode.Escape, EventModifiers.None))
                {
                    escape.target = root;
                    root.SendEvent(escape);
                }

                yield return null;
                Assert.AreEqual(DisplayStyle.None, findBar.resolvedStyle.display);
                Assert.AreEqual(44, itemGrid.itemsSource.Count);

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
                List<string> free = itemGrid.itemsSource.Cast<Cell[]>().Where(row => row[8].Text == "0").Select(row => row[0].Text).ToList();
                CollectionAssert.AreEqual(sheetOrder.Where(free.Contains).ToList(), free);
                itemGrid.sortColumnDescriptions.Clear();
                itemGrid.sortColumnDescriptions.Add(new SortColumnDescription(itemGrid.columns.IndexOf(itemGrid.columns["c8"]), SortDirection.Ascending));
                yield return null;
                Assert.AreEqual("0", ItemColumn(8)[0]);
                Assert.AreEqual("2500", ItemColumn(8)[43]);

                itemGrid.sortColumnDescriptions.Clear();
                itemGrid.sortColumnDescriptions.Add(new SortColumnDescription("c0", SortDirection.Descending));
                yield return null;
                Assert.AreEqual("30111", ItemColumn(0)[0]);
                root.Q<UnityEditor.UIElements.ToolbarSearchField>(className: "holysheet-find__field").value = "1000";
                for (int frame = 0; frame < 30 && itemGrid.itemsSource.Count != 7; frame++)
                {
                    yield return null;
                }

                CollectionAssert.AreEqual(new[] { "10007", "10006", "10005", "10004", "10003", "10002", "10001" }, ItemColumn(0), "Found rows stay sorted.");
                root.Q<UnityEditor.UIElements.ToolbarSearchField>(className: "holysheet-find__field").value = "";
                yield return null;
                Assert.AreEqual(44, itemGrid.itemsSource.Count);
                Assert.AreEqual("30111", ItemColumn(0)[0], "Clearing find keeps the sort.");

                itemGrid.sortColumnDescriptions.Clear();
                itemGrid.sortColumnDescriptions.Add(new SortColumnDescription("row", SortDirection.Ascending));
                yield return null;
                CollectionAssert.AreEqual(sheetOrder, ItemColumn(0));
                itemGrid.sortColumnDescriptions.Clear();
                yield return null;
                CollectionAssert.AreEqual(sheetOrder, ItemColumn(0), "No sort is sheet order.");

                Assert.Less(HolySheetWindow.CompareNatural("Item2", "item10"), 0);
                Assert.Less(HolySheetWindow.CompareNatural("가방", "나무"), 0);
                Assert.Greater(HolySheetWindow.CompareNatural("b", "A"), 0);
                Assert.AreEqual(0, HolySheetWindow.CompareNatural("x", "x"));

                root.Q<UnityEditor.UIElements.ToolbarSearchField>(className: "holysheet-search").value = "advanced";
                PressEnterInSearch(root);
                yield return null;
                yield return null;
                CollectionAssert.Contains(TreeNames(root), "Job.Advanced", "The table opens to show the matching sheet.");
                StringAssert.StartsWith("Character.xlsx / Job.Advanced  ·  part of Job  ·  8 rows", root.Q(className: "holysheet-caption").Q<Label>().text);

                root.Q<UnityEditor.UIElements.ToolbarSearchField>(className: "holysheet-search").value = "monster.xlsx";
                PressEnterInSearch(root);
                yield return null;
                yield return null;
                string[] names = TreeNames(root);
                int monsterBook = Array.IndexOf(names, "Monster.xlsx");
                Assert.GreaterOrEqual(monsterBook, 0, string.Join(", ", names));
                Assert.AreEqual("Drop", names.ElementAtOrDefault(monsterBook + 1), string.Join(", ", names));
                Assert.AreEqual("holysheet:excel", root.Query(className: "holysheet-tree-row").ToList().First(row => Plain(row.Q<Label>(className: "holysheet-tree-row__name").text) == "Monster.xlsx").Q<Image>().image.name);

                tree = root.Q<TreeView>();
                int at = tree.selectedIndex;
                Label selectionLabel = root.Q<Label>(className: "holysheet-selection__label");
                var selectionBuild = root.Q<UnityEditor.UIElements.ToolbarButton>(className: "holysheet-selection__build");
                tree.SetSelection(new[] { at + 1, at + 3 });
                yield return null;
                Assert.AreEqual("2 sheets", selectionLabel.text);
                Assert.AreEqual("Build selection (2)", selectionBuild.text);
                var toolbar = root.Q<UnityEditor.UIElements.Toolbar>();
                CollectionAssert.AreEqual(new[] { "Check", "Build all", "Build selection (2)" },
                    toolbar.Query<UnityEditor.UIElements.ToolbarButton>().ToList().Select(button => button.text).ToArray());
                CollectionAssert.AreEqual(new[] { "Tables", "Settings" },
                    toolbar.Query<UnityEditor.UIElements.ToolbarToggle>().ToList().Select(toggle => toggle.text).ToArray());
                StringAssert.Contains("Drop, Spawn", selectionBuild.tooltip);
                StringAssert.Contains("Monster.xlsx / Drop", root.Q(className: "holysheet-selection").tooltip);
                tree.SetSelection(new[] { at, at + 2 });
                yield return null;
                Assert.AreEqual("Monster.xlsx", selectionLabel.text, "A selected workbook covers its tables.");
                Assert.AreEqual("Build selection (3 · enum 1)", selectionBuild.text, "Drop, Monster and Spawn. The workbook's Battle.Enums sheet is read again too.");
                tree.SetSelection(new[] { 0 });
                yield return null;
                StringAssert.IsMatch(@"^Excel · \d+ workbooks$", selectionLabel.text, "A source node covers the workbooks under it.");
                tree.SetSelection(new[] { at + 2 });
                yield return null;
                Assert.AreEqual("Monster · 2 sheets", selectionLabel.text);
                Assert.AreEqual("Build selection (1)", selectionBuild.text, "Two sheets, one table.");

                root.Q<UnityEditor.UIElements.ToolbarSearchField>(className: "holysheet-search").value = "EStat";
                PressEnterInSearch(root);
                yield return null;
                yield return null;
                CollectionAssert.Contains(TreeNames(root), "EStat");
                StringAssert.StartsWith("Common.xlsx / Enums  ·  EStat  ·  byte  ·  12 members", root.Q(className: "holysheet-caption").Q<Label>().text);
                Assert.AreEqual(12, root.Q<MultiColumnListView>().itemsSource.Count);
                Assert.AreEqual(new[] { "Member", "Value", "Comment" }, root.Q<MultiColumnListView>().columns.Select(column => column.title).ToArray());
                Assert.AreEqual("Build selection (enum 12)", root.Q<UnityEditor.UIElements.ToolbarButton>(className: "holysheet-selection__build").text);

                root.Q<UnityEditor.UIElements.ToolbarSearchField>(className: "holysheet-search").value = "EItemTag";
                PressEnterInSearch(root);
                yield return null;
                yield return null;
                StringAssert.Contains("Flags", root.Q(className: "holysheet-caption").Q<Label>().text);
                CollectionAssert.Contains(((List<string[]>)root.Q<MultiColumnListView>().itemsSource).Select(row => row[2]).ToList(), "8  (1 << 3)");
                MultiColumnListView enumGrid = root.Q<MultiColumnListView>();
                enumGrid.sortColumnDescriptions.Add(new SortColumnDescription("f2", SortDirection.Descending));
                yield return null;
                CollectionAssert.AreEqual(new[] { "Event", "Bound", "Sellable", "Stackable", "Tradable", "None" },
                    ((List<string[]>)enumGrid.itemsSource).Select(row => row[1]).ToArray());

                root.Q<UnityEditor.UIElements.ToolbarSearchField>(className: "holysheet-search").value = "EJobId";
                PressEnterInSearch(root);
                yield return null;
                yield return null;
                CollectionAssert.Contains(TreeNames(root), "EJobId");
                StringAssert.Contains("Mapping", root.Q(className: "holysheet-caption").Q<Label>().text);
                Assert.AreEqual(Enum.GetValues(typeof(EJobId)).Length, root.Q<MultiColumnListView>().itemsSource.Count);

                root.Q<UnityEditor.UIElements.ToolbarSearchField>(className: "holysheet-search").value = "battle";
                PressEnterInSearch(root);
                yield return null;
                yield return null;
                CollectionAssert.Contains(TreeNames(root), "Battle.Enums");
                StringAssert.StartsWith("Monster.xlsx / Battle.Enums  ·  1 enum", root.Q(className: "holysheet-caption").Q<Label>().text);
                Assert.AreEqual(4, root.Q<MultiColumnListView>().itemsSource.Count);
                Assert.AreEqual("Enum", root.Q<MultiColumnListView>().columns[0].title);

                root.Q<UnityEditor.UIElements.ToolbarSearchField>(className: "holysheet-search").value = "";
                yield return null;

                window.SelectPage("Tables");
                Assert.AreEqual("No problems.", root.Q<Label>(className: "holysheet-problems-bar__text").text);
                window.SelectPage("Problems");
                CollectionAssert.AreEqual(new[] { true, false }, root.Query<UnityEditor.UIElements.ToolbarToggle>(className: "holysheet-tab").ToList().Select(tab => tab.value).ToArray(), "Problems open on the Tables page.");
                Assert.AreEqual("No problems.", root.Q(className: "holysheet-problems").Q<Label>(className: "holysheet-empty").text);

                window.SelectPage("Settings");
                Assert.AreEqual(5, root.Query<Foldout>(className: "holysheet-section").ToList().Count);
                Assert.AreEqual(HolySheetProject.LoadConfig().Sources.Count, root.Query(className: "holysheet-card").ToList().Count);
                Assert.IsNotNull(root.Query<DropdownField>().ToList().FirstOrDefault(field => field.label == "Language"));

                HolySheetText.Override = HolySheetLanguage.Korean;
                window.SelectPage("Settings");
                Assert.AreEqual("소스", root.Query<Foldout>(className: "holysheet-section").First().text);
                StringAssert.StartsWith($"검사 완료 · 테이블 {DemoTables.Definition.Tables.Length}개", root.Q<Label>(className: "holysheet-status").text);
                window.SelectPage("Tables");
                Assert.AreEqual("문제가 없습니다.", root.Q<Label>(className: "holysheet-problems-bar__text").text);
                Assert.AreEqual("시트 3개", HolySheetText.Count(3, "sheet"));
                Assert.AreEqual("Not in the table yet", HolySheetText.T("Not in the table yet"), "A missing translation shows the English text.");
            }
            finally
            {
                window.Close();
            }
        }

        [Test]
        public void Tree_nodes_and_problems_open_the_file_they_came_from()
        {
            HolySheetProject.RunBlocking(commit: false, offline: true);
            string item = HolySheetWindow.FindSourceFile("Item.xlsx");
            Assert.IsTrue(File.Exists(item), item);
            StringAssert.EndsWith("Demo/Sheets/Item.xlsx", item.Replace('\\', '/'));
            StringAssert.EndsWith("Sheets/Localization/Text.csv", HolySheetWindow.FindSourceFile("Localization", "Text").Replace('\\', '/'));
            Assert.IsNull(HolySheetWindow.FindSourceFile("Nope.xlsx"));
        }

        [UnityTest]
        public IEnumerator The_window_reads_the_sheets_by_itself_after_a_restart()
        {
            foreach (HolySheetWindow open in Resources.FindObjectsOfTypeAll<HolySheetWindow>())
            {
                open.Close();
            }

            typeof(HolySheetProject).GetProperty(nameof(HolySheetProject.LastResult)).SetValue(null, null);
            HolySheetWindow.Open();
            HolySheetWindow window = UnityEditor.EditorWindow.GetWindow<HolySheetWindow>();
            try
            {
                window.SelectPage("Tables");
                DateTime deadline = DateTime.UtcNow.AddSeconds(60);
                while ((HolySheetProject.LastResult == null || HolySheetProject.IsRunning) && DateTime.UtcNow < deadline)
                {
                    yield return null;
                }

                Assert.IsNotNull(HolySheetProject.LastResult, "Opening the window with nothing read did not check the sheets within 60 seconds.");
                Assert.IsTrue(HolySheetProject.LastResult.Build.Success, Errors(HolySheetProject.LastResult.Build));
                Assert.IsNull(HolySheetProject.LastResult.Commit, "The automatic run only checks; it writes nothing.");
                yield return null;
                yield return null;
                CollectionAssert.AreEqual(new[] { "Excel", "Character.xlsx", "Job", "Level", "Skill" }, TreeNames(window.rootVisualElement).Take(5).ToArray());
            }
            finally
            {
                window.Close();
            }
        }

        [UnityTest]
        public IEnumerator Only_a_Google_tab_has_local_copy_actions_and_a_download_time()
        {
            var config = new HolySheetConfig { BaseDirectory = _temp };
            var google = new SourceConfig { Type = SourceType.Google, Name = "Remote", SpreadsheetId = "test", Snapshot = "snapshot" };
            google.Sheets.Add(new GoogleSheetRef("Hello", 7));
            config.Sources.Add(google);
            config.Sources.Add(new SourceConfig { Type = SourceType.Csv, Path = "local" });
            const string csv = "Key<int>,int\nId,Power\n,\n1,150\n";
            Directory.CreateDirectory(Path.Combine(_temp, "snapshot"));
            Directory.CreateDirectory(Path.Combine(_temp, "local"));
            File.WriteAllText(Path.Combine(_temp, "snapshot", "Hello.csv"), csv);
            File.WriteAllText(Path.Combine(_temp, "local", "Local.csv"), csv);
            using var server = new SingleTabCsv(csv);
            var options = new TableBuildOptions { Offline = true, KeepGooglePreview = true, HttpHandler = server };
            var build = TableBuild.Run(config, options);
            build = GoogleTabCopies.ChangeAsync(config, build, "Remote", "Hello", true, options).GetAwaiter().GetResult();
            var original = HolySheetProject.LastResult;
            var last = typeof(HolySheetProject).GetProperty(nameof(HolySheetProject.LastResult));
            last.SetValue(null, new HolySheetRunResult(build, null, null));
            HolySheetWindow.Open();
            var window = UnityEditor.EditorWindow.GetWindow<HolySheetWindow>();
            try
            {
                yield return null;
                yield return null;
                var tree = window.rootVisualElement.Q<TreeView>();
                var googleRow = Row("Hello");
                StringAssert.IsMatch(@"Local copy · \d{2}:\d{2}", googleRow.Q<Label>(className: "holysheet-tree-row__note").text);
                var actions = Actions(googleRow);
                CollectionAssert.Contains(actions, "Download again from Google");
                CollectionAssert.Contains(actions, "Show local CSV in folder");
                CollectionAssert.Contains(actions, "Use Google original");
                CollectionAssert.DoesNotContain(Actions(Row("Local")), "Use local copy");
                CollectionAssert.DoesNotContain(Actions(Row("Remote")), "Download again from Google");

                HolySheetText.Override = HolySheetLanguage.Korean;
                window.SelectPage("Tables");
                yield return null;
                yield return null;
                tree = window.rootVisualElement.Q<TreeView>();
                googleRow = Row("Hello");
                StringAssert.IsMatch(@"로컬 사본 · \d{2}:\d{2} 갱신", googleRow.Q<Label>(className: "holysheet-tree-row__note").text);
                CollectionAssert.Contains(Actions(googleRow), "Google에서 다시 받기");

                VisualElement Row(string name) => tree.Query<Label>(className: "holysheet-tree-row__name").ToList().Single(label => label.text == name).parent;
                string[] Actions(VisualElement row)
                {
                    tree.SetSelection((int)row.userData);
                    var menu = new DropdownMenu();
                    using var evt = ContextualMenuPopulateEvent.GetPooled(null, menu, row, null);
                    row.SendEvent(evt);
                    return menu.MenuItems().OfType<DropdownMenuAction>().Select(action => action.name).ToArray();
                }
            }
            finally
            {
                window.Close();
                last.SetValue(null, original);
            }
        }

        private sealed class SingleTabCsv : HttpMessageHandler
        {
            private readonly string csv;
            public SingleTabCsv(string csv) => this.csv = csv;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Assert.AreEqual("format=csv&gid=7", request.RequestUri.Query.TrimStart('?'));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent(csv, Encoding.UTF8, "text/csv") });
            }
        }

        [UnityTest]
        public IEnumerator Refresh_shortcuts_check_the_sheets_without_building()
        {
            HolySheetProject.RunBlocking(commit: false, offline: true);
            HolySheetWindow.Open();
            var window = UnityEditor.EditorWindow.GetWindow<HolySheetWindow>();
            try
            {
                yield return null;
                yield return null;
                var root = window.rootVisualElement;
                typeof(HolySheetWindow).GetField("_refreshProject", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    .SetValue(window, (Action)(() => HolySheetProject.RunAsync(commit: false, offline: true)));
                window.Focus();
                foreach (var (key, modifiers) in new[]
                {
                    (KeyCode.R, EventModifiers.Control),
                    (KeyCode.R, EventModifiers.Command),
                    (KeyCode.F5, EventModifiers.None)
                })
                {
                    if (key == KeyCode.F5)
                    {
                        root.Q<TreeView>().Focus();
                    }

                    var before = HolySheetProject.LastResult;
                    window.SendEvent(new Event { type = EventType.KeyDown, keyCode = key, modifiers = modifiers });
                    DateTime deadline = DateTime.UtcNow.AddSeconds(10);
                    while (HolySheetProject.LastResult == before && DateTime.UtcNow < deadline)
                    {
                        yield return null;
                    }

                    Assert.AreNotSame(before, HolySheetProject.LastResult, $"{modifiers} + {key}");
                    Assert.IsNull(HolySheetProject.LastResult.Commit);
                    Assert.IsNull(HolySheetProject.LastResult.Build.Only);
                }
            }
            finally
            {
                window.Close();
            }
        }

        private static string[] TreeNames(VisualElement root)
        {
            return root.Query<Label>(className: "holysheet-tree-row__name").ToList()
                .Where(label => label.resolvedStyle.display == DisplayStyle.Flex && label.text.Length > 0 && label.parent.parent.resolvedStyle.display == DisplayStyle.Flex)
                .Select(label => Plain(label.text)).ToArray();
        }

        private static string Plain(string text) => System.Text.RegularExpressions.Regex.Replace(text, "<[^>]*>", "");

        private static void PressEnterInSearch(VisualElement root)
        {
            var search = root.Q<UnityEditor.UIElements.ToolbarSearchField>(className: "holysheet-search");
            VisualElement input = search.Q<TextField>() ?? (VisualElement)search;
            using (KeyDownEvent enter = KeyDownEvent.GetPooled('\0', KeyCode.Return, EventModifiers.None))
            {
                enter.target = input;
                input.SendEvent(enter);
            }
        }
    }
}
