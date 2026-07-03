// PlatformFactory.cpp — the Factory-Method selector.
//
// Picks the concrete Abstract Factory for the current OS at compile time. This is the ONLY
// place the platform choice is hard-coded. The project targets **Windows and macOS only** (no
// Linux/POSIX); any other target is a hard compile error so it fails loudly rather than silently
// building a broken core.
//
// KEEP PORTABLE: no <windows.h>. Compiled with PrecompiledHeader=NotUsing.

#include "IPlatformFactory.h"

#if defined(_WIN32)
#include "Windows/WindowsPlatformFactory.h"
#elif defined(__APPLE__)
#include "Mac/MacPlatformFactory.h"
#else
#error "Unsupported platform: MasterSplinter.Logic supports Windows and macOS only."
#endif

namespace ms
{
    std::unique_ptr<IPlatformFactory> CreatePlatformFactory()
    {
#if defined(_WIN32)
        return std::make_unique<WindowsPlatformFactory>();
#elif defined(__APPLE__)
        return std::make_unique<MacPlatformFactory>();
#endif
    }
}
