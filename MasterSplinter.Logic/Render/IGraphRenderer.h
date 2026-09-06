#pragma once

// The renderer seam. Drawing the commit graph is per-platform; deciding WHAT to draw is not, and
// already happened in Graph/GraphLayout.
//
// This is the second product on the IPlatformFactory family, alongside IProcessRunner, and for
// the same reason: Windows draws with Direct2D on a SwapChainPanel, macOS will draw with
// CoreGraphics, and nothing above this interface has to know which.
//
// KEEP PORTABLE: no <windows.h>. The surface arrives as an opaque void* precisely so this header
// does not have to name a platform type -- on Windows it is a SwapChainPanel's IUnknown*.

#include <cstdint>

namespace ms::render
{
    // Where the graph is drawn, in device-independent pixels, plus where the host has scrolled to.
    //
    // Row height is fixed by the host (26 px), which is what makes the offset -> row arithmetic
    // exact: the renderer draws only the rows the viewport actually covers rather than walking the
    // whole display list every frame.
    struct Viewport
    {
        float width = 0.0f;
        float height = 0.0f;
        double scrollPx = 0.0;      // the history list's vertical offset
        float rowHeight = 26.0f;
        float scale = 1.0f;         // the panel's composition scale (DPI)
    };

    // The list chrome the renderer has to paint for itself.
    //
    // Not a design preference -- a platform fact, found by measuring. A WinUI 3 desktop
    // SwapChainPanel does NOT blend with the XAML content behind it: clearing the surface fully
    // transparent leaves black, not the list underneath. Probed by clearing to 50% red over a
    // (30,30,30) list and reading back (64,0,0), which is the source composited over BLACK rather
    // than over the rows.
    //
    // So the surface cannot be a see-through overlay. It paints the row background itself, and
    // the selection with it, or the selected row shows as a black notch in the graph column.
    struct Chrome
    {
        std::uint32_t background = 0xFF1E1E1E;   // 0xAARRGGBB
        std::uint32_t selected = 0xFF2563EB;
    };

    class IGraphRenderer
    {
    public:
        virtual ~IGraphRenderer() = default;

        // Binds the renderer to the host's presentation surface. False means the device could not
        // be created, and the host should fall back to drawing nothing rather than to crashing.
        virtual bool Attach(void* surface) = 0;

        // The display list from Graph/GraphLayout. Copied: the caller owns its buffer, and a
        // refresh replaces the whole thing anyway.
        virtual void SetModel(const void* displayList, int length) = 0;

        virtual void SetViewport(const Viewport& viewport) = 0;

        // The colours to paint rows with. See Chrome for why the renderer paints them at all.
        virtual void SetChrome(const Chrome& chrome) = 0;

        // Which rows are selected, by index. The history list allows extended selection, so this
        // is a set rather than a single row; an empty set means nothing is selected.
        virtual void SetSelection(const int* rows, int count) = 0;

        // Draws one frame. Safe to call when nothing is attached or the model is empty.
        virtual void Render() = 0;
    };
}
