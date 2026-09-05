# The flat C ABI

`MasterSplinter.Logic/MasterSplinter.Logic.h` is the single source of truth — every export carries a comment
explaining its exact git command, field layout and edge cases. This page is the map; read the header for the
detail.

**52 exports**, all `extern "C"`, C types only, no name mangling. The C# side is
`Interop/NativeLogic.cs` — the only P/Invoke site in the app.

## Rules

- **Strings are UTF-8.** Inbound parameters marshal as `UnmanagedType.LPUTF8Str`; `bool` marshals as
  `UnmanagedType.I1` (C++ `bool` is 1 byte, not the 4-byte Win32 `BOOL`).
- **Every `char*` return is heap-allocated by the DLL and must be released with `MsGitFree`.** C# funnels
  this through one `TakeString` helper with the free in a `finally`. The one exception is `MsLogicVersion`,
  which returns a static that must **not** be freed.
- **Delimiters:** fields `0x1F`, records `0x1E`. Payloads are NUL-free (the marshaller stops at the first
  NUL) — except the one binary export below.
- **Multi-value arguments** (path lists, cherry-pick sha lists) are `0x1E`-separated in a single string.
- **Lifecycle lives in `MsLogicInitialize`/`MsLogicShutdown`, not `DllMain`** — DllMain runs under the loader
  lock and is Windows-only. C# calls them from `App.OnLaunched` / window `Closed`.
- **Nothing throws across the boundary.** An exception unwinding through a C ABI is undefined behavior, so
  every entry point in `GitApi.cpp` is wrapped: `CallRead` / `CallWrite` catch everything and return the
  failure value for that export's convention. Inbound argument conversion happens *inside* the guard, so a
  `bad_alloc` building a `std::string` is caught too.

## Three return conventions

They coexist deliberately; know which one you are calling.

| Convention | Failure looks like | Used by |
|---|---|---|
| **Plain read** | empty string | `MsGitLog`, `MsGitRefDetails`, `MsGitStatus`, `MsGitCommitFiles`, `MsGitRemotes`, `MsGitAheadBehind`, `MsGitStashList`, `MsGitSearchLog`, `MsGitReflog`, the diff/shortstat reads |
| **OK/ERR framed** | `ERR\x1f<git's merged output>` | every write, plus `MsGitOpenRepository`, `MsGitHeadMessage`, `MsGitRepositoryState`, `MsGitBlame` |
| **Raw bytes** | `nullptr` | `MsGitFileBytesAtCommit` only — `char*` + `int* outLen`, **may contain NULs**, copy exactly `*outLen` bytes |

Blame is OK/ERR-framed because "that path is not in that revision" is a routine, actionable failure worth a
message. Split it on the **first** `0x1F` only: the payload is file content and may contain `0x1F` itself.

## Exports by area

| Area | Exports |
|---|---|
| Lifecycle | `MsLogicInitialize` `MsLogicShutdown` `MsLogicVersion` `MsLogicAdd` |
| Repository | `MsGitIsRepository` `MsGitOpenRepository` |
| History | `MsGitLog` `MsGitRefDetails` |
| Commit inspection | `MsGitCommitFiles` `MsGitCommitShortStat` `MsGitFileDiff` `MsGitFileAtCommit` |
| Compare (a..b) | `MsGitRangeFiles` `MsGitRangeShortStat` `MsGitRangeFileDiff` |
| Working tree | `MsGitStatus` `MsGitWorkTreeFileDiff` `MsGitFileBytesAtCommit` |
| Stage & commit | `MsGitStagePaths` `MsGitStageAll` `MsGitUnstagePaths` `MsGitDiscardPaths` `MsGitCommit` `MsGitHeadMessage` |
| Branches & tags | `MsGitCheckout` `MsGitCreateBranch` `MsGitDeleteBranch` `MsGitRenameBranch` `MsGitCreateTag` `MsGitDeleteTag` `MsGitAheadBehind` |
| Remotes | `MsGitRemotes` `MsGitSetRemoteUrl` `MsGitFetch` `MsGitPull` `MsGitPush` |
| Sequencer | `MsGitMerge` `MsGitRebase` `MsGitCherryPick` `MsGitRevert` `MsGitSequencerAction` `MsGitMergeTool` `MsGitRepositoryState` |
| Stash | `MsGitStashList` `MsGitStashSave` `MsGitStashApply` `MsGitStashPop` `MsGitStashDrop` |
| Blame / search / reflog | `MsGitBlame` `MsGitSearchLog` `MsGitReflog` |
| Memory | `MsGitFree` |

## Progress & cancellation

The seven long-running commands (`Fetch`, `Pull`, `Push`, `Merge`, `Rebase`, `CherryPick`, `Revert`, plus
`SequencerAction` and `MergeTool`) take an optional callback:

```c
typedef int (*MsGitProgressFn)(void* userData, const char* bytes, int length);
```

Called with each chunk of git's merged output (**not** NUL-terminated — use `length`) and with `(NULL, 0)` as
a periodic heartbeat. **Return 0 to cancel**; the child tree is terminated and the command returns `ERR` with
whatever it had produced. Invocations are serialized and never outlive the originating call, so the callback
needs no lock. This is why the ABI has no cancel-handle exports.

## Traps this ABI has already paid for

- **The `area` int in `MsGitWorkTreeFileDiff` is 0=unstaged, 1=staged, 2=untracked** — the C# `WorkTreeArea`
  enum declares Staged first, so casting the enum to int **swaps** them. Always go through
  `GitRepository.AreaFlag`. This shipped once and was caught only in UI verification.
- Because of that, `MsGitSequencerAction` takes **named strings**, not an int pair — one export instead of
  twelve, with per-operation allowlists (`merge` → continue|abort; the rest → +skip). Rejected values are
  quoted, never spliced behind `--`.
- **`for-each-ref` uses `%xx` escapes, `log --pretty` uses `%xNN`.** In a `for-each-ref` format, `%1f` emits
  byte 0x1F while `%x1f` emits the literal text `%x1f`. Get it wrong and every field parses as one blob and
  the sidebar silently empties. Both format strings are pinned by tests.
- **Paths travel via `-z`, translated NUL -> RS by the native side.** `core.quotePath=false` only
  stops *non-ASCII* escaping; a path containing a quote, a backslash or a control character is
  still C-quoted in the line-based formats, so `MsGitCommitFiles`/`MsGitRangeFiles` used to hand
  the app a filename that does not exist (`"a\nb.txt"`, quotes and backslash included). `-z` is the
  only way to get the real bytes; `MsGitStatus` already did this, and `NulToRs` in `GitText.h` is
  now the one implementation. NOTE the record shapes differ: porcelain packs `XY <path>` into one
  token, while `--name-status` emits the status and each path as SEPARATE tokens - one path
  normally, two (old then new) for R/C - so the reader walks the stream rather than splitting rows.
- **Merge diffs need `--diff-merges=first-parent`, not `-m`.** `-m` emits one section *per parent*
  even alongside `--first-parent`, so `MsGitCommitFiles` silently listed the union of both parents'
  changes while `MsGitCommitShortStat` printed one shortstat line per parent — which the C# regex
  parser then blended into a single wrong stat. Worse, without either flag `diff-tree` prints
  **nothing** for a merge, so `MsGitFileDiff` returned empty and every file listed under a merge
  commit opened to a blank diff pane. All three exports must carry the same flag or they describe
  three different diffs. Requires git 2.31+. Pinned by `MergeDiffs.AllThreeCommandsUseFirstParentDiffMerges`.
- **`MsGitStashSave` returns ERR when git stashed nothing.** `git stash push` exits 0 on a clean tree;
  reporting that as success tells the user their work is parked when it is not. It probes `refs/stash` before
  and after (3 spawns) rather than matching git's localizable message.
- **Stash selectors are positional** — `stash@{2}` is only valid until the next drop/pop renumbers
  everything. Re-read the list after every mutation.
- **`MsGitSearchLog` picks exactly one git predicate per mode.** Git ANDs `--grep` with `--author` rather
  than ORing, so a combined "message or author" search would silently return the intersection.
- **The Phase 8 reads check the exit code**; `MsGitLog` historically did not and relied on the C# parser's
  field-count floor to drop git's error text. `git reflog show refs/stash` on a repo that never stashed exits
  non-zero, and that is the *ordinary* case.
- **Nothing forces.** No `--force`, no `--force-with-lease`, no `-X ours/theirs`, no `--squash`, no
  `rebase -i`, no `--autostash`. Git's refusal is the message the user sees. Pinned by guard tests.
- **`GIT_EDITOR=true` is mandatory** on every sequencer command — `rebase --continue` and
  `cherry-pick --continue` re-open `$EDITOR` and have no `--no-edit` flag, so a GUI child would hang forever.
  Paired with `GIT_SEQUENCE_EDITOR=true` and `GIT_TERMINAL_PROMPT=0`.

## Adding an export

1. Declare it in `MasterSplinter.Logic.h` with a comment stating the exact git command and the field layout.
2. Implement it in the matching `GitBackend.<Area>.cpp` — build a `GitArgs`, end with a terminal verb
   (`RunRaw` / `RunRead` / `RunValue` / `RunWrite`).
3. Add the `extern "C"` shim in `GitApi.cpp` — one line, `CallRead` or `CallWrite` depending on convention.
4. Add gtest cases asserting the exact argv (see **[testing.md](testing.md)**).
5. Add the `[DllImport]` + `TakeString` wrapper in `NativeLogic.cs`, and the parse in `GitRepository.cs`.

A native ABI change wants a clean C++ rebuild — see **[../README.md](../README.md)**.
