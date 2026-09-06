# The commit graph

Lane layout is portable C++; drawing is per-platform. The split the core already used for process
execution, applied again:

```
git log --all --parents
   → Parse/LogParser        (records, C++)
   → Graph/GraphLayout      (lanes, C++, gtest'd)
   → display list           (a byte-sized span, the contract)
   → Render/Windows/D2DGraphRenderer   /   CoreGraphics (macOS, later)
```

This page described a plan until Phases E and F landed. It now describes what was built, and
**flags the four places the plan turned out to be wrong** — each marked *Corrected*, because they
are the parts someone would otherwise repeat.

## Layout — `Graph/GraphLayout.{h,cpp}`

Pure logic: no git, no process runner, no OS, no strings in the output. Input is per-row parent
**row indices**; output is the display list.

The model is the standard incremental active-lane walk. Per row: find the lane the commit sits on
(the leftmost one already waiting for it, or a fresh slot), route each parent, release lanes as
branches end. Five lane operations — claim, claim-for-parent, allocate, set, free.

Two rules keep the graph narrow, and both were added *because* driving it against a real
repository showed the graph fanning into a wall of parallel lines:

- **A merge parent joins a lane already heading for that commit** rather than opening another.
- **A commit folds into a lane to its LEFT that is already heading for its first parent.** This is
  what git draws as `|/`. Verified on CNTK, whose top six commits all have parent `e93964800`:
  without the fold each held its own lane down to that commit; with it they collapse exactly as
  git does. Leftward only — pulling a long-running lane rightward onto a tip would move the
  mainline around from row to row.

A parent outside the loaded window is `-1`, not an error: the log is capped at `MaxCommits`, so an
edge routinely leaves it. Such a row is flagged `kFlagBoundary` and drawn with a hollow dot, so the
line leaving the bottom of the row does not read as a dead end.

Colours cycle 0–5 on lane *allocation*, not by lane index, so a branch keeps one colour for its
whole life — which is the thing a reader actually follows.

## The display list

```
u32 rowCount
per row:  u8 laneCount · u8 dotLane · u8 colorIndex · u8 flags · u8 segCount
          then segCount × { u8 x1, u8 y1, u8 x2, u8 y2, u8 colorIndex }
```

X is a lane index. Y is in **half-row units** (0 top, 1 centre, 2 bottom) so the whole list stays
byte-sized; the renderer scales by row height. `flags`: bit 0 merge, bit 1 root, bit 2 boundary.

> **Corrected.** The plan omitted the leading `u32 rowCount`. A renderer cannot validate a buffer
> without it, and the host cannot check the graph describes the rows it actually has.

It rides in the **extra section of the packed log buffer** (see [abi.md](abi.md)), computed by
`ParseLogRecordsWithGraph` in the same pass that builds the records.

> **Corrected.** The plan called for a separate `MsGitLogGraph` that re-walks the log. It exists
> under that name, but it does the layout *inside the record parse* rather than as a second pass —
> that is the only place each commit's parents are already resolved to row positions, and a
> separate walk could disagree with the first if a ref moved between them, drawing the graph
> against rows that are no longer on screen.

Search results deliberately carry **no** graph, and neither does a filtered list. Both are subsets,
so almost every parent lies outside them; lanes drawn between them would describe a history that is
not the one on screen. The old placeholder drew one blue lane per row here, which was equally
meaningless but looked authoritative.

## Renderer — Direct2D on a `SwapChainPanel`

`Render/IGraphRenderer.h` (portable) + `Render/Windows/D2DGraphRenderer.cpp`, built by
`IPlatformFactory` — the same Abstract Factory seam `IProcessRunner` sits on, so macOS drops in a
CoreGraphics implementation and nothing above changes. `MacPlatformFactory` returns `nullptr`
today, which the host treats as "draw no graph".

A **handle-based** ABI, the only one here: a renderer owns a GPU device that has to live across
calls. `MsGraphCreate` takes the panel's `IUnknown*`; every piece of COM and D3D work stays native.

- D3D11 (BGRA support) → `ID2D1Device` → device context; swap chain via
  `IDXGIFactory2::CreateSwapChainForComposition`, flip-sequential, `B8G8R8A8_UNORM`, 2 buffers.
- Falls back to WARP when there is no usable GPU. A failed create degrades to a blank graph column,
  never to a crash.
- `DXGI_ERROR_DEVICE_REMOVED` / `_RESET` and `D2DERR_RECREATE_TARGET` rebuild the device.
- Rows are indexed once when the model is set, so a scroll jumps straight to the first visible row
  instead of walking the list every frame.

> **Corrected, and this is the big one.** The plan had the panel overlay the list transparently,
> with the row selection showing through from underneath. **That does not work.** A WinUI 3 desktop
> `SwapChainPanel` does not blend with the XAML content behind it: clearing fully transparent
> leaves black, not the rows. Measured — clearing to 50% red over a (30,30,30) list reads back
> (64,0,0), which is the source composited over *black*.
>
> So the surface paints the row background and the selection band itself, from colours read out of
> the same `ListView` resources the rows use. WinUI insets its band by 2px and rounds it; a plain
> full-height rectangle left a visible step at the column boundary, so the renderer reproduces
> both. Measured after: the band spans y 509..530 on each side of the boundary.
>
> The cost is that a little list chrome now lives in the renderer (`Chrome`, `SetSelection`).
> Hover is not reproduced — the row under the pointer shows its pointer-over tint only in the
> Description column. That is the one visible seam left, and it is deliberate: tracking hover would
> mean duplicating more of the template for less.

> **Corrected.** `ISwapChainPanelNative` is hand-declared (`MIDL_INTERFACE`, one method, the GUID
> `63aad0b8-7c24-40ff-85a8-640d944cc325`). The plan noted the header ships in the WinUI package —
> it does, but using it means adding the whole WinUI NuGet package to a plain C++ DLL that needs
> nothing else from it.

On the XAML side the graph column leaves the `DataTemplate` entirely: a `SwapChainPanel` overlays
the fixed 150 px column with `IsHitTestVisible=false`, fed the history `ScrollViewer`'s vertical
offset on `ViewChanged`. Row height is a fixed **26 px**, which is what makes offset → row exact —
verified by selecting a row after scrolling ~1400 px and measuring the band centred on the same
pixel in both columns.

**Why Direct2D rather than Win2D:** Win2D *is* Direct2D behind a WinRT wrapper. Going straight to
D2D avoids a NuGet dependency and puts the drawing code where the C++ core already is.

## What this design cannot do

The renderer is not unit-testable the way the layout is. `GraphLayout` has 19 gtest cases; the
renderer is verified visually, against `git log --graph --oneline --all` on a merge-heavy
repository. A "draw to a WIC bitmap and hash it" harness would close that if regression coverage is
ever wanted.

## How it shipped

Three commits, each independently verifiable:

1. **E** — layout in C++ + display list + gtest, still drawn by the old per-row `GraphCanvas`.
   Proved the algorithm against real repositories at zero rendering risk. It also exposed that
   `GraphCanvas` never clipped to its column: it had only ever drawn one lane, and CNTK's
   twenty-odd painted straight over the Description text.
2. **F1** — the D2D renderer replaces `GraphCanvas`.
3. **F2** — `GraphCanvas.cs` and `Models/Graph.cs` deleted. Isolating those types during the
   refactor is what made this a file deletion rather than an untangling.
