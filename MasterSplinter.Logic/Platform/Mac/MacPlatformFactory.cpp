// MacPlatformFactory.cpp — concrete Abstract Factory (macOS).
//
// Pure C++: it only news up a MacProcessRunner (whose Objective-C++ body lives in the .mm; the two
// objects link together in the macOS dylib). Guarded like the runner; built only on macOS.

#if defined(__APPLE__)

#include "MacPlatformFactory.h"
#include "MacProcessRunner.h"

namespace ms
{
    std::unique_ptr<IProcessRunner> MacPlatformFactory::CreateProcessRunner() const
    {
        return std::make_unique<MacProcessRunner>();
    }

    std::unique_ptr<render::IGraphRenderer> MacPlatformFactory::CreateGraphRenderer() const
    {
        // The display list is platform-neutral, so this is a CoreGraphics view away -- but an
        // honest nullptr beats a stub that silently draws nothing while claiming to work.
        return nullptr;
    }
}

#endif // defined(__APPLE__)
