# The commit graph

## Current state - a placeholder, but behind the right seam

Every row still draws **one blue lane with a dot**, regardless of topology. What has changed is
*where* that happens.

Layout is now a single pass over the whole log, in `CommitGraph.Assign(commits, index)`:

```csharp
// GitRepository.Log / SearchLog
=> WithGraph(ParseCommitRecords(...));   // parse, then lay out
```

It used to be assigned per row inside the record parser (`row.Graph = SimpleGraph()`), which is
structurally backwards: **lane assignment is inherently cross-row** - where a commit sits depends on
where its children sat - so a per-row hook could never have grown into the real thing. It also
allocated five objects per commit for output identical on every row; the placeholder now shares one
instance.

The primitives themselves (`GraphRow`, `GraphLine`, `GraphDot`, `GraphBuilder`, `GraphColor`) now sit
alone in `Models/Graph.cs`, so the step-3 delete below is removing a file rather than picking
types out of a 365-line one.

`CommitGraph.Assign` is the seam Phase E replaces. Only its body changes: it will hand the commit
list to the native core and unpack the display list. Parsing, the view model and the history list
stay as they are.

### `CommitIndex`

Lane layout resolves each commit's parents to positions, so it needs a hash lookup - scanning would
be quadratic on a 2000-commit log. `CommitIndex` (built once per load, thrown away with it) provides
`ByHash`, `PositionOfHash`, `PositionOf` and `Contains`, and is already threaded into
`CommitGraph.Assign` even though the placeholder ignores it.

Two semantics it pins, both of which matter to the algorithm:

- **A parent outside the loaded window is a miss, not an error.** The log is capped at `MaxCommits`,
  so an edge routinely leaves the window.
- **Membership is reference identity, not hash equality.** Rows are rebuilt on every refresh, so a
  row from a previous load has the same hash but is a different object.

The view model's own hash lookups (restoring a selection after refresh, jumping from the reflog or
blame) use it too. Every write to the loaded list goes through `SetLoadedCommits`, so the list and
its index cannot disagree.

Parent hashes have always been available - `MsGitLog` passes `--all --parents` and emits `%P` as
field 2.

## Planned design

Split along the seam the core already uses: **layout is portable logic, rendering is per-platform.**

```
git log --all --parents
   → GraphLayout (C++, pure, gtest'd)        ← the algorithm
   → display list (fixed-size binary blob)   ← the contract
   → D2DGraphRenderer (Windows) / CoreGraphics (macOS, later)
```

### Layout — `MasterSplinter.Logic/Graph/GraphLayout.{h,cpp}`

Pure logic: no OS calls, no process spawn, no strings in the output. Input is the parsed commit list
(hash + parents); output is, per row, the dot's lane, a colour index, and the segments crossing that row.

The algorithm is the incremental active-lane model: walk commits in display order, find or allocate the
commit's lane, route each parent (reuse / fork / join / cross), and release lanes when a branch merges in.
It needs a `hash → row index` map, which `CommitIndex` already provides (see above).

> **Reference, do not copy.** `TortoiseGit/src/TortoiseProc/lanes.{h,cpp}` is the qgit-derived `Lanes` class
> (~26 lane states, 8 colours) with its driver in `LogDataVector.cpp`. **TortoiseGit is GPLv2+** — reading it
> for the state machine is fine; copying the code would relicense this project.

Test cases the suite must cover: straight-line history, simple fork/merge, octopus merge, criss-cross,
multiple orphan roots, `--all` with disjoint roots, lane reuse after a branch ends, and stability when `-n`
truncates the window so some parents fall outside it.

### The display list

One new export returns the packed log records **and** the graph list in a single blob — one spawn, one
allocation, one marshal:

```c
char* MsGitLogGraph(const char* root, int order, int maxCount, int* outLen);
```

Per row: `laneCount:u8, dotLane:u8, colorIndex:u8, flags:u8`, then
`segCount:u8 × { x1, y1, x2, y2 (lane / half-row units), colorIndex }`.

Fixed-size and string-free, so the renderer walks it as a raw span with **zero per-row allocation** — and the
macOS renderer will consume the identical bytes.

### Renderer — Direct2D on a `SwapChainPanel`

`Render/IGraphRenderer.h` (portable) + `Render/Windows/D2DGraphRenderer.cpp` (D3D11 + DXGI + D2D1),
constructed by the existing `IPlatformFactory` — the same Bridge / Abstract Factory seam `IProcessRunner`
already sits on, so macOS later drops in a CoreGraphics implementation and nothing above changes.

A **handle-based** ABI, which is a new convention here (everything else is stateless root-passing):

```c
void* MsGraphCreate(void* panelUnknown, float dpi);
void  MsGraphSetModel(void* h, const void* displayList, int len);
void  MsGraphSetViewport(void* h, float widthDip, float heightDip, double scrollPx, float rowHeightPx);
void  MsGraphRender(void* h);
void  MsGraphDestroy(void* h);
```

C# hands over the `SwapChainPanel`'s `IUnknown*`; the C++ side QIs `ISwapChainPanelNative`
(`microsoft.ui.xaml.media.dxinterop.h`, shipped in the Windows App SDK WinUI package) and calls
`SetSwapChain`. **All COM and D3D work stays native.** Swap chain via
`IDXGIFactory2::CreateSwapChainForComposition`, flip-sequential, `B8G8R8A8_UNORM`, 2 buffers; scale from
`CompositionScaleChanged`; `DXGI_ERROR_DEVICE_REMOVED`/`_RESET` must recreate the device.

On the XAML side the graph column leaves the `DataTemplate` entirely: a `SwapChainPanel` overlays the fixed
150 px column with `IsHitTestVisible=false`, fed the history `ScrollViewer`'s vertical offset on
`ViewChanged`. Row height stays a fixed **26 px**, which is what makes offset → row arithmetic exact.

**Why Direct2D rather than Win2D:** Win2D *is* Direct2D behind a WinRT wrapper. Going straight to D2D avoids a
NuGet dependency and means the drawing code is already where the planned C++ migration wants it.

**What this design cannot do:** the renderer is not unit-testable the way the layout is. Only `GraphLayout`
gets gtest coverage; the renderer is verified visually, with an optional "draw to a WIC bitmap and hash it"
harness if regression coverage is wanted later.

### Staging

Shippable in three steps:

1. Layout in C++ + display list + gtest, still drawn by the existing `GraphCanvas` (translate the display
   list back into `GraphRow`). Proves the algorithm against real repositories at zero rendering risk.
2. `SwapChainPanel` + D2D renderer replaces `GraphCanvas`.
3. Delete `GraphCanvas.cs` and the `GraphRow` / `GraphLine` / `GraphDot` / `GraphBuilder` / `GraphColor`
   model types.

Correctness check at every step: compare the rendered lanes against `git log --graph --oneline --all` on a
merge-heavy repository.
