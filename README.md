# HolySheet — game tables from sheets

HolySheet turns xlsx workbooks, CSV folders and Google Sheets into typed, **read-only** C# tables with binary data files.
The generated code and data run on any .NET: a Unity client and a plain .NET server load the same files.
Formerly UniGoogleSheets (UGS).

[한국어 사용자 가이드](docs/src/content/docs/index.md)

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
| Tools | A Unity editor window and a `holysheet` command line. Both read the same `holysheet.json` and produce identical output. |
| Safety | Loaded tables cannot be modified. Every data file is hash-checked on load; the manifest can be signed. |
| Live reload | `GameTables.Watch(dir)` keeps tables current while the game or server runs. |

## Install

**Unity** — Package Manager → *Add package from git URL*:

```
https://github.com/shlifedev/uni-google-sheet.git?path=src
```

Then add the serializer you want the tables to use ([MemoryPack](https://github.com/Cysharp/MemoryPack) or
[MessagePack](https://github.com/MessagePack-CSharp/MessagePack-CSharp)) to the project; the window warns when it is
missing.

**Command line** — needs the .NET 10 SDK:

```bash
dotnet build dotnet/HolySheet.Cli -c Release
dotnet dotnet/HolySheet.Cli/bin/Release/net10.0/holysheet.dll help      # or: dotnet pack, then dotnet tool install
```

## Quick start

In Unity open **Tools → HolySheet → Tables**, press *Create holysheet.json*, put a workbook in the `Sheets` folder and press **Build**.
From a terminal the same thing is:

```bash
holysheet init --sample     # holysheet.json plus a small sample workbook as CSV
holysheet build             # generated code and data
holysheet watch             # build again whenever a sheet is saved
```

`init --sample` stops before writing if the config or any sample CSV already exists. Add `--force` to replace those files deliberately.
A build reports the path of any existing file it cannot identify as HolySheet output and stops before changing the output.

A table sheet has three header rows (configurable) and then data:

| Key\<int> | string | Enum\<EItemKind> | int | float[] | #note |
|---|---|---|---|---|---|
| **Id** | **Name** | **Kind** | **Price** | **Stats** | |
| Item id | Display name | Kind of item | Price in gold | Stat values | Designer notes |
| 1001 | Wooden Sword | Weapon | 100 | 3, 1.5 | starter |
| ~2002 | Not ready | Weapon | 0 | | this row is skipped |

## Sheet rules

These rules are applied, in this order, before any cell is typed. Sheets that add rows or columns are merged in name order,
so an xlsx file, a CSV folder and a Google spreadsheet holding the same sheets build the same bytes. `holysheet build` can also write the result as CSV
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
| `T[]` (also `List<T>`) | `ReadOnlyArray<T>` | `1, 2, 3` or `[1, 2, 3]`. See [Arrays](#arrays). |
| `Enum<EKind>` or just `EKind` | `EKind` | Member name. Flags cells take `A, B` or `A\|B`; inside an array element, `A\|B`. |
| `Key<T>` | lookup | Unique. The first key is the primary key (`Get`, `Find`, `TryGet`, `Contains`, indexer); others get `GetByField`. Integers, text or enums. |
| `Index<T>` | lookup | Not unique: `ListByField(value)` returns every matching row. |
| `Ref<Table>` | `TableRef` | The key of a row in another table; checked at build time. `Ref<Table>?` allows an empty cell; `Ref<Table>[]` is an array of keys. |
| `Mapping<EName>` | an enum | The column's text becomes enum members whose values are the row keys. Not stored. |

### Arrays

An array cell lists its elements separated by commas. Brackets are optional, and spaces around elements are ignored.
Put an element in double quotes when it holds a comma, a bracket or leading/trailing spaces; inside quotes, `\"` is a
quote and `\\` a backslash. A blank cell or `[]` is an empty array.

| Cell | Elements |
|---|---|
| `1, 2, 3` or `[1, 2, 3]` | `1` `2` `3` |
| `sword, blade` | `sword` `blade` |
| `"Hello, world", plain` | `Hello, world` `plain` |
| `"say \"hi\""` | `say "hi"` |
| `Read\|Write, Read` (a Flags enum array) | `Read \| Write` `Read` |

The build warns about two cells that parse but probably mean something else:

- **A number shown with thousands separators.** Google Sheets hands over what a cell displays, so `1000` formatted as
  `1,000` arrives as two elements, `1` and `0`. Format array columns as plain text, or write a real list in brackets
  (`[1,000]`) to say it is one.
- **Text still written with `|`.** `sword|blade` in a `string[]` column is one element. Write `sword, blade`, or quote it
  (`"sword|blade"`) if the bar belongs to the text.

`sheet.arraySeparator` changes the separator (one character). Projects whose `holysheet.json` says `"|"` keep reading `1|2|3`;
`[1, 2]` and quoted elements work with any separator.

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

Run `holysheet watch` alongside the game to rebuild the data whenever you save a sheet.

## Tamper detection

Every data file is listed in `tables.manifest.json` with its size and SHA-256, and checked on load. A modified,
truncated or swapped file raises `TableLoadException` with `TableLoadError.HashMismatch`; data built for different
generated code raises `SchemaMismatch` and names the table. Nothing is partially applied.

Hashes catch damage, not a deliberate replacement of both a file and the manifest. For that, sign the manifest:

```bash
holysheet keygen --write-config     # writes holysheet.private.key, puts the public key in holysheet.json
holysheet build                     # signs the manifest; bakes the public key into the generated code
holysheet verify Generated/Data     # checks hashes and signature of a data folder
```

The private key stays on the build machine (`signing.privateKeyFile`, or `signing.privateKeyEnv` in CI). Clients
verify with the public key and refuse an unsigned or wrongly signed manifest (`SignatureInvalid`).

## Metadata for patching

HolySheet does not ship a patch system; it leaves what one needs. The manifest is plain JSON, so any tool can read it:

```json
{
  "holysheet": 1,
  "serializer": "memorypack",
  "schema": "9d92…",
  "content": "5c1e…",
  "build": "1.4.0 (a1b2c3d)",
  "tool": "holysheet/2.0.0",
  "tables": [
    { "name": "Item", "file": "Item.bytes", "rows": 6, "bytes": 412, "hash": "…", "schema": "…" }
  ],
  "signature": { "algorithm": "rsa-sha256", "value": "…" }
}
```

- `schema` covers every table layout and enum and equals `GameTables.SchemaHash`. `content` covers every data file
  hash and identifies this exact build of the data. `build` is your label (`holysheet build --label "…"`).
- The tool writes one table per line, so the file diffs cleanly in version control.
- The signature covers everything except itself, in the form the tool writes. Reformatting the file does not break it;
  changing any value does.
- `TableManifest.Diff(installed, latest)` (and `holysheet diff old new --json`) lists the tables added, changed and removed,
  the files to download with their sizes and hashes, and whether the layout changed (`RequiresNewCode`): if it did
  not, a client can load the new data with the code it already has.
- Each table has its own `schema` hash, so a patch can tell "new data" from "needs a new client" per table.
- Unknown members, at the top or in a table, are preserved, so a patch tool can add its own without breaking older
  clients.
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
reader can be selected with `"csv": { "backend": "sep" }` wherever .NET 8+ is available: the `holysheet` command line and
the Unity 7 editor, which runs on CoreCLR (verified on 7000.0.0a16, .NET 10.0.10). Unity 6 runs on Mono, where Sep
cannot load; the package excludes it there. Both readers give identical results, checked by a conformance suite.

Sep is not the default because it does not make this pipeline faster. Sep scans 7.9 MB of CSV in about 2 ms, but the
sheet rules need every cell as a string, and creating those strings takes about 12 ms with either reader. What did
make large builds fast was reading files in parallel and server GC: 8 tables × 100,000 rows (63 MB of CSV) build in
about 0.5 s.

## Configuration

`holysheet.json` lives in the project root (next to `Assets` in Unity). Paths are relative to it.

```jsonc
{
  "sources": [
    { "type": "xlsx", "path": "Sheets" },
    { "type": "csv",  "path": "Sheets/Csv" },
    { "type": "google", "spreadsheetId": "1AbC…",
      "auth": { "type": "serviceAccount", "keyFile": "secrets/sa.json" },   // or "public", "apiKey", "accessToken", "custom", "command"
      "snapshot": "Sheets/Snapshot" },                                       // saved CSV: diffs and --offline builds
    { "type": "googleFolder", "folderId": "1XyZ…",                          // every spreadsheet and .xlsx in a Drive folder
      "auth": { "type": "apiKey", "apiKeyEnv": "HOLYSHEET_GOOGLE_KEY" },
      "snapshot": "Sheets/DriveSnapshot" }
  ],
  "output":  { "code": "Assets/Game/Generated/Tables", "data": "Assets/StreamingAssets/Tables", "csv": null, "schema": true },
  "codegen": { "namespace": "Game.Data", "tablesClass": "GameTables", "serializer": "memorypack" },
  "sheet":   { "header": ["type", "name", "comment"], "arraySeparator": "," },
  "csv":     { "backend": "auto" },
  "signing": { "publicKey": "rsa:…", "privateKeyFile": "holysheet.private.key", "privateKeyEnv": "HOLYSHEET_PRIVATE_KEY" },
  "build":   { "label": null, "parallel": true, "cache": ".holysheet/cache", "validators": [] }
}
```

### Google Sheets and Google Drive

Two kinds of Google source, both added from the window (Settings → Sources → Add) or in `holysheet.json`:

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
| `command` | credentials from a command (`"command": "gcloud auth print-access-token"`): it prints an access token, JSON with `access_token`, or a service account's JSON key | yes | yes |
| `custom` | credentials from a C# class of your project (`"provider": "vault"`), Unity editor only | yes | yes |

Requests are kept few, because the Sheets API allows 60 reads a minute per account:

- With credentials, a spreadsheet costs two Sheets API requests however many tabs it has: the tab list and one
  `values:batchGet` for all tabs.
- Before downloading, one Drive request asks for the file's version (a Drive folder's listing already includes it). If
  it is the version downloaded last time, the tabs are read from the local copy in `.holysheet/cache/google` and nothing else
  is requested. A manual online refresh in the Unity window or desktop app (Ctrl+R, Cmd+R or F5) fetches the current
  data even when Drive still reports the previous version. Ordinary checks, builds and CLI watch keep using the version cache.
- A "too many requests" answer or a server error is retried up to five times, waiting 1, 2, 4, 8 seconds plus up to a
  second at random, or as long as Google's `Retry-After` header says.

Keys come from files, environment variables, a credential command or a provider class, never from the command line. With `snapshot` set, what was fetched is
also saved as CSV (one folder per workbook for a Drive folder): commit it for reviewable diffs, and build from it with
`--offline`. In the Unity or desktop tree, right-click a Google tab and choose **Use local copy** to download its CSV.
Checks and builds read that copy. **Download again from Google** refreshes that tab and its preview. **Use Google original**
returns the tab to normal reads. Ctrl+R, Cmd+R or F5 refreshes all sources and their local copies.

#### Your own credentials

When the credentials live somewhere HolySheet does not read on its own (a secret manager, the OS keychain, a company token
service), write a provider class in any editor assembly. Nothing registers it: the editor finds every class that
implements `IGoogleCredentialProvider`, and lists it under Access → Custom provider by its class name, or by the name in
the attribute.

```csharp
using HolySheet.Core.Sources;

[HolySheetCredentialProvider("vault")]   // optional; without it the name is "VaultCredentials"
public sealed class VaultCredentials : IGoogleCredentialProvider
{
    public async Task<GoogleCredential> GetCredentialAsync(GoogleCredentialRequest request, CancellationToken cancellationToken)
    {
        string keyJson = await MyVault.ReadAsync("google/holysheet-reader", cancellationToken);
        return GoogleCredential.FromServiceAccountKey(keyJson);   // or FromAccessToken(token, account), FromApiKey(key)
    }
}
```

```jsonc
"auth": { "type": "custom", "provider": "vault" }
```

The provider runs on a background thread, once per source per build, so it must not call Unity's API. A service
account key is signed and exchanged for a token by HolySheet. The `holysheet` CLI cannot load editor classes: for CI, use
`"type": "command"` with a command that prints the same credentials, or build `--offline` from the snapshot.

## Partial builds

Build only some workbooks or sheets, and take everything else as it was at the last build: to ship your changes to
`Item` while a teammate is still halfway through `Monster` in the same Google spreadsheet, or to avoid downloading forty
tabs to pick up one.

- **Window**: select nodes in the tree (Ctrl/Cmd or Shift click). A workbook means all its sheets, a table means every
  sheet it is built from. The bar under the tree, or the right-click menu, runs Check or Build on just that.
- **CLI**: `holysheet build --only Item.xlsx --only "Monster.xlsx/Monster.Boss"` (or comma separated). A workbook is named
  as the tree shows it: the file name for xlsx, the folder name for CSV, the spreadsheet name for Google.

Every table is still built, so the manifest, the tables class and the enums agree with each other; tables made only of
unselected sheets come out byte for byte the same, so their files are not written again. Only the selected tabs are
downloaded from Google.

The unselected sheets come from the **sheet cache** (`build.cache`, default `.holysheet/cache`): every successful build writes
the sheets it read there. The folder ignores itself in git. A partial build refuses to run when the manifest in the data
folder is not the one this machine's last build wrote, for example after a pull: the cached sheets may be older than
that data, and building from them would put old values back. Run a full build then.

## Project validation rules

Implement `HolySheet.Core.ITableValidator` in an editor assembly to check project-specific data before output is written. Unity discovers public validator classes automatically. To share a rule with the desktop app and CLI, build it as a .NET Standard 2.1 DLL and list its path in `build.validators`. The [sample validator](dotnet/samples/Sample.Validators/ItemPriceValidator.cs) reports an error at the original price cell for a negative price and a warning for a free item.

## Command line

```
holysheet init [--sample] [--unity]     create holysheet.json
holysheet build [--offline] [--label]   read the sources, write code and data
holysheet build --only Item.xlsx        partial build: read only these again (see below)
holysheet check                         read and validate only
holysheet watch [--interval 30]         build on every change (Google sources are polled)
holysheet diff <old> <new> [--json]     what changed between two manifests
holysheet verify <dataDir>              hashes and signature of a data folder
holysheet keygen [--write-config]       create a signing key pair
holysheet info                          runtime, CSV backends, serializers
```

`--json` gives machine-readable output on every command. Exit codes: `0` success, `1` sheet errors or failed
verification, `2` bad command line, `3` the tool could not run. In CI without the .NET tool, Unity can build too:

```bash
Unity -batchmode -quit -projectPath . -executeMethod HolySheet.Editor.HolySheetCommandLine.Build
```

Custom sheet conventions plug into the transform pipeline:

```csharp
[InitializeOnLoadMethod]
static void AddRules() => HolySheetProject.CreatePipeline = () =>
    TransformPipeline.CreateDefault().InsertAfter(ReadTablesStep.StepName, new MyStep());
```

Editor scripts in the default assembly see the engine as is. An editor script inside its own assembly definition adds
`HolySheet.Core` (the engine) and `HolySheet.Editor` (the window) to its references.

## Repository layout

```
src/                    the Unity package (com.lumera.holysheet)
  Runtime/              what generated code depends on; no dependencies; C# 9; compiled by Unity and by .NET unchanged
  Editor/               the window and the build runner
    Core/               the engine: sources, sheet rules, validation, code and data generation; C# 9; compiled by Unity and by .NET unchanged
    Plugins/CoreClr/    the optional Sep CSV backend as DLLs, loaded only by the Unity 7 (CoreCLR) editor
  Samples~/Demo/        a scene that loads, shows, reloads and tamper-tests the sample tables
dotnet/
  HolySheet.Core/       builds src/Editor/Core for .NET (netstandard2.1, no dependencies)
  HolySheet.Csv.Sep/    the optional Sep CSV backend (.NET 8+)
  HolySheet.Cli/        the holysheet command
  HolySheet.Runtime/    builds src/Runtime for .NET
  HolySheet.Tests/      282 tests
  samples/              a sample workbook, its generated tables for both serializers, and a .NET server
unity/HolySheet.Dev/    the Unity project used to develop and test the package
tools/                  build-unity-plugins.sh, make-sample-workbook.py
```

## Developing

```bash
dotnet test dotnet/HolySheet.Tests                     # engine, runtime, both serializers, CLI
tools/build-unity-plugins.sh                           # after changing dotnet/HolySheet.Csv.Sep
unity test unity/HolySheet.Dev --mode EditMode         # the package inside a real editor
unity test unity/HolySheet.Dev --mode PlayMode         # the demo scene
```

The Unity tests pass on 6000.3 (Mono) and on the 7000.0 alpha (CoreCLR). The engine is shipped to Unity as a compiled
netstandard2.1 DLL because it is written in a newer C# than Unity compiles; the runtime is shipped as C# 9 source.
`unity/HolySheet.Dev/Assets/Samples/Demo` is a symbolic link to `src/Samples~/Demo`, so the sample exists once.

## License

MIT
