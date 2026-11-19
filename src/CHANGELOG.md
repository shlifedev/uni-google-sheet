# Changelog

## 2.0.0

A rewrite. Nothing from 1.x carries over: the Apps Script bridge, the reflection-based runtime and the JSON data files are gone.

- Sources: xlsx workbooks, CSV folders and Google Sheets (public link, API key or service account), mixed freely.
- Output: read-only C# row and table classes plus binary data, for MemoryPack or MessagePack.
- The generated code depends only on `UGS.Runtime` and the chosen serializer, and runs on Unity 6+ and on .NET servers.
- Sheet rules: ignored sheets, columns and rows; `Table.Config`; `Table.Child` row sheets; `Table.Col.Group` column sheets;
  `Enums` sheets; `Mapping<>` columns; `Key<>`, `Index<>`, `Ref<>` and arrays.
- Arrays: elements are separated by `,` (`sheet.arraySeparator`), optionally in `[ ]`; quoted elements may hold commas.
  The build warns about a number shown with thousands separators (`1,000`) and about text still written with `|`.
- Integrity: every data file is hashed in a manifest and checked on load; the manifest can be signed.
- Loading: sequential by default, parallel on request, synchronous or asynchronous.
- Reloading: `Watch` keeps tables current while the game or server runs; unchanged tables are shared between snapshots.
- Tools: an editor window, a `ugs` command line with the same behaviour, and `ugs diff` for patch planning.
- CSV: a built-in RFC 4180 reader on every runtime; the Sep reader is selectable on .NET 8+ (the CLI and the Unity 7 CoreCLR editor). Both give identical results.
