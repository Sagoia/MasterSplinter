# Refactoring pass — scope and outcomes

**This pass is refactoring only.** No feature was added, removed or changed. Everything here is
structure, tests, and two behaviour bugs found *by* the new tests. The commit deliberately contains
no work from Phases D, E or F — see [Deferred](#deferred) below.

**Verified at the end:** 188/188 native gtest, 227/227 managed xunit (213 unit + 14 end-to-end),
clean rebuild with zero warnings, packaged app launches.

---

## What changed

### The C# side gained a testable half

`MasterSplinter.Core` — plain `net8.0`, **no WinUI reference** — now holds the models, the git output
parsers, the P/Invoke surface and the UI-agnostic policy. That boundary is what lets the tests run
under a normal test host: no Windows App SDK runtime, no packaged identity.

The C# test count went **0 → 227**. `NativeLogic` is `internal`; the app sees only a small
`NativeCore` lifecycle facade, so the 52 `MsGit*` entry points cannot be reached from the UI layer.

### The C ABI stopped being able to crash the host

`GitApi.cpp` had **no `try/catch` anywhere**. A `std::bad_alloc` or a `std::filesystem` throw would
have unwound across the C ABI — undefined behaviour, taking the process with it. Every entry point is
now guarded, with argument conversion moved *inside* the guard, and `Backend()` no longer takes a
process-wide mutex on every call.

### Duplication collapsed behind named policy

| Was | Now |
|---|---|
| 4 near-identical mutation runners (2 byte-for-byte identical) | one `GitOperationRunner` + a named `RefreshScope` |
| 26 copies of one guard literal, 44 `int code;`, 29 hand-built argv vectors (C++) | `GitArgs` builder + four terminal verbs |
| 27 imperatively-built `ContentDialog`s | `Confirmations` (Core, tested) + `DialogBuilder` (view) — **4 left**, none of them conversions |
| 8 command gates re-raised by a hand-maintained method | `[NotifyPropertyChangedFor]`, compiler-maintained |

### Big files split by area

Four times, always behaviour-preserving, always the same shape — and the area names now line up
across every layer:

```
GitBackend.Sequencer.cpp  ←→  GitRepository.Sequencer.cs  ←→  MainViewModel.Sequencer.cs  ←→  RepositoryWorkspace.Sequencer.cs
```

`GitBackend.cpp` 1336 → 93 + 8 · `GitRepository.cs` 1114 → 91 + 8 · `MainViewModel` 2003 → 226 + 8 ·
`RepositoryWorkspace.xaml.cs` 2171 → 137 + 8 · `Models.cs` 365 → 5 by concern.

> **The trap, hit once:** a naive by-region cut puts *cross-cutting* code in whichever area happens to
> contain it — `RunGitOperationAsync` landed in `.WorkingCopy.cs`, `RaiseCommandState` in
> `.Remotes.cs`. Anything used by more than one area must be hoisted back into the core file
> afterwards, or the split just hides the coupling.

### Bugs found and fixed

All three were found by writing tests or by measuring, not by reading code:

1. **Every diff ended with a phantom blank line** carrying a line number. Splitting on git's trailing
   newline leaves an empty tail the parser treated as content. Verified against real git that a
   genuinely blank context line always arrives as `" "`, so a zero-length line inside a hunk can only
   be that artifact.
2. **A conflicting `git stash pop` left the UI stale.** It writes conflict markers *and* exits
   non-zero; the refresh policy read that as "nothing changed". Reproduced against real git (exit 1,
   `UU`, entry kept) before changing it. Now `FullAlways`, like the sequencer commands.
3. **`--no-optional-locks` was a 4.6× pessimisation.** It stops git persisting its refreshed stat
   cache, so every `status` redid the whole refresh. The watcher loop it guarded against does not
   happen — git rewrites `.git/index` once and stops. Both re-measured before removing it.

Two more were self-inflicted during this pass and caught by self-review: a `Recent` load race, and a
static scroll latch that could have disabled auto-scroll for every later progress dialog.

---

## Patterns established

Follow these rather than re-deriving them.

**Pure policy → Core, behind a host interface if it needs the VM.** Used eleven times
(`GitOperationRunner`, `CommitIndex`, `CommitGraph`, `SidebarBuilder`, `CommitOrdering`, `RemoteUrl`,
`WorkingTreeWarning`, `SearchBanner`, `Confirmations`, `DiffCache`, the preference stores). The caller
becomes a thin host; the logic gets tested.

**Do not add abstractions to make WinUI types testable.** The plan originally called for an
`IDispatcher` so `MainViewModel` could be constructed headless. That is the wrong order — extracting
policy *out* gets the logic under test without touching VM construction, and leaves a smaller VM.

**Inject the platform edge.** `SettingsStore` takes a directory, not `ApplicationData`;
`RemoteUrl.Validate` takes the local-path probe. Both became testable and moved to Core as a result.

**Return a spec, render it elsewhere.** `Confirmations` returns title/message/button/**which button is
default**; the view only renders. What the app promises the user is then pinned by tests.

**Say which verification level a claim reached.** Compiles / unit / end-to-end / packaged launch are
four different claims. See `testing.md`.

---

## Deferred

**Phases D, E and F are explicitly out of scope for this commit** and are not started.

| Phase | Status |
|---|---|
| **D** - parsers to C++ behind a packed wire format | **Done** (D0-D6). The testability worry did not materialise: only the parser subset moved (~60 xunit cases, not 213), each deleted file was replaced by gtest cases in the same commit, and both suites grew overall. The deciding argument turned out to be neither macOS reuse nor wall-clock but **correctness**: a length-prefixed format makes the `0x1F`/`0x1E` desync impossible by construction. |
| **E** — lane layout in C++ | Deferred. Groundwork is done: `CommitGraph.Assign` is the single seam (one method body to replace), `CommitIndex` provides the hash→position lookup, and `Models/Graph.cs` isolates the placeholder types. |
| **F** — Direct2D `SwapChainPanel` renderer | Deferred. Design settled in `graph.md`. |

**E does not depend on D** — the graph display list is its own small fixed-size payload and does not
need the general packed wire format first.

---

## Still outstanding (refactoring)

1. **~60 `Click` handlers with no `ICommand` anywhere.** Blocked on an `IDialogService`: most are
   "show a dialog, gather input, call the VM", and the dialog needs a `XamlRoot` the view model does
   not have. This is an architecture change, not an extraction.
2. **`RepositoryWorkspace.xaml`** (1094 lines) — 142 classic `{Binding}`, no `x:Bind`.

`GitApi.cpp` (510) and `NativeLogic.cs` (442) are also large, but both are flat lists of ABI entry
points — repetitive by nature rather than tangled. Leave them.
