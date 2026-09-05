# Testing

Three suites. The first two are hermetic — no repository, no `git.exe`, no Windows App SDK runtime.
The third deliberately is not: it exists to check the assumptions the other two encode.

| Suite | Covers | Runner |
|---|---|---|
| `MasterSplinter.Logic.Tests` (Google Test) | The C++ core - command building, the git-output parsers, and the packed wire format: **288 cases / 63 suites** | `MasterSplinter.Logic.Tests.exe` |
| `MasterSplinter.Core.Tests` (xunit) | The host: packed unpacking, ABI contracts and extracted policy: **224 cases** | `dotnet test` |
| `EndToEndSmokeTests` (in the same xunit project) | The real native DLL + real git against a scratch repo: **16 cases** | `dotnet test` |

**Phase D moved the parsers, and the coverage moved with them.** Every xunit file it deleted was
replaced by gtest cases in the same commit, usually with a few more: `RecordParserTests`,
`UnifiedDiffParserTests` and `PorcelainBlameParserTests` are gone, and `parse_test.cpp` /
`status_parse_test.cpp` hold their successors. What stayed on the host is *unpacking* - the
`*UnpackTests` files - plus all the policy that was never parsing in the first place.

All run in CI on every push, on an `x64` + `ARM64` matrix.

**Know which level a claim reached** — see the four levels in the plan file. "Compiles" and "unit tests
pass" are different claims from "the packaged app launches", and none of them mean a feature was driven
through the UI.

## Native — `MasterSplinter.Logic.Tests`

Injects a fake `IProcessRunner` into `GitBackend` and asserts **the exact git argv** each operation builds,
then scripts the fake's output and asserts the interpretation. Because `GitBackend` is pure logic, the suite
is OS-independent — the same tests will run on macOS.

```powershell
msbuild MasterSplinter.Logic.Tests\MasterSplinter.Logic.Tests.vcxproj -t:Build -p:Configuration=Debug -p:Platform=ARM64
```

```powershell
MasterSplinter.Logic.Tests\ARM64\Debug\MasterSplinter.Logic.Tests.exe
```

Running the Debug build needs the debug CRT on `PATH`. Match the build architecture to the machine. The test
project compiles the core sources directly rather than linking the DLL, so **adding a new
`GitBackend.<Area>.cpp`, `Parse/*.cpp` or `Packed/*.cpp` means adding it to both `.vcxproj` files**, not
just the Logic one, with `<PrecompiledHeader>NotUsing</PrecompiledHeader>`.

### `FakeProcessRunner`

A hand-written double, no mocking framework:

- Records every call — `executable`, exact `args`, `input`, `env`, `hadSink`, `sinkCancelled`. The vector is
  `mutable` because `Run` is `const`.
- Responses are scripted **by call index**; once exhausted it yields `("", 0)`. That is what makes
  multi-spawn operations (`StashSave`'s probe → push → re-probe) testable.
- `processStarts = false` simulates the "could not start" contract: returns `false`, empty out, exit `-1`.
- It **replays streaming**, invoking the sink with the scripted output and then the `(nullptr, 0)` heartbeat,
  so cancellation is testable without a real process.
- Helpers: `CallCount`, `ArgsOf(i)`, `InputOf(i)`, `EnvOf(i, name)`, `ArgsContain(i, flag)`, `HadSink(i)`,
  `SinkCancelled(i)`.

### The packed format and the parsers

`packed_test.cpp` reads the wire format with its own inline little-endian helpers, deliberately NOT
through `PackedRead.h`: if the writer and the reader shared a mistake it would cancel out and the suite
would stay green while the real boundary broke. `PackedBufferTests.cs` does the same on the host,
assembling buffers by hand from the documented offsets. Those two files are the pin; everything
downstream uses the shared readers, because by then the format itself is already fixed.

The parser suites (`parse_test.cpp`, `status_parse_test.cpp`) feed each parser sample git output and
assert the records that come back. They need no `FakeProcessRunner` at all - a parser is a free function
over a string, touching neither git nor the process runner.

### Two kinds of test

**Exact-argv tests** are the bulk — deliberately change-detectors on command construction, because the argv
*is* the contract with git.

**Guard tests** encode policy and should never be deleted:

| Test | Guards |
|---|---|
| `Checkout.NeverForces` | no `--force` on switch |
| `CreateBranch.NeverUsesForcingVariants` | no `-f` / `--force` |
| `NetworkCommands.NeverForce` | no `--force` / `--force-with-lease` |
| `Phase7Commands.NeverInteractiveOrForcing` | no `-i`, `--squash`, `-X`, `--autostash`, `--autosquash` |
| `SequencerAction.RejectsAnythingOutsideTheAllowlists` | per-operation continue/abort/skip allowlists |
| `NullRunner.DoesNotCrashAndYieldsErrorPaths` | every method survives a missing runner |

Format strings (`FMT`, `REF_FMT`, `STASH_FMT`, `REFLOG_FMT`) are **duplicated verbatim in the test file as
pins**, with a guard asserting `%x1f` never appears in the `for-each-ref` format — see the escape trap in
**[abi.md](abi.md)**.

The one non-hermetic corner is `RepositoryState`, which probes the git dir for `MERGE_HEAD` / `rebase-merge/`.
Its tests use a `TempGitDir` RAII helper to create real directories — still no git process.

## Managed — `MasterSplinter.Core.Tests`

```bash
dotnet test MasterSplinter.Core.Tests/MasterSplinter.Core.Tests.csproj -p:Platform=ARM64
```

`MasterSplinter.Core` is plain **net8.0 with no WinUI reference**, which is what lets these run under a normal
test host — no Windows App SDK runtime, no packaged identity. The parsers are `internal` (they are
`GitRepository` implementation detail, not UI-facing API) and reached via `InternalsVisibleTo`.

What is covered, and why each earns its place. Rows struck through were moved to the native suite by
Phase D and are kept here because the *reason* they exist has not changed - only the file has:

| Area | Why it is pinned |
|---|---|
| ~~`ParseUnifiedDiff`~~ (now native) | Combined `@@@` diffs — a conflicted file's diff rendered *empty* before this was handled. Plus the trailing-newline artifact below. |
| ~~`ParsePorcelainBlame`~~ (now native) | git emits the author block only on a commit's **first** group; later groups carry the sha alone. Without per-sha caching most lines render with a blank author — correctness, not optimisation. |
| `AreaFlag` / `WsFlag` | The ABI *parameter* takes 0=unstaged, 1=staged, but the C# enum declares Staged first, so casting the enum swaps them. This shipped once. One test asserts the mapping is *not* a plain cast - and another that the packed status record deliberately uses the *other* ordering. |
| `PackedBuffer` | The wire format, read from hand-built bytes rather than from the native writer, so a layout mistake cannot cancel itself out. Also that a malformed buffer degrades to inert instead of throwing. |
| The `*UnpackTests` | One per packed record layout. In particular: every enum whose value crosses the ABI as an integer (`DiffLineKind`, `FileChangeStatus`, `WorkTreeArea`, `BadgeKind`) still matches its C++ twin in declaration order. Reordering either side alone is otherwise silent. |
| ~~`IsUnmerged`~~ (now native) | All seven unmerged XY pairs, matched before the staged/unstaged split — otherwise a `UU` file is listed twice. |
| `NormalizeMessage` | A WinUI `TextBox` reports line breaks as a bare CR. |
| `ParseOkErr` | Splits on the **first** separator only, so git text containing `0x1F` survives. |
| ~~`ParseCommitRecords`~~ (now native) | The field-count floor is what keeps git's error text off the commit list. |
| `SideBySideBuilder` | Positional pairing of removed/added runs, and the one-sided filler rows. |
| `GitOperationRunner` | The refresh policy. A conflicting stash pop changes the working tree *and* exits non-zero, so treating failure as "nothing changed" showed a stale tree — pinned by a regression test. |
| `CommitOrdering` | git cherry-pick applies its arguments left to right, so the order is a correctness requirement. Dates are deliberately ignored: they tie, and rebases reorder them. |
| `SidebarBuilder` | Section omission, remote grouping, subtree visibility, and the upstream split (a branch name may itself contain slashes). |
| `RemoteUrl` | URL shape rules, with the local-path probe injected so they test without a filesystem. |
| `WorkingTreeWarning` | Tracked vs untracked wording. Conflating them has bitten twice — untracked files never block a switch but do block a fast-forward, and telling someone to "commit" an untracked file contradicts git's own message. |
| `SearchBanner` | Names the predicate that actually ran; a blank query with a path filter is described as the path search it really is. |
| `EditorLauncher.SplitCommand` | Quoted exe paths (Program Files) and malformed input. |
| `Confirmations` | What each destructive confirmation promises. One test asserts **every** destructive confirmation defaults to Cancel, so a reflexive Enter cannot discard work; another that discarding an *untracked* file says "permanently delete" rather than reusing the tracked wording. |
| `DiffCache` | The LRU bounding retained diffs. Pins that eviction releases *both* representations, flips `DiffLoaded` back so the file reloads, and never evicts the entry just loaded. |
| `CommitIndex` | The lookup the commit graph is built on. Pins that a parent outside the loaded window is a miss (the log is capped, so edges routinely leave it) and that membership is reference identity (rows are rebuilt every refresh). |
| `SettingsStore` / `RecentRepositoriesStore` | Round-trip, case-insensitive path dedup (Windows would otherwise list one repo twice), the 10-entry cap, and that corrupt or unwritable files degrade to defaults instead of throwing. |

> **Write separators as `\u001f`, never `\x1f`.** C# hex escapes are greedy, so `"\x1ffatal"` is the single
> character U+01FF rather than US followed by `f`. `\u` is fixed-width and cannot do that. (Same family as the
> `for-each-ref` `%xx` vs `log` `%xNN` trap in [abi.md](abi.md).)

**A bug these tests found on their first run** (the parser has since moved to
`Parse/DiffParser.cpp`, and so has this test): `ParseUnifiedDiff` emitted a phantom blank context line,
carrying a line number, at the end of *every* diff. Splitting on newlines leaves an empty tail after git's
final newline, and the parser treated it as content. Verified against real git that a genuinely blank context
line always arrives as a single space (git writes the marker column), so a zero-length line inside a hunk can
only ever be that artifact.

## End-to-end — `EndToEndSmokeTests`

Builds a scratch repository with **real git** — root commit, two-parent merge, rename, tag, and a dirty
tree with staged / unstaged / untracked entries — then drives the **real native DLL** through
`GitRepository`.

This is a different proof from the unit tests. Those feed the parsers hand-written samples, which shows
they parse what we *believe* git emits; these show git actually emits it. They also confirm P/Invoke
resolves now that `NativeLogic` lives in its own assembly — a class of breakage no unit test can see.

Skips itself (rather than failing) when git or the native DLL is unavailable, so a machine without them
still gets a green unit run. CI builds the native core before the managed test step so these run there.

## Coverage gaps

Known and deliberate:

- **`GitApi.cpp`** - the `Joined`/`Sink` adapters and the exception guard. `DupBytes` and the `outLen`
  contract are now exercised indirectly by every packed export the end-to-end tests drive.
- **`WindowsProcessRunner.cpp`** — argv quoting, environment-block ordering, job-object teardown, the stdin
  writer thread, the heartbeat thread. Verified by integration and manual testing instead.
- **`MacProcessRunner.mm`, `PlatformFactory.cpp`** — the Mac path is source-complete but never built.
- **`MainViewModel` and the WinUI layer** — still untested. The VM cannot be constructed headless (its
  constructor calls `DispatcherQueue.GetForCurrentThread()`). The fix is **not** an `IDispatcher`
  abstraction: extracting policy into Core behind a host interface (as `GitOperationRunner`,
  `SidebarBuilder` and `CommitOrdering` did) gets the logic under test without touching VM construction.
- **Driving the UI.** Nothing here opens a repository, stages a file or views a diff in the running app.
  That needs GUI automation.

## Verifying against a real repository

**P/Invoke the built DLL straight from PowerShell** (`Add-Type` plus UTF-8 `byte[]` params) against a scratch
repo. Needs the debug CRT on `PATH`. Windows PowerShell 5.1 is .NET Framework, so: `Marshal.PtrToStringUTF8`
does not exist (hand-roll a NUL scan); `Set-Content -Encoding utf8` writes a BOM and the `.ps1` itself must be
UTF-8 **with** BOM; single-element arrays unroll (wrap them in `@(...)`); and a `PSMethod` cannot be cast to a
delegate — add a `static Bind(...)` factory inside the `Add-Type` C# source.

**Testing credential-prompt suppression without popping Git Credential Manager:** set `credential.helper=`
(empty) on a scratch repo and use a URL with a *username but no password* (`https://nobody@github.com/...`) —
git fails in about 1.5 s with "terminal prompts disabled". A plain nonexistent GitHub repo does **not** work:
it returns "Repository not found" without ever challenging.
