# MasterSplinter — UI Changes

A SourceTree‑style Git client UI built on **WinUI 3 / Windows App SDK** (`net8.0-windows10.0.19041`).
This document lists every UI change made on top of the original empty `MasterSplinter.Entrypoint`
template (which started as a blank `MainWindow` with a `Mica` backdrop).

> **Update (Phase 1):** the UI is now backed by a **real, read-only Git backend** — the native C++ core
> (`MasterSplinter.Logic`) shells out to `git.exe` behind a flat C ABI, and the C# app parses it into the
> view models. The original sample data has been removed. See the **Phase 1** section at the bottom.

---

## 1. Files added / changed

### Added
| File | Purpose |
| --- | --- |
| `Models/Models.cs` | Data models: `CommitRow`, `GraphRow`/`GraphLine`/`GraphDot` (+ `GraphBuilder`), `Badge`, `ChangedFile`, `DiffLine`, and their enums. |
| `ViewModels/MainViewModel.cs` | Sample data: 18 commits with a real branch/merge graph, per‑commit changed files & diffs, and the sidebar tree. Holds `SelectedCommit` / `SelectedFile`. |
| `ViewModels/SidebarItemVM.cs` | Sidebar node (`SidebarKind`, expand/select/visible state). |
| `Infrastructure/ObservableObject.cs` | Tiny `INotifyPropertyChanged` base. |
| `Infrastructure/Converters.cs` | Value converters + a `Glyphs` table of Segoe Fluent code points (used as `char.ConvertFromUtf32` so source stays pure ASCII). |
| `Infrastructure/Selectors.cs` | `BadgeTemplateSelector`, `DiffLineTemplateSelector`, `SidebarTemplateSelector`. |
| `Infrastructure/GraphCanvas.cs` | Custom `Canvas` that draws one commit row's branch graph (lanes, dots, diagonal merges). |
| ~~`Infrastructure/GridSplitterLite.cs`~~ | _(removed)_ — replaced by the official `CommunityToolkit.WinUI.Controls.GridSplitter` (`...Controls.Sizers` package). The splitter columns/rows are 12px gutters; resized panes carry `MinWidth/MaxWidth` (or `MinHeight/MaxHeight`). |
| `Controls/ToolButton.xaml(.cs)` | Toolbar command button: icon‑over‑label, font‑glyph **or** vector icon, fades when disabled, forwards `Click`. |
| `Controls/RepositoryWorkspace.xaml(.cs)` | The whole repository workspace (toolbar, sidebar, history table, detail + diff, mode tabs). |
| `Themes/AppResources.xaml` | Light/Dark `ThemeDictionaries`, converters, vector git icons, shared styles. |

### Changed
| File | Change |
| --- | --- |
| `MainWindow.xaml(.cs)` | Replaced the empty window with a **custom WinUI 3 title bar** + `MenuBar` + `TabView` repository tab strip hosting the workspace. |
| `App.xaml` | Merged `Themes/AppResources.xaml` into application resources. |

---

## 2. Window shell

### Custom title bar (modern WinUI 3, not the default Win32 caption)
Implemented per the [Title‑bar customization docs](https://learn.microsoft.com/en-us/windows/apps/develop/title-bar):

- `ExtendsContentIntoTitleBar = true` (set in code — it errors in XAML) hides the system title bar.
- `SetTitleBar(TitleBarDrag)` defines the draggable region.
- `AppWindow.TitleBar.PreferredHeightOption = Tall` (48 px) so the caption buttons match the bar height.
- `LeftPaddingColumn` / `RightPaddingColumn` are sized from `AppWindow.TitleBar.Left/RightInset ÷ RasterizationScale` on `Loaded`/`SizeChanged`, so content never slides under the min/max/close buttons at any DPI.
- The bar holds: **blue branch logo + “MasterSplinter” title + the menu bar** + a draggable spacer.
- Caption‑button colors (transparent background, theme‑matched foreground/hover) are re‑applied on `RootGrid.ActualThemeChanged`.

### Top menu bar
`MenuBar` with **File, Edit, View, Repository, Actions, Tools, Help**. Wired items: *File ▸ New Tab*, *File ▸ Exit*, *View ▸ Toggle Light / Dark Theme*.

### Repository tab strip
`TabView` (`TabWidthMode=SizeToContent`) used as a **strip only** — tabs **sourcetreewin / tutorial‑repo / jquery‑ui‑carousel**, each with a folder icon and a close button; the **+** button adds a new tab. The `RepositoryWorkspace` lives in its **own grid row directly below** the strip, not inside the tab content.

> Why: WinUI `TabView` hosts its selected content in an internal presenter that sizes to content (top‑aligned) and ignores `VerticalContentAlignment`, so a `*`‑based workspace placed inside a tab does not fill the height — it left an empty band at the bottom in maximized/full‑screen. Hosting the workspace in a bounded `*` row of the window grid makes it fill correctly. The strip therefore selects the active repository while one shared workspace renders below it.

---

## 3. Repository workspace

### Command toolbar
Large icon‑over‑label buttons, left‑aligned primary group + right‑aligned utility group, separated by 1 px rules and a bottom divider.

- Primary: **Commit, Push, Pull, Fetch, Branch, Merge, Stash, Discard, Tag**
- Utility: **Git Flow, Terminal, Explorer, Settings, Theme**
- **Merge** and **Discard** are disabled (rendered faded at 0.4 opacity).
- Segoe Fluent Icons has **no git glyphs**, so **Branch / Merge / Stash** are drawn as vector `ControlTemplate`s (`BranchIconTemplate`, `MergeIconTemplate`, `StashIconTemplate`); the **Theme** button toggles light/dark.

### Left navigation sidebar (≈280 px, resizable)
Collapsible tree with uppercase section headers and selectable leaves:

- **FILE STATUS** → Working Copy
- **BRANCHES** → master *(default selected: gray fill + semibold)*
- **TAGS** → 0.10.7, 0.11.0, 0.11.1, 0.8.6, v0.6.4, v0.7.4
- **REMOTES** → origin → gh‑pages, HEAD, master, touch, v0.10

Headers/`origin` show a chevron and collapse their children; branch leaves use the vector branch icon, tags a tag glyph, remotes a globe.

### Filter row
`All Branches` combo · `Show Remote Branches` checkbox · `Date Order` combo · right‑aligned `Jump to:` + search box.

### Commit history table
5‑column table (`Graph · Description · Date · Author · Commit`) with a light‑gray header row and compact 26 px rows. Selected row uses a strong blue highlight (`#2563EB`) with white text.

- **Graph column** — the custom `GraphCanvas` draws blue/green/orange/red lanes, commit dots, and diagonal branch/merge lines, aligned row‑to‑row.
- **Description column** — inline rounded badges (`master`, `origin/master`, `origin/HEAD`, `origin/gh-pages`, tags…) with branch/tag icons and per‑kind colors (local / remote / tag / HEAD), followed by the commit message.

### Bottom area (resizable split: detail | diff)
- **Commit detail panel** — `Commit / Parents / Author / Date / Committer` metadata (hashes as blue link text), the full commit message, then the **CHANGED FILES** list with status icons (green +, amber ✎, red −) and blue selection.
- **Diff viewer** — header with file path + search + more/settings buttons; monospaced body with a line‑number gutter, soft‑green added lines, soft‑red removed lines, hunk headers with a **Reverse hunk** button. Read‑only.

### Bottom mode tabs (inside the changed‑files panel)
**File Status / Log · History / Search** are a **`SelectorBar`** docked at the **bottom of the commit‑detail panel**, directly below `FilesList` (row 4 of that panel's grid). They span only the detail‑panel width and sit to the left of the diff viewer; the bar uses a distinct `FooterBrush` background + a top divider to separate it from the file list above.

> `SelectorBar` (the purpose‑built WinUI segmented‑selection control) handles single‑selection and the animated accent selection indicator natively — replacing an earlier hand‑styled `RadioButton` group (~45 lines of custom `ControlTemplate`). Earlier iterations also placed these as a full‑width footer at the very bottom of the window; per request they now live inside the changed‑files panel group.

---

## 4. Theming

- Full **light/dark** support via `ThemeDictionaries` in `AppResources.xaml`; all panel/text/diff/badge colors flow through `{ThemeResource …}` keys.
- Toggle via **View ▸ Toggle Light / Dark Theme** or the toolbar **Theme** (sun) button — both flip `RootGrid.RequestedTheme`, which also re‑tints the title‑bar caption buttons.
- `Mica` backdrop on the window; selection blue and status colors are intentionally theme‑independent.

---

## 5. Notable WinUI 3 implementation notes

- Panes are resized with the official **`CommunityToolkit.WinUI.Controls.GridSplitter`** (Sizers package); give the splitter row/column an explicit width (e.g. 12px) so the gutter is grabbable, and put `MinWidth/MaxWidth` (or `MinHeight/MaxHeight`) on the resized definitions.
- `FontWeights` lives in `Microsoft.UI.Text`, `Colors` in `Microsoft.UI` (not the `Windows.UI.*` equivalents).
- Glyphs are emitted via `char.ConvertFromUtf32(code)` / XML entities (`&#xE713;`) to keep source ASCII‑safe.
- Sidebar taps are handled once on the host `ItemsControl` (via `OriginalSource`), avoiding event handlers inside `DataTemplate`s.

---

## 6. Build & run

Packaged WinUI app on an **ARM64** machine. Use **full VS MSBuild** (not `dotnet build`) — the C# app
ProjectReferences the native C++ `vcxproj`, whose targets the .NET SDK's msbuild lacks:

```powershell
& "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" `
  MasterSplinter.Entrypoint\MasterSplinter.Entrypoint.csproj -restore -t:Build -p:Configuration=Debug -p:Platform=ARM64
Add-AppxPackage -Register "MasterSplinter.Entrypoint\bin\ARM64\Debug\net8.0-windows10.0.19041.0\win-arm64\AppX\AppxManifest.xml" -ForceUpdateFromAnyVersion
Start-Process "shell:AppsFolder\e0495604-e15e-4723-9b06-1d9e9d7c5cbf_3z1m0dck14mey!App"
```

Or simply open the solution in Visual Studio and press **F5** (platform **ARM64**). If a code change
doesn't seem to take effect from the command line, the MSIX loose layout went stale — delete `bin`/`obj`
and rebuild clean.

---

## 7. Phase 1 — read-only Git backend

The UI is now backed by **real git data**. The native C++ core shells out to `git.exe` (mirroring how
TortoiseGit reads data), exposed via a flat C ABI and consumed from C# by P/Invoke.

| Layer | File(s) | Role |
| --- | --- | --- |
| Native git backend | `MasterSplinter.Logic/GitBackend.cpp`, `MasterSplinter.Logic.h` | `RunGit` (CreateProcessW) + `MsGit*` C-ABI funcs that build git commands and return delimited UTF-8. |
| P/Invoke | `Interop/NativeLogic.cs` | Bindings for `MsGit*`; copy-then-free of returned `char*`. |
| Git service | `Git/GitRepository.cs` | Parses delimited streams into models (`Open`/`Log`/`ListRefs`/`ChangedFiles`/`Diff`/`FileAt`). |
| Recents | `Git/RecentRepositoriesStore.cs` | Persists recent repos as JSON in local app data. |
| View model | `ViewModels/MainViewModel.cs` | Async loading, selection-driven file/diff, search filter, order, recents. |

**Features (acceptance criteria met):** open a folder with validation + clear error for non-repos
(CORE-001); recent repositories on a home screen (CORE-002); repo name + branch header (CORE-003);
real commit history with graph + ref badges (LOG-001); commit selection → detail (LOG-002); full
hash/parents/author/committer/body (LOG-003); changed files with status icons + real unified diff
(LOG-004); live message/author/SHA search (LOG-005); copy short/full SHA (LOG-006); open a file's
content at a commit (LOG-007). The commit graph is intentionally simple (dot + single lane) for now.

**FFI note:** returned `char*` buffers must not contain embedded NUL bytes — `Marshal.PtrToStringUTF8`
truncates at the first NUL. The backend uses newline/tab/`0x1F`/`0x1E` separators, never `git -z`.
