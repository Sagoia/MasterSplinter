# MasterSplinter — UI

A SourceTree-style Git client UI on **WinUI 3 / Windows App SDK** (`net8.0-windows10.0.19041`), built up
from the empty template. Backed by the real read-only Git backend (native C++ core → C# parsing → MVVM) —
see [README](README.md) for the backend and architecture.

## Shell — `MainWindow.xaml`
- **Custom title bar**: `ExtendsContentIntoTitleBar` + `SetTitleBar` (set in code), `PreferredHeightOption.Tall`
  (48px); left/right padding columns sized from `AppWindow.TitleBar` insets ÷ rasterization scale; caption-button
  colors re-tinted per theme. Holds app logo + title + `MenuBar` (File/Edit/View/Repository/Actions/Tools/Help).
- **Repository tab strip** (`TabView`) used as a *strip only* — the shared `RepositoryWorkspace` sits in its own
  grid row **below** it, not inside the tab. (WinUI `TabView` sizes tab content to content and ignores
  `VerticalContentAlignment`, so a `*`-layout workspace inside a tab won't fill height.)

## Workspace — `Controls/RepositoryWorkspace.xaml`
- **Toolbar** of icon-over-label `ToolButton`s (Commit/Push/Pull/Fetch/Branch/Merge/Stash/…). Segoe Fluent has no
  git glyphs, so Branch/Merge/Stash are vector `ControlTemplate`s.
- **Sidebar** — collapsible tree: FILE STATUS / BRANCHES / TAGS / REMOTES (grouped `origin/…`).
- **History table** — 5 columns (Graph / Description / Date / Author / Commit), 26px rows; `GraphCanvas` draws the
  lane/dot/merge graph; Description shows inline ref badges + the commit message.
- **Detail + diff** (resizable split) — commit metadata + changed files (status icons), and the diff viewer
  (unified/side-by-side, syntax highlighting, whitespace options, binary/image). Mode tabs are a `SelectorBar`
  docked under the file list; **File Status ↔ Log / History** switches the panel between the working-copy
  status view and the selected commit's files (selecting a commit also returns to history).
- **Working copy (Phase 3)** — entered from the sidebar "Working Copy" node or the File Status tab. One
  grouped `ListView` (`CollectionViewSource IsSourceGrouped`, `GroupStyle.HidesIfEmpty`; the group class *is*
  an `ObservableCollection<ChangedFile>`, and its `Source` must be set in code-behind) shows
  **Staged / Unstaged / Untracked** sections with counts; renames render `old → new`; a file staged and
  modified again appears in both sections. Selection feeds the same diff panel (staged = index↔HEAD,
  unstaged = worktree↔index, untracked = synthesized all-added via `--no-index`). Context menu:
  Open in External Editor (command template with `{path}` under Tools ▸ Options…, blank = shell default —
  launched with `UseShellExecute=true`, since bare `CreateProcess` dies on App-Execution-Alias exes like
  notepad), Reveal in Explorer (path must be backslash-normalized or `/select` silently ignores it), Copy Path.
- **Refresh** — toolbar button, View ▸ Refresh, F5 / Ctrl+R (accelerators duplicated on the workspace root:
  `MenuFlyoutItem` accelerators are unreliable in WinUI 3 while the flyout is closed). A `FileSystemWatcher`
  (500 ms debounce on a `DispatcherQueueTimer`, `.git` noise filtered to index/HEAD/refs) auto-refreshes:
  worktree edits reload status, `.git` changes reload branch + log + status; selection is restored by
  (area, path) since every refresh rebuilds the item objects.
- Panes resize via CommunityToolkit `GridSplitter` (12px gutters + Min/Max on the resized definitions).

## Theming — `Themes/AppResources.xaml`
- Light/Dark `ThemeDictionaries`; panel/text/diff/badge colors via `{ThemeResource …}`. Toggle from the View menu
  or the toolbar sun button (flips `RootGrid.RequestedTheme`, re-tints caption buttons). `Mica` backdrop; selection
  blue is intentionally theme-independent.

## Notes
- `FontWeights` lives in `Microsoft.UI.Text`, `Colors` in `Microsoft.UI`; glyphs via `char.ConvertFromUtf32` / XML
  entities to keep source ASCII. Sidebar taps are handled once on the host `ItemsControl` (via `OriginalSource`),
  not inside `DataTemplate`s.
