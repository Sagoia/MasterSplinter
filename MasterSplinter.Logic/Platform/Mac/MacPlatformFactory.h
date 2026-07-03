#pragma once
// MacPlatformFactory — the concrete Abstract Factory for macOS. Builds a MacProcessRunner.

#include "../IPlatformFactory.h"

namespace ms
{
    class MacPlatformFactory final : public IPlatformFactory
    {
    public:
        std::unique_ptr<IProcessRunner> CreateProcessRunner() const override;
    };
}
