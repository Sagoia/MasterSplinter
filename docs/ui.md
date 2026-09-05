# UI

A SourceTree-style Git client on **WinUI 3 / Windows App SDK** (`net8.0-windows10.0.19041`), built up from
the empty template. Backed by the native C++ core — see **[architecture.md](architecture.md)**.

## Shell — `MainWindow.xaml(.cs)`

- **Custom title bar**: `ExtendsContentIntoTitleBar` + `SetTitleBar` (set in code, not XAML),
  `PreferredHeightOption.Tall` (48 px). Left/right padding columns are sized from `AppWindow.TitleBar` insets
  ÷ rasterization scale on `Loaded`/`SizeChanged`; caption-button colors are re-tinted per theme on
  `ActualThemeChanged`. Holds app logo + title + `MenuBar` (File/Edit/View/Repository/Actions/Tools/Help).
- **Repository tab strip** (`TabView`) used as a *strip only* — the shared `RepositoryWorkspace` sits in its
  own grid row **below** it, not inside the tab. WinUI's `TabView` hosts content in an internal presenter
  that sizes to content and ignores `VerticalContentAlignment`, so a `*`-layout workspace inside a tab won't
  fill height (it leaves an empty band when maximized).
- `Mica` backdrop. The window owns the folder picker.

## Workspace — `Controls/RepositoryWorkspace.xaml(.cs)`

**Toolbar** of icon-over-label `ToolButton`s (Commit/Push/Pull/Fetch/Branch/Merge/Stash/…). Segoe Fluent has
no git glyphs, so Branch/Merge/Stash are vector `ControlTemplate`s in `Themes/AppResources.xaml`.

**Sidebar** — collapsible tree: FILE STATUS / BRANCHES / TAGS / REMOTES (grouped `origin/…`) / STASHES. Taps
are handled once on the host `ItemsControl` via `OriginalSource`, not inside `DataTemplate`s.

**History table** — a `ListView` with a hand-rolled 5-column grid (Graph 150 / Description * / Date 140 /
Author 180 / Commit 96) and a separate header `Border` duplicating the widths. Rows are a fixed **26 px**
(`CommitItemStyle`), which is what lets graph lanes line up across rows. `SelectionMode="Extended"` for
multi-select cherry-pick; `Commits_SelectionChanged` takes `e.AddedItems[^1]` so the detail pane follows the
last-clicked row, not the far end of a shift-select. Capped at `MaxCommits = 2000`, no paging.

> The graph column is currently a **placeholder** — one blue lane per row. See **[graph.md](graph.md)**.

**Detail + diff** (resizable split) — commit metadata + changed files (status icons), and the diff viewer.
Mode tabs are a `SelectorBar` docked under the file list: **File Status ↔ Log / History**, plus the search
and reflog modes. Selecting a commit returns to history. Panes resize via CommunityToolkit `GridSplitter`
(12 px gutters + Min/Max on the resized definitions).

## Feature surfaces

**Diff viewer** — unified / side-by-side toggle, ColorCode syntax highlighting, whitespace options
(show all / ignore space change / ignore all), binary card with before/after image preview, and compare mode
(mark a commit, then compare against another commit or a ref) with an ahead/behind chip.

- Side-by-side rows are built by `Infrastructure/SideBySideBuilder`; one `DiffCellTemplateSelector` serves
  both columns.
- Syntax highlighting is a `Syntax.Code`/`Syntax.Lang` attached property rendering into a `TextBlock`'s
  `Inlines` with `RichTextBlockFormatter(ElementTheme)`, re-rendering on `ActualThemeChanged`.
- **Gotcha:** `Button.Flyout` does *not* trigger on the styled icon button (`IconGhostButtonStyle`) — the
  click registers but the popup never opens, while right-click `MenuFlyout`s work fine. Use
  `FlyoutBase.AttachedFlyout` + a `Click` handler calling `FlyoutBase.ShowAttachedFlyout(...)`.

**Working copy** — entered from the sidebar "Working Copy" node or the File Status tab. One grouped
`ListView` (`CollectionViewSource IsSourceGrouped`, `GroupStyle.HidesIfEmpty`; the group class *is* an
`ObservableCollection<ChangedFile>` and its `Source` must be set in code-behind) showing
**Conflicted / Staged / Unstaged / Untracked** with counts. Renames render `old → new`; a file staged and
modified again appears in both sections. Selection feeds the same diff panel (staged = index↔HEAD,
unstaged = worktree↔index, untracked = synthesized all-added via `--no-index`).

**Staging & commit** — double-click a row toggles stage/unstage. Discarding an untracked file is a
`File.Delete` with distinct "permanently delete" wording. The commit editor sits under the file list;
`CanCommit` requires a non-blank subject, something staged (or amend), no load in flight, and **no remaining
conflict**. The amend checkbox pre-fills subject/body from HEAD only when the editor is empty.

**Branches & tags** — create/rename/delete branches, create/delete tags, checkout (branch or detached
commit). `branch -d` is always tried before offering `-D`, and the safe attempt's error text feeds the
"Delete anyway?" dialog rather than the InfoBar.

**Remotes** — edit fetch/push URLs, fetch (all / prune / tags), pull (`--ff-only`), push (`-u` publishes,
optional tags). The header carries an upstream track chip, which costs no extra git call — `ListRefs` already
parses `%(upstream:track,nobracket)` per branch.

**Merge / rebase / cherry-pick / revert** — dialogs for each, plus continue/skip/abort. A repository-state
banner shows what is half-finished and how far along (`state`, branch or short sha, rebase step/total). A
conflict arrives through the ERR channel but is a **normal outcome**: the view refreshes anyway and the
progress dialog is retitled "— conflicts" rather than "— failed". The commit editor is pre-filled from
`MERGE_MSG` for merge/cherry-pick/revert (not rebase, whose commits are finished by `--continue`), and the
prefill is **taken back out** when the operation ends — otherwise the next unrelated commit inherits
"Merge branch 'x'". Conflicted files get a Resolve → external mergetool path (`git mergetool --no-prompt`).

**Stash / blame / search / reflog** — save/apply/pop/drop stashes from the sidebar; blame opens in a
secondary window; commit search runs as a real git query with modes Message / Author / Content / Path / Hash
(one predicate each) plus match-case, regex, all-branches and a path filter; the reflog replaces the commit
list in place with a banner to exit.

**`GitProgressDialog`** — streams output for every long-running command with a determinate progress bar and a
Cancel button. The bar's parser is modelled on TortoiseGit's `CProgressDlg::ParserCmdOutput`: a line holding
both a `:` and a `%` is a progress line, the label is the text before the colon, the value is the digit run
ending at the `%`, and the bar only moves when the value is `> 0`. Two deliberate deviations — find the colon
**before** the `%` (a plain reverse-find folds a `1.2 MiB | 500 KiB/s` tail into the label), and start
**indeterminate** until the first percentage arrives (a flat determinate bar during a silent SSH handshake
reads as hung).

**Refresh** — toolbar button, View ▸ Refresh, F5 / Ctrl+R. Accelerators are duplicated on the workspace root
because `MenuFlyoutItem` accelerators are unreliable in WinUI 3 while the flyout is closed. A
`FileSystemWatcher` auto-refreshes (details in **[architecture.md](architecture.md)**); selection is restored
by (area, path) since every refresh rebuilds the item objects.

## View model layout

`MainViewModel` is one `partial` class across nine files, split the same way `GitBackend` was on the
native side — by area, with no delegation layer, so the compiled type and its API are unchanged.

| File | Holds |
|---|---|
| `MainViewModel.cs` | Shared state, selection, and the **cross-cutting git-operation plumbing**: `RunGitOperationAsync`, the `IGitOperationHost` implementation, the watcher-suppression window, `RaiseCommandState`. |
| `.Loading.cs` | Opening, refreshing, lazy per-commit loads. **Where the commit graph plugs in.** |
| `.History.cs` | The in-memory filter, git-backed search, reflog mode. |
| `.Diff.cs` | Unified vs side-by-side, whitespace, compare mode, binary previews. |
| `.WorkingCopy.cs` | Status groups, staging, discard, the commit editor. |
| `.Refs.cs` | Branch, tag and stash mutations. |
| `.Remotes.cs` | Remotes and the three network commands. |
| `.Sequencer.cs` | Merge, rebase, cherry-pick, revert + continue/skip/abort. |
| `.Sidebar.cs` | Selection and taps; the tree itself is built in Core by `SidebarBuilder`. |

Anything used by more than one area lives in `MainViewModel.cs`. That is the rule that keeps the
split honest: the refresh policy is shared by all four mutation areas, so it belongs to none of them.

## Dialogs

Two kinds, deliberately handled differently.

**Confirmations** go through `Confirmations` in `MasterSplinter.Core`, which returns a `Confirmation`
record - title, message, primary-button text, and **which button is default**. `ConfirmAsync` only
renders it. The wording is a product promise (whether discarding an untracked file says "permanently
delete"; whether git's own refusal is quoted back), so it lives where tests can pin it.

> **Destructive confirmations default to Cancel**, so a reflexive Return cannot discard work. The
> exception is a dialog that is a heads-up rather than a guard - switching branches defaults to
> Switch, because git itself refuses when the switch is unsafe.

**Input dialogs** use `Controls/DialogBuilder.cs`, which stays in the app project: unlike the wording,
an input form is genuinely UI, so what is worth removing is the boilerplate, not the decisions. Fluent
field methods (`Combo`, `ComboIndex`, `TextBox`, `MultilineTextBox`, `Check`, `Note`, `Paragraph`,
`Add`) return a typed `Field<T>` read back after `ShowAsync`, plus behaviour helpers:

| Helper | Why it exists |
|---|---|
| `EnablePrimaryWhen` | Branch/tag names cannot be blank. Scoped to its own field, so an optional message box does not re-validate the required one. |
| `FocusOnOpen(selectAll:)` | Rename pre-fills the current name; typing should replace it. |
| `DisableWhile` | `--all` and a remote name are mutually exclusive to git. |
| `DefaultToCancel` | Rebase and revert rewrite history, so Return must not trigger them. |
| `ValidateOnSubmit` | Validates on the way out, showing the reason under the field - a greyed-out Save says nothing about *why*. |
| `Note` vs `Paragraph` | `Note` is muted and small; `Paragraph` is full weight. A warning meant to be read must not be quietly de-emphasised. |

Four `ContentDialog` sites remain and are not candidates: `ShowMessageAsync` and `ConfirmAsync` *are*
the abstraction, `ShowFileAtCommit` is a content viewer, and the remotes list builds per-remote blocks
with inline links that reopen it.

## Theming — `Themes/AppResources.xaml`

Light/Dark `ThemeDictionaries`; panel/text/diff/badge colors via `{ThemeResource …}`. Toggle from the View
menu or the toolbar sun button (flips `RootGrid.RequestedTheme` and re-tints caption buttons). Converters
declared there return **theme-independent** brushes only — anything theme-dependent goes through a
`ThemeResource` key. Selection blue and the graph lane colors are intentionally theme-independent.

## WinUI traps worth not re-deriving

- **`TextBox.Text` reports line breaks as a bare `\r`** — not `\n`, not `\r\n`. Passed straight to git, a
  two-line commit or tag message becomes one line with an embedded CR. Everything sourced from a `TextBox`
  must go through `GitRepository.NormalizeMessage`.
- **Git draws progress with a bare CR** (`Receiving objects: 45%…\r`), so the progress log must treat a lone
  CR as "overwrite the current line" and CRLF as one break — with a pending-CR flag, because chunk
  boundaries fall between them. (Same byte as the `TextBox` trap, opposite direction.)
- **The XAML type generator emits setter-based providers** for every type reachable from an `x:Class` type's
  *public* members, so a positional `record` (init-only accessors) fails to compile with **CS8852**. Both
  `x:Bind` and a plain public `ObservableCollection<T>` property trigger it. Keep the collection private and
  assign `ItemsSource` in code.
- **`RootGrid.RequestedTheme` does not reach another window.** The theme must be passed into a secondary
  window's constructor and applied to its root.
- **`x:Name` inside a `DataTemplate` is not reachable from code-behind.** The dynamic "Cherry-pick N
  Commits…" label is found by walking `MenuFlyout.Items` for a `Tag`.
- **`explorer.exe /select,"path"` silently opens the default folder if the path contains forward slashes.**
  `rev-parse --show-toplevel` returns forward slashes → normalize with `Path.GetFullPath` first.
- **Launching an App-Execution-Alias exe (e.g. `notepad.exe`) from the MSIX app with
  `UseShellExecute=false` spawns a windowless orphan.** `EditorLauncher` uses `true`.
- `FontWeights` lives in `Microsoft.UI.Text`, `Colors` in `Microsoft.UI`; glyphs go through
  `char.ConvertFromUtf32` / XML entities to keep source ASCII.
- The app is **multi-instance** — activating it again launches a new instance rather than focusing the old
  one.

## Known debt

`RepositoryWorkspace.xaml.cs` is now nine `partial` files by area (137 lines in the core one,
from 2171), named to match `MainViewModel.<Area>.cs` and `GitRepository.<Area>.cs`. The domain
rules have moved to Core (`RemoteUrl`, `WorkingTreeWarning`, `Confirmations`) and dialogs go
through `DialogBuilder`.

What is left needs an architecture change rather than an extraction: ~60 `Click` handlers with
**no `ICommand` anywhere**, which cannot become `[RelayCommand]`s until an `IDialogService`
exists — most are "show a dialog, gather input, call the VM", and the dialog needs a `XamlRoot`
the view model does not have. `RepositoryWorkspace.xaml` (1094 lines) still uses 142 classic
`{Binding}` expressions and no `x:Bind`. See the refactor plan.
