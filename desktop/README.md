# HolySheet desktop

The HolySheet table tool as a desktop app for macOS and Windows: open a folder with a `holysheet.json`, look at the tables as read,
read the problems, build, and edit the config. It is the Unity window without Unity, and it builds exactly what the
window and the `holysheet` command line build from the same `holysheet.json`. All three run the same engine code.

```
desktop/
  src/                 React + TypeScript UI (Vite)
  src-tauri/           Tauri 2 shell (Rust): starts the engine and relays calls to it
  scripts/build-host.mjs   publishes the engine as the sidecar binary
dotnet/HolySheet.Desktop.Host/   the engine process (holysheet-host): JSON-RPC over stdin/stdout, on HolySheet.Core
dotnet/HolySheet.Tooling/        verify, key pair, init, run summary: shared tool tasks
```

## How it fits together

```
React ── invoke("rpc", {method, params}) ──▶ Rust (sidecar.rs) ── one JSON line on stdin ──▶ holysheet-host (.NET)
      ◀── result / "holysheet://event" ─────           ◀── one JSON line on stdout ──────────
```

- The web view can only call the `rpc` command. It has no shell access; only Rust starts and talks to the engine.
- The engine is published self-contained, so the app needs no .NET installed. It is started on first use, started
  again if it dies (the UI re-opens the project), and stopped when the app quits.
- The methods are in `dotnet/HolySheet.Desktop.Host/RpcServer.cs` (`DispatchAsync`) and typed in `src/rpc.ts`.
  The engine also sends `run.started` / `run.finished` notifications for every build, including those that auto build
  starts.

## Develop

Needs Node 20+, Rust (stable) and the .NET 10 SDK. The sidecar script finds the SDK in `$DOTNET`, then
`/usr/local/share/dotnet/dotnet`, then `PATH`.

```bash
cd desktop
npm install
npm run tauri dev                          # publishes the engine, starts Vite and the app
npm run tauri dev -- -- path/to/holysheet.json  # opens that project (a folder works too)
npm test                                        # sorting, tree and selection rules, translations
npm run tauri build                             # installers under src-tauri/target/release/bundle
```

Engine tests are with the rest of the .NET tests: `dotnet test dotnet/HolySheet.Tests --filter DesktopHostTests`.

## Translations

Texts are written in English and looked up by that text, like the Unity window's `HolySheetText.cs`, so both tools share
one set of Korean translations:

- `src/i18n/ko.unity.json` is exported from `src/Editor/HolySheetText.cs`. Run `npm run i18n` after changing it.
- `src/i18n/ko.app.json` holds what only the desktop app says.
- `npm test` fails when a `T("…")` or `F("…")` text has no Korean translation.

Engine messages (problems, hints, the run summary) stay in English, as in Unity.

## Differences from the Unity window

- **Custom credential providers.** These are C# classes in a Unity project, so they run only in the Unity editor.
  In the desktop app, use the `command` auth type, or build offline from a snapshot.
- **No serializer warning.** The Unity window warns when MemoryPack or MessagePack is missing from the project.
  The desktop app has no project assemblies to check.
- **Other config files.** The app can open a config file with another name, such as `holysheet.memorypack.json`.
- **Theme.** Dark by default, in the range of Unity's dark editor; Settings → App → Theme switches to light or to
  the system's. What a field is for is its tooltip, as in the Unity window.
- **Auto build can poll Google.** Settings → App → Auto build interval re-reads Google sources on a timer, as
  `holysheet watch --interval` does. 0 means only local files trigger a build.

## Not done yet

- Code signing and notarization (Apple Developer ID, a Windows certificate). The CI builds are unsigned.
- App icons are the Tauri defaults.
