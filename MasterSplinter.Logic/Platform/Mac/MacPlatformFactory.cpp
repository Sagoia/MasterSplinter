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
}

#endif // defined(__APPLE__)
