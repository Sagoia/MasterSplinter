# Architecture

```
git.exe → C++ core (flat C ABI) → P/Invoke → C# parsing → MVVM → WinUI 3 UI
```

Git data comes from **shelling out to `git.exe` and parsing its output** (TortoiseGit's model, no library
binding), so behavior is predictable and command-building lives in one place. The core is portable C++;
each OS keeps its own UI. Windows ships today, macOS is the next target.

| Layer | Where | Role |
|---|---|---|
| Native core | `MasterSplinter.Logic` (C++20 DLL) | Builds git commands behind `extern "C"` `MsGit*`/`MsLogic*`. Process execution is abstracted per OS. |
| Interop | `MasterSplinter.Core` | `NativeLogic` is the only P/Invoke site and is `internal`; the app sees only the `NativeCore` lifecycle facade. Marshals UTF-8, frees native strings via `MsGitFree`. |
| Git service | `MasterSplinter.Core` | Parses the delimited streams into models. Instance per open repo; replaced wholesale on refresh. Split by area into `GitRepository.<Area>.cs`, **mirroring the native `GitBackend.<Area>.cpp` files** so both sides of one ABI area sit under the same name. |
| View models | `ViewModels/` | Selection, async loading, search. Every native call runs on `Task.Run`. |
| UI | `Controls/`, `MainWindow.xaml`, `Themes/` | WinUI 3 shell, history + graph, diff panels, light/dark. |

## C++ core design

The core splits *what git command to run* (portable) from *how to run a process on this OS* (per-platform),
using four GoF patterns. The same sources target Windows and macOS; any other platform is a compile error.

```
GitApi.cpp → GitBackend (Bridge: builds git args) → IProcessRunner (Bridge/Adapter seam)
                                                      ├── WindowsProcessRunner  (Win32 + C++/WinRT + WIL)
                                                      └── MacProcessRunner      (Foundation NSTask + POSIX)
             chosen by IPlatformFactory / CreatePlatformFactory()  (Abstract Factory + Factory Method)
```

| Pattern | Role |
|---|---|
| Bridge | `GitBackend` delegates process launch to `IProcessRunner` — command-building and OS execution vary independently. |
| Adapter | `WindowsProcessRunner` / `MacProcessRunner` wrap the OS API behind `IProcessRunner`. |
| Abstract Factory | `IPlatformFactory` builds the platform's services. |
| Factory Method | `CreateProcessRunner()`; `CreatePlatformFactory()` picks per OS (`_WIN32` / `__APPLE__`), in exactly one file. |

Adapters are the OS seam and use the **full, mixed** platform API — Windows mixes classic Win32
(`CreateProcessW`) with C++/WinRT (`winrt::to_hstring`) and WIL (RAII handles); macOS mixes Foundation
(`NSTask`) with POSIX (signal-aware exit codes: `128 + signal`). `GitBackend` is pure logic — no OS calls,
no `<windows.h>`, no `std::wstring` — which is what makes it unit-testable in isolation.

`GitBackend` is **stateless with respect to any repository**: every method takes `root`, every method is
`const`, and the only member is the runner. The core holds no cache and no per-repo state.

Its implementation is split by area across sibling files — `GitBackend.cpp` (process plumbing + the
terminal verbs) plus `GitBackend.History/Diff/WorkTree/Refs/Remotes/Sequencer/Stash/Blame.cpp`. They are all
the same class: because it is stateless, the split needed no delegation layer and left the public API and the
C ABI byte-identical.

Two pieces do the repetitive work:

- **`GitArgs`** (`Git/GitArgs.h`) builds the argument list — `QuotePathOff()`,
  `Whitespace(mode)`, `Path()/Paths()`, `AddIf()`. It deliberately does not reorder or validate: the argv it
  produces is the contract the unit tests assert, so it has to be predictable rather than clever.
- **Terminal verbs** apply one of the two return conventions, so no method restates the `int code;` dance:
  `RunRaw` (exit code ignored — for the reads where a non-zero exit is normal, like `diff --no-index`),
  `RunRead` (output, or `""` when git failed), `RunValue` (trimmed single value + an `ok` flag), and
  `RunWrite` (`OK` / `ERR`<US>message).

## Process execution contract

`IProcessRunner::Run(exe, args, const RunOptions&, out, exitCode)` is the single virtual; the shorter
overloads are non-virtual conveniences, so adding a knob never changes the vtable. `RunOptions` carries:

- **`input`** — stdin payload; `nullopt` wires stdin to the null device. Implementations must write stdin
  **concurrently** with draining stdout (sequential write-then-drain deadlocks when a pipe buffer fills).
- **`env`** — name/value overrides; empty means inherit.
- **`onOutput`** — a sink called with each chunk of merged stdout/stderr **and with `(nullptr, 0)` as a
  250 ms heartbeat**. Returning `false` cancels. The heartbeat is the point: a stalled SSH handshake prints
  nothing, so a cancel riding only on output would never fire.

Output is merged stdout+stderr, raw UTF-8, binary-safe. `Run` returns `false` **only** when the process
could not be started.

**Cancellation needs a job object, not `TerminateProcess`.** `git fetch` over HTTP spawns `git-remote-http`,
which inherits the stdout pipe — killing only the parent leaves the grandchild holding the write end and the
drain loop keeps blocking. Measured: 21.5 s vs **1.3 s**. `WindowsProcessRunner` creates a job with
`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`, spawns `CREATE_SUSPENDED`, assigns, resumes, and cancels with
`TerminateJobObject`. Job creation failing is non-fatal (falls back to `TerminateProcess`).
**`MacProcessRunner` has no equivalent yet** — `[task terminate]` alone; `setpgid`/`killpg` is the fix when
the macOS build exists.

## Wire format

Delimited UTF-8: fields `0x1F` (US), records `0x1E` (RS), and the payload must be **NUL-free** — the managed
marshaller stops at the first NUL. `git status -z` is NUL-separated, so the core translates each NUL to
`0x1E` before returning it. The one exception is raw binary (image previews): bytes plus an explicit length.

`std::filesystem::path` built from a plain `std::string` is read in the ANSI code page on Windows. Git hands
us UTF-8, so paths are built from `std::u8string` — otherwise a repo under a non-ASCII path silently looks
empty.

Full export list and the three return conventions: **[abi.md](abi.md)**.

## Threading & refresh

Every git call is synchronous and may be slow, so the view models wrap each in `Task.Run`. Long-running
commands (fetch/pull/push, merge/rebase/cherry-pick/revert, mergetool) stream through the progress sink and
are cancellable; everything else is not.

`RepositoryWatcher` is a `FileSystemWatcher` with a **500 ms debounce** on a `DispatcherQueueTimer`. It drops
`*.lock` and everything under `.git/` except an allowlist (`index`, `HEAD`, `packed-refs`, `MERGE_HEAD`,
`MERGE_MSG`, `CHERRY_PICK_HEAD`, `REVERT_HEAD`, `REBASE_HEAD`, `ORIG_HEAD`, `rebase-merge/*`,
`rebase-apply/*`, `refs/*`); anything else counts as a worktree change. Buffer overflow assumes everything
changed.

**`status` and worktree `diff` deliberately do NOT pass `--no-optional-locks`.** The flag stops git
persisting its refreshed stat cache, so every call redoes the whole refresh — measured on a 3000-file repo
at ~4.6× (629/835/688/440 ms with the flag, 687/120/144/153 ms without; the first call pays the refresh,
the rest are cheap). The watcher loop it appeared to prevent does not happen: watching the index mtime
across repeated runs shows git rewriting `.git/index` **once** after the worktree changes and then leaving
it alone, which the 500 ms debounce absorbs.

**Opening and refreshing issue their git reads in parallel.** `for-each-ref`, `stash list`, `log` and
`rev-parse --absolute-git-dir` do not depend on one another and — importantly — none of them writes
`.git/index`, so they are safe to overlap; they go out together behind one `Task.WhenAll` rather than in
sequence. Measured on a 12k-commit repository: **406 ms sequential vs 250 ms parallel**. `WhenAll` runs
before any result is read, so a failure in one cannot leave the others unobserved. `RepositoryState` is
fetched with the batch but *applied* only after `IsLoading` clears, so the state banner's command gates
still settle on their final values.

> Only `status` and worktree `diff` write the index, and neither is in this batch. Adding a write to it
> would need rethinking — two git processes racing for `index.lock` is a real failure mode.

Three overlapping mechanisms keep in-app writes from flickering the UI:

| Mechanism | Armed by | Effect |
|---|---|---|
| 500 ms debounce | every FS event | Coalesces bursts (trailing edge). |
| 2 s suppression window | `RunStatusMutationAsync` only | Downgrades a `repoDirty` echo to a status-only reload, so index writes don't reload the log. |
| `IsLoading` guard | any load in flight | Drops the event entirely. |

Every git write goes through one runner, `MainViewModel.RunGitOperationAsync`, which takes a `RefreshScope`:

| Scope | Used by | Behaviour |
|---|---|---|
| `StatusOnly` | stage / unstage / discard | Arms the suppression window, reloads only the status list. |
| `FullOnSuccess` | checkout / branch / tag, stash save / drop | Full refresh, **no** suppression window; skipped when the op failed, because nothing changed. |
| `FullAlways` | fetch / pull / push, merge / rebase / cherry-pick / revert, **stash apply / pop** | Full refresh **even on failure**. |

The test for which scope an operation belongs in is *does a failure leave state behind?* Checkout, branch,
tag, stash-save and stash-drop all fail without changing anything. Stash **apply/pop** do not: a conflicting
pop writes the markers, leaves the file unmerged (`UU`), keeps the stash entry, and still exits 1 — so it
belongs with the sequencer commands, not with the ref writes.

**Ref writes deliberately skip the suppression window.** Checkout/branch/tag move HEAD and refs, so the log,
decoration badges and sidebar all must rebuild — arming the window would swallow exactly the refresh they
need. Network and sequencer writes refresh **even on failure**: a fetch can update some refs and still error,
and a merge that stops on a conflict has rewritten the index and left the repository mid-operation, so
skipping the refresh would leave the app showing a repository that no longer exists.

The commit-list filter is **debounced by 180 ms** and applied as a single collection reset
(`BulkObservableCollection.Reset`). Without both, each keystroke re-filtered up to 2000 rows, raised 2001
`CollectionChanged` events, and reset `SelectedCommit` — which re-ran that commit's git work, measured at
~257 ms of git per character typed.

## Known structural debt

Recorded here so it isn't rediscovered. See the refactor plan for the intended fixes.

- **Two God objects remain:** `MainViewModel` (~2.1k lines, 134 public members, zero `ICommand`s) and
  `RepositoryWorkspace.xaml.cs` (~2.2k lines, 75 handlers, 27 imperative `ContentDialog`s). `GitBackend` has
  been split by area (above), though it still presents 47 methods on one type.
- **`MainViewModel` still has no `ICommand`s.** The eight `Can*` gates are now declared with
  `[NotifyPropertyChangedFor]` rather than re-raised by hand, but ~60 `Click` handlers remain in
  code-behind. Moving them to `[RelayCommand]` needs an `IDialogService`: most are "show a dialog,
  gather input, call the VM", and the dialog needs a `XamlRoot` the VM does not have.
- **`MainViewModel` cannot be constructed headless** (its constructor calls
  `DispatcherQueue.GetForCurrentThread()`). The fix is *not* an `IDispatcher` abstraction - extracting
  policy into Core behind a host interface gets logic under test without touching VM construction.
- **No C# tests**, and `MainViewModel` cannot be constructed headless (its constructor calls
  `DispatcherQueue.GetForCurrentThread()` and reads `ApplicationData`).
- **The commit graph is a placeholder** — one blue lane per row; parents are parsed but unused. See
  **[graph.md](graph.md)**.

## Reference material

`TortoiseGit/` in the repo root is a **vendored read-only reference clone** — untracked, never compiled, and
**GPLv2+**. Read it for behavior (progress parsing, lane algorithm, conflict handling); do not copy code into
this tree.
