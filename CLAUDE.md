# HolySheet

## The Unity window and the desktop app are one tool

The Unity editor window (`src/Editor/HolySheetWindow.cs`, `HolySheetWindow.uss`) and the desktop app (`desktop/`) are
two shells over the same engine. Someone who knows one must already know the other: the same parts in the same
places, the same words, the same colors for the same meaning, the same result from the same click.

**A change to the UI of one is a change to both.** This holds for layout, wording, colors, icons, defaults and
behavior, however small the change.

### When you change either UI

1. Find the counterpart in the table under [Where each part lives](#where-each-part-lives).
2. Make the same change there, in the same commit.
3. If the change cannot apply to the other shell (it needs Unity, or it needs the operating system), add a line to
   "Differences from the Unity window" in `desktop/README.md` that says what differs and why.
   A difference that is not listed there is a bug.
4. Run the [checks](#checks).

Do not finish a UI task with only one shell changed and the other left for later.

### What must match

**Structure and order**

- Pages: `Tables`, then `Settings`.
- The toolbar shows page tabs, status, `Check`, `Build all`, then `Build selection (n)`.
  The desktop app adds the project name and its menu at the far left, and nothing else.
- A single Google tab has local copy controls in its tree context menu and shows its last download time while pinned.
- Tables page: the tree with its search on the left and the selection line under it; on the right the caption with
  the `View` menu, the find bar, the grid; the problems bar and panel under the grid.
- Settings sections, in this order and open or closed by default as here: `Sources` (open), `Output` (open),
  `Sheet and build options` (closed), `Signing (…)` (closed), then the shell's own section, open: `Editor` in Unity,
  `App` on the desktop.
- Fields inside a section keep the same order and the same labels.

**Words**

- The English text is the key in both shells, so the same thing must have exactly the same English text: labels,
  tooltips, empty states, menu items, the status line (`Built · 21 tables`, `Failed · 3 errors`).
- A new text goes into `src/Editor/HolySheetText.cs` with its Korean, then `npm run i18n` in `desktop/` exports it to
  `desktop/src/i18n/ko.unity.json`. Never edit `ko.unity.json` by hand.
- `desktop/src/i18n/ko.app.json` is only for texts the Unity window does not have (opening a folder, the theme).
  A text both shells show does not belong there.
- Engine messages (problems, hints, the run summary) stay in English in both.

**Behavior**

- What a control does, when it is enabled, what a selection means for a build, how the tree is grouped, how a column
  sorts, what a click or double click on a node or a problem opens.
- Defaults of the view options (grid lines, alignment, striped rows) and what each option is called.
- Anything that decides something about sheets, tables or builds belongs in the engine (`src/Editor/Core`,
  `dotnet/HolySheet.Tooling`), where both shells get it. Do not write such a rule in one UI.
- Where a shell has to hold the logic itself, the desktop file says in its first comment which window method it
  follows (`lib/tree.ts` → `FillTree`, `lib/sort.ts` → `SortRows` / `CompareNatural`, `lib/config.ts` → the Settings
  rules). Change both, and keep that comment true.

**Colors that carry meaning**

Column type colors are the same values in both shells, in both themes. Change them in both files together.

| Type | Dark | Light |
|---|---|---|
| int | `#8db7e6` | `#2c5d9b` |
| float, double | `#7fc8b0` | `#1d7660` |
| bool | `#e3a46a` | `#a0560f` |
| DateTime | `#c3a1e0` | `#7243a0` |
| enum | `#d8c578` | `#85700f` |
| Ref | `#e597b6` | `#a13a66` |

Mapping columns are italic, not colored. The color goes on the type under each heading; cells keep the default text
color. Color appears only where it carries meaning: problems, column types, the running state.

**Look**

- The Unity window uses Unity's own controls and editor colors, so it sits next to the Inspector. The desktop app's
  dark theme stays in the range of Unity's dark editor (`#323232`–`#3e3e3e` surfaces, `#242424`–`#2a2a2a` lines).
- An editor tool, not a web page: flat bars, 3px corners (only a floating menu is rounder), dense rows (20px tree
  rows, 22–24px section bars), 11px for secondary text, no gradients, no decorative animation, shadows only under
  what floats over the window (menus, popups, notices).
- A Settings section is a full-width bar that folds, like an Inspector component header; the fields under it stop
  at a readable width. A source is a card: icon, kind, a one-line summary, a remove button.
- What a field is for is its tooltip in both shells, not a line of help text under it.
- Icons: the same thing has the same icon idea in both (an Excel workbook, a Google sheet, a folder, a table, an
  enum, error, warning). Unity uses its built-in editor icons; the desktop app draws its own on a 16px grid in
  `components/Icons.tsx`.

### Where each part lives

| Part | Unity window | Desktop app |
|---|---|---|
| Styles and palettes | `src/Editor/HolySheetWindow.uss` | `desktop/src/styles.css` |
| Texts and Korean | `src/Editor/HolySheetText.cs` | `src/i18n/ko.unity.json` (exported), `ko.app.json` |
| Toolbar and status | `BuildToolbar`, `RefreshToolbar` | `components/Toolbar.tsx` |
| Tree | `FillTree`, `MakeTreeRow`, `ApplyTreeSearch` | `lib/tree.ts`, `components/TableTree.tsx` |
| Selection build | `UpdateTreeSelection`, `DescribeSelection`, `RunSelection` | `selection.ts` |
| Data view | `ShowPreview`, `ShowEnumPreview`, `Header`, `SetViewStyle` | `components/Preview.tsx`, `DataGrid.tsx` |
| Sorting | `SortRows`, `CompareNatural` | `lib/sort.ts` |
| Problems | `BuildProblems`, `BuildProblemsBar`, `BindProblemRow` | `components/Problems.tsx` |
| Settings | `BuildSettings`, `BuildSource`, `BuildSigning`, `Section` | `components/SettingsPage.tsx`, `lib/config.ts` |
| Shell-only section | `BuildEditorSettings` | the `App` section of `SettingsPage.tsx` |

Paths in the desktop column are under `desktop/src/` unless they start with `src/Editor`.

### Checks

```bash
cd desktop
npm run i18n      # then `git diff src/i18n/ko.unity.json` must show only what you meant to change
npm test          # fails when a T("…") or F("…") text has no Korean; also the tree, sort and selection rules
```

Unity side: the editor tests in `unity/HolySheet.Dev/Assets/Tests/Editor/EditorTests.cs`.

Then look at both shells with the same project open and compare the part you changed, in English and in Korean,
in the dark and the light theme.
