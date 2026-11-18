# UGS — game tables from sheets

UGS turns xlsx workbooks, CSV folders and Google Sheets into typed, **read-only** C# tables with binary data files.
The generated code and data run on any .NET: a Unity client and a plain .NET server load the same files.

```csharp
GameTables tables = GameTables.Load("Tables");            // every file is verified against the manifest

ItemRow potion  = tables.Item.Get(2001);                   // primary key
ItemRow sword   = tables.Item.GetByCode("wooden_sword");   // secondary key
var tierOne     = tables.Item.ListByTier(1);               // index: ReadOnlyArray<ItemRow>
MonsterRow boss = tables.Monster.Get((int)EMonsterId.Dragon);
ItemRow weapon  = tables.Item.Get(boss.Weapon);            // a reference resolves through the table it points at

potion.Price = 1;          // does not compile: rows have no setters
potion.Stats[0] = 1f;      // does not compile: arrays are handed out as ReadOnlyArray<T>
```

Version 2 is a rewrite. Nothing from 1.x carries over.

| | |
|---|---|
| Sources | xlsx, CSV folders, Google Sheets (public link, API key, service account). Mix them freely. |
| Output | C# row and table classes plus one binary file per table, for **MemoryPack** or **MessagePack**. |
| Hosts | Unity 6000.0 and newer (Mono, IL2CPP, and the CoreCLR editor of Unity 7), and .NET (netstandard2.1 / .NET 8+). |
| Tools | A Unity editor window and a `ugs` command line. Both read the same `ugs.json` and produce identical output. |
| Safety | Loaded tables cannot be modified. Every data file is hash-checked on load; the manifest can be signed. |
| Live reload | `GameTables.Watch(dir)` keeps tables current while the game or server runs. |

## Install

**Unity** — Package Manager → *Add package from git URL*:

```
https://github.com/shlifedev/uni-google-sheets.git?path=src
```

Then add the serializer you want the tables to use ([MemoryPack](https://github.com/Cysharp/MemoryPack) or
[MessagePack](https://github.com/MessagePack-CSharp/MessagePack-CSharp)) to the project; the window warns when it is
missing.

**Command line** — needs the .NET 10 SDK:

```bash
dotnet build dotnet/UGS.Cli -c Release
dotnet dotnet/UGS.Cli/bin/Release/net10.0/ugs.dll help      # or: dotnet pack, then dotnet tool install
```

## Quick start

In Unity open **Tools → UGS → Tables**, press *Create ugs.json*, put a workbook in the `Sheets` folder and press **Build**.
From a terminal the same thing is:

```bash
ugs init --sample     # ugs.json plus a small sample workbook as CSV
ugs build             # generated code and data
ugs watch             # build again whenever a sheet is saved
```

A table sheet has three header rows (configurable) and then data:

| Key\<int> | string | Enum\<EItemKind> | int | float[] | #note |
|---|---|---|---|---|---|
| **Id** | **Name** | **Kind** | **Price** | **Stats** | |
| Item id | Display name | Kind of item | Price in gold | Stat values | Designer notes |
| 1001 | Wooden Sword | Weapon | 100 | 3\|1.5 | starter |
| ~2002 | Not ready | Weapon | 0 | | this row is skipped |

## Sheet rules

These rules are applied, in this order, before any cell is typed. Sheets that add rows or columns are merged in name order,
so an xlsx file, a CSV folder and a Google spreadsheet holding the same sheets build the same bytes. `ugs build` can also write the result as CSV
(`output.csv`) so you can see exactly what the build saw.

| Rule | Effect |
|---|---|
| Sheet name starts with `#` or `~` | The sheet is ignored. |
| `Table` | Defines the table `Table`. |
| `Table.Config` | The table written sideways: one field per row (type, name, comment, value). It has exactly one row, exposed as `tables.Table.Value`. |
| `Table.Anything` | More rows for `Table`. Same columns by name and type; their order may differ. Several such sheets are appended in name order. |
| `Table.Col.Anything` | More columns for `Table`, matched to its rows by the first `Key<>` column. Rows it does not list get blank cells; a key the table does not have is an error. Several such sheets are joined in name order. |
| `Enums`, `Something.Enums` | Enum definitions, one per column. A column to the right with no name holds values (`5`, `0x10`, `1 << 3`, `Read \| Write`). `Flags<int>` in the type row makes a `[Flags]` enum. |
| Any header cell of a column starts with `#` or `~` | The column is ignored. |
| The first cell of a row (column A) starts with `#` or `~` | The row is ignored. Blank rows are ignored too. |
| The same field name on several columns | The columns become one array, left to right. |

Header rows default to `type`, `name`, `comment`. Set `sheet.header` to reorder them
(`["comment", "name", "type"]`) or to drop the comment row.

CSV is read by RFC 4180: quoted fields may contain commas, quotes (doubled) and line breaks; CRLF, LF and CR all end a
row; a byte order mark is ignored; nothing is trimmed. Google Sheets are fetched as CSV, one request per tab, concurrently.

### Types

| Type cell | C# | Notes |
|---|---|---|
| `bool` `byte` `sbyte` `short` `ushort` `int` `uint` `long` `ulong` `float` `double` `string` | the same | An empty cell is the default (`0`, `false`, `""`). Parsing never depends on the machine's culture. |
| `DateTime` | `DateTime` (UTC) | `2026-01-31`, `2026-01-31 13:30:00`, ISO 8601. |
| `T[]` (also `List<T>`) | `ReadOnlyArray<T>` | `1\|2\|3` or `[1, 2, 3]`. The separator is configurable. |
| `Enum<EKind>` or just `EKind` | `EKind` | Member name. Flags cells take `A, B` or `A\|B`. |
| `Key<T>` | lookup | Unique. The first key is the primary key (`Get`, `Find`, `TryGet`, `Contains`, indexer); others get `GetByField`. Integers, text or enums. |
| `Index<T>` | lookup | Not unique: `ListByField(value)` returns every matching row. |
| `Ref<Table>` | `TableRef` | The key of a row in another table; checked at build time. `Ref<Table>?` allows an empty cell; `Ref<Table>[]` is an array of keys. |
| `Mapping<EName>` | an enum | The column's text becomes enum members whose values are the row keys. Not stored. |

Every problem is reported with its cell and a fix, and a build with any error writes nothing:

```
error: Game.xlsx / Item!G12
  'ten' is not a whole number. Write it like 10, without decimals or thousands separators.
  fix: The column 'Price' is int. Example: 10
```

## Loading

```csharp
GameTables tables = GameTables.Load(directory);                                    // sequential
GameTables tables = GameTables.Load(directory, new TableLoadOptions { Parallel = true });
GameTables tables = await GameTables.LoadAsync(source);                            // no blocking I/O

// Unity
GameTables.Load(new StreamingAssetsTableSource("Tables"));                         // desktop, iOS, consoles
await GameTables.LoadAsync(new StreamingAssetsTableSource("Tables"));              // Android, WebGL
GameTables.Load(TableSource.Preload(new ResourcesTableSource("Tables")), options); // Resources + parallel decode
GameTables.Load(TableSource.FromDelegate(name => myPatchCache.Read(name)));        // anything else
```

Parallel loading is off by default: not every platform has threads, and how much it helps depends on the garbage
collector. Measured on 8 tables × 100,000 rows (72.6 MB of MemoryPack data, .NET 10, Apple M-series, best of 7):

| | sequential | parallel |
|---|---|---|
| Server GC (typical for a game server) | 91 ms | 16 ms |
| Workstation GC (the default for apps) | 196 ms | 131 ms |

Verifying every file's SHA-256 is about 10–20 ms of those sequential figures. A key lookup is about 30 ns.

### Read-only, for real

A loaded table set is an immutable snapshot. Rows store their values in private fields that only the serializer
writes; the public surface is get-only properties; arrays are exposed as `ReadOnlyArray<T>`, a struct with no setter
and no mutable span; rows and tables have no public constructors. There is no supported way to change table memory
from game code, by accident or on purpose.

## Reloading

```csharp
using TableWatcher<GameTables> watcher = GameTables.Watch(directory);
watcher.Reloaded += change => Debug.Log("changed: " + string.Join(", ", change.ChangedTables));
watcher.ReloadFailed += exception => Debug.LogWarning(exception.Message);   // the current tables stay in use

int hp = watcher.Current.Monster.Get(1).Hp;   // always a complete, consistent snapshot
```

A reload never changes a snapshot: it builds a new one and swaps it in with a single reference write. Tables whose
data did not change are the same objects in both snapshots, so a reload after editing one sheet decodes one table.
The watcher uses file system notifications with a debounce, falls back to polling where those are unavailable, and
has a manual mode (`CheckNow()`) for platforms without threads. In Unity its events arrive on the main thread.

Together with `ugs watch` (or *Auto build* in the editor window) this gives edit-sheet → see-it-in-the-running-game.

## Tamper detection

Every data file is listed in `tables.manifest.txt` with its size and SHA-256, and checked on load. A modified,
truncated or swapped file raises `TableLoadException` with `TableLoadError.HashMismatch`; data built for different
generated code raises `SchemaMismatch` and names the table. Nothing is partially applied.

Hashes catch damage, not a deliberate replacement of both a file and the manifest. For that, sign the manifest:

```bash
ugs keygen --write-config     # writes ugs.private.key, puts the public key in ugs.json
ugs build                     # signs the manifest; bakes the public key into the generated code
ugs verify Generated/Data     # checks hashes and signature of a data folder
```

The private key stays on the build machine (`signing.privateKeyFile`, or `signing.privateKeyEnv` in CI). Clients
verify with the public key and refuse an unsigned or wrongly signed manifest (`SignatureInvalid`).

## Metadata for patching

UGS does not ship a patch system; it leaves what one needs.

```
ugs-manifest 1
serializer memorypack
schema 9d92…                    every table layout and enum; equals GameTables.SchemaHash
content 5c1e…                   every data file hash; identifies this exact build of the data
build 1.4.0 (a1b2c3d)           your label: ugs build --label "…"
table Item file=Item.bytes rows=6 bytes=412 hash=… schema=…
sign rsa-sha256 …
```

- `TableManifest.Diff(installed, latest)` (and `ugs diff old new --json`) lists the tables added, changed and removed,
  the files to download with their sizes and hashes, and whether the layout changed (`RequiresNewCode`): if it did
  not, a client can load the new data with the code it already has.
- Each table has its own `schema` hash, so a patch can tell "new data" from "needs a new client" per table.
- Unknown lines and attributes are preserved, so a patch tool can add its own without breaking older clients.
- `tables.schema.json` describes every column and enum for tools that have no generated code.
- `TableLoader.ReadManifest` reads only the manifest, to decide whether anything needs fetching.

## Choosing a serializer

`codegen.serializer` is `"memorypack"` (default) or `"messagepack"`. The data files are written from the schema alone
and are byte-identical to what each library would serialize; the tests prove this by reading them with the real
libraries and re-serializing.

- **MemoryPack** stores values as they sit in memory, so loading is close to a copy. Best when every consumer is C#.
- **MessagePack** is compact and readable from other languages. Choose it when a non-C# service reads the data too.

## CSV readers

The built-in CSV reader is used by default and works on every runtime. The [Sep](https://github.com/nietras/Sep)
reader can be selected with `"csv": { "backend": "sep" }` wherever .NET 8+ is available: the `ugs` command line and
the Unity 7 editor, which runs on CoreCLR (verified on 7000.0.0a16, .NET 10.0.10). Unity 6 runs on Mono, where Sep
cannot load; the package excludes it there. Both readers give identical results, checked by a conformance suite.

Sep is not the default because it does not make this pipeline faster. Sep scans 7.9 MB of CSV in about 2 ms, but the
sheet rules need every cell as a string, and creating those strings takes about 12 ms with either reader. What did
make large builds fast was reading files in parallel and server GC: 8 tables × 100,000 rows (63 MB of CSV) build in
about 0.5 s.

## Configuration

`ugs.json` lives in the project root (next to `Assets` in Unity). Paths are relative to it.

```jsonc
{
  "sources": [
    { "type": "xlsx", "path": "Sheets" },
    { "type": "csv",  "path": "Sheets/Csv" },
    { "type": "google", "spreadsheetId": "1AbC…",
      "auth": { "type": "serviceAccount", "keyFile": "secrets/sa.json" },   // or "public", "apiKey", "accessToken"
      "snapshot": "Sheets/Snapshot" },                                       // saved CSV: diffs and --offline builds
    { "type": "googleFolder", "folderId": "1XyZ…",                          // every spreadsheet and .xlsx in a Drive folder
      "auth": { "type": "apiKey", "apiKeyEnv": "UGS_GOOGLE_KEY" },
      "snapshot": "Sheets/DriveSnapshot" }
  ],
  "output":  { "code": "Assets/Game/Generated/Tables", "data": "Assets/StreamingAssets/Tables", "csv": null, "schema": true },
  "codegen": { "namespace": "Game.Data", "tablesClass": "GameTables", "serializer": "memorypack" },
  "sheet":   { "header": ["type", "name", "comment"], "arraySeparator": "|" },
  "csv":     { "backend": "auto" },
  "signing": { "publicKey": "rsa:…", "privateKeyFile": "ugs.private.key", "privateKeyEnv": "UGS_PRIVATE_KEY" },
  "build":   { "label": null, "parallel": true, "cache": ".ugs/cache" }
}
```

### Google Sheets and Google Drive

Two kinds of Google source, both added from the window (Settings → Sources → Add) or in `ugs.json`:

| Source | What it reads |
|---|---|
| `google` (Google Sheets) | One spreadsheet, by the id in its URL (`docs.google.com/spreadsheets/d/<id>`). Optionally only the tabs listed as `Name:gid`. |
| `googleFolder` (Google Drive folder) | Every Google spreadsheet and every uploaded `.xlsx` file directly in a folder (`drive.google.com/drive/folders/<id>`), each as a workbook named after the file. Files named `#…` or `~…` are skipped; subfolders are not read (add them as their own sources). A file added to the folder is picked up by the next build. |

| `auth.type` | Use it for | Spreadsheet | Folder |
|---|---|---|---|
| `public` | a spreadsheet shared as "anyone with the link" | yes (fetched as one xlsx export) | no: a folder cannot be listed without credentials |
| `apiKey` | link-shared spreadsheets and folders; enable the Sheets and Drive APIs for the key | yes | yes |
| `serviceAccount` | private files: share the spreadsheet or folder with the account's e-mail (`client_email`) as a viewer | yes | yes |
| `accessToken` | an OAuth token with the `drive.readonly` scope, from an environment variable | yes | yes |

Requests are kept few, because the Sheets API allows 60 reads a minute per account:

- With credentials, a spreadsheet costs two Sheets API requests however many tabs it has: the tab list and one
  `values:batchGet` for all tabs.
- Before downloading, one Drive request asks for the file's version (a Drive folder's listing already includes it). If
  it is the version downloaded last time, the tabs are read from the local copy in `.ugs/cache/google` and nothing else
  is requested. Frequent `ugs watch` polling therefore costs Drive requests only, until someone edits a file.
- A "too many requests" answer or a server error is retried up to five times, waiting 1, 2, 4, 8 seconds plus up to a
  second at random, or as long as Google's `Retry-After` header says.

Keys come from files or environment variables, never from the command line. With `snapshot` set, what was fetched is
also saved as CSV (one folder per workbook for a Drive folder): commit it for reviewable diffs, and build from it with
`--offline` or the window's Offline toggle. Partial builds download only the selected files and tabs.

## Partial builds

Build only some workbooks or sheets, and take everything else as it was at the last build: to ship your changes to
`Item` while a teammate is still halfway through `Monster` in the same Google spreadsheet, or to avoid downloading forty
tabs to pick up one.

- **Window**: select nodes in the tree (Ctrl/Cmd or Shift click). A workbook means all its sheets, a table means every
  sheet it is built from. The bar under the tree, or the right-click menu, runs Check or Build on just that.
- **CLI**: `ugs build --only Item.xlsx --only "Monster.xlsx/Monster.Boss"` (or comma separated). A workbook is named
  as the tree shows it: the file name for xlsx, the folder name for CSV, the spreadsheet name for Google.

Every table is still built, so the manifest, the tables class and the enums agree with each other; tables made only of
unselected sheets come out byte for byte the same, so their files are not written again. Only the selected tabs are
downloaded from Google.

The unselected sheets come from the **sheet cache** (`build.cache`, default `.ugs/cache`): every successful build writes
the sheets it read there. The folder ignores itself in git. A partial build refuses to run when the manifest in the data
folder is not the one this machine's last build wrote, for example after a pull: the cached sheets may be older than
that data, and building from them would put old values back. Run a full build then.

## Command line

```
ugs init [--sample] [--unity]     create ugs.json
ugs build [--offline] [--label]   read the sources, write code and data
ugs build --only Item.xlsx        partial build: read only these again (see below)
ugs check                         read and validate only
ugs watch [--interval 30]         build on every change (Google sources are polled)
ugs diff <old> <new> [--json]     what changed between two manifests
ugs verify <dataDir>              hashes and signature of a data folder
ugs keygen [--write-config]       create a signing key pair
ugs info                          runtime, CSV backends, serializers
```

`--json` gives machine-readable output on every command. Exit codes: `0` success, `1` sheet errors or failed
verification, `2` bad command line, `3` the tool could not run. In CI without the .NET tool, Unity can build too:

```bash
Unity -batchmode -quit -projectPath . -executeMethod UGS.Editor.UgsCommandLine.Build
```

Custom sheet conventions plug into the transform pipeline:

```csharp
[InitializeOnLoadMethod]
static void AddRules() => UgsProject.CreatePipeline = () =>
    TransformPipeline.CreateDefault().InsertAfter(ReadTablesStep.StepName, new MyStep());
```

Editor scripts in the default assembly see the engine as is. An editor script inside its own assembly definition adds
`UGS.Core` (the engine) and `UGS.Editor` (the window) to its references.

## Repository layout

```
src/                    the Unity package (com.shlifedev.ugs)
  Runtime/              what generated code depends on; no dependencies; C# 9; compiled by Unity and by .NET unchanged
  Editor/               the window and the build runner
    Core/               the engine: sources, sheet rules, validation, code and data generation; C# 9; compiled by Unity and by .NET unchanged
    Plugins/CoreClr/    the optional Sep CSV backend as DLLs, loaded only by the Unity 7 (CoreCLR) editor
  Samples~/Demo/        a scene that loads, shows, reloads and tamper-tests the sample tables
dotnet/
  UGS.Core/             builds src/Editor/Core for .NET (netstandard2.1, no dependencies)
  UGS.Csv.Sep/          the optional Sep CSV backend (.NET 8+)
  UGS.Cli/              the ugs command
  UGS.Runtime/          builds src/Runtime for .NET
  UGS.Tests/            282 tests
  samples/              a sample workbook, its generated tables for both serializers, and a .NET server
unity/UGS.Dev/          the Unity project used to develop and test the package
tools/                  build-unity-plugins.sh, make-sample-workbook.py
```

## Developing

```bash
dotnet test dotnet/UGS.Tests                     # engine, runtime, both serializers, CLI
tools/build-unity-plugins.sh                     # after changing dotnet/UGS.Csv.Sep
unity test unity/UGS.Dev --mode EditMode         # the package inside a real editor
unity test unity/UGS.Dev --mode PlayMode         # the demo scene
```

The Unity tests pass on 6000.3 (Mono) and on the 7000.0 alpha (CoreCLR). The engine is shipped to Unity as a compiled
netstandard2.1 DLL because it is written in a newer C# than Unity compiles; the runtime is shipped as C# 9 source.
`unity/UGS.Dev/Assets/Samples/Demo` is a symbolic link to `src/Samples~/Demo`, so the sample exists once.

## License

MIT
