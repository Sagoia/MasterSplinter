#pragma once

// Direct2D renderer for the commit graph, presenting onto a WinUI SwapChainPanel.
//
// This is the SECOND translation unit in the core allowed to include <windows.h> (via the D3D and
// D2D headers). The invariant it joins WindowsProcessRunner in holding is the same: everything
// platform-specific lives under a Platform/ or Render/Windows/ folder, and nothing else in the
// core depends on Windows headers.
//
// Why Direct2D rather than Win2D: Win2D IS Direct2D behind a WinRT wrapper. Going straight to D2D
// avoids a NuGet dependency, and puts the drawing code where the portable core already is.

#include <memory>

#include "../IGraphRenderer.h"

namespace ms::render
{
    std::unique_ptr<IGraphRenderer> CreateD2DGraphRenderer();
}
