# UGS Demo

Open `Demo.unity` and press Play. The scene loads the tables built from the workbooks in `Sheets/` and shows them.

It needs the **MemoryPack** package in the project, because the demo tables were generated for that serializer.

Things to try while it runs:

- **Reload**: edit a workbook in `Sheets/`, run `Tools > UGS > Build Tables`, and watch the values change without leaving Play Mode.
  The log at the bottom names the tables that changed; the others were not decoded again.
- **Parallel**: toggles decoding on several threads.
- **Tamper test**: changes one byte of one table in memory and shows the loader refusing it.

To rebuild the demo tables, run `Tools > UGS > Demo > Use the demo config` once. It writes a `ugs.json` in the project
root that points at this folder.

| Folder | What it holds |
|---|---|
| `Sheets/` | The data of a mid-sized online RPG, split the way a team would split it (names are made up):<br>`Common.xlsx` enums and game config · `Character.xlsx` jobs, levels, skills · `Item.xlsx` items, equipment, upgrades, sets, enhancement · `World.xlsx` regions, maps, dungeons, portals, NPCs · `Monster.xlsx` monsters, bosses, spawns, drops · `Quest.xlsx` quests and objectives · `Shop.xlsx` shops, goods, gacha · `Localization/Text.csv` UI text. Regenerate with `tools/make-demo-workbooks.py`. |
| `Generated/` | Generated row and table classes. Do not edit; they are overwritten by every build. |
| `Resources/UgsDemo/` | One `.bytes` file per table, the manifest and the schema description. |
| `Scripts/` | `UgsDemo.cs`, the test bench, and an editor menu item. |
