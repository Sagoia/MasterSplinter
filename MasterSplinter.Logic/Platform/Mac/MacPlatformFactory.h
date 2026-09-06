#pragma once
// MacPlatformFactory — the concrete Abstract Factory for macOS. Builds a MacProcessRunner.

#include "../IPlatformFactory.h"

namespace ms
{
    class MacPlatformFactory final : public IPlatformFactory
    {
    public:
        std::unique_ptr<IProcessRunner> CreateProcessRunner() const override;
        // No CoreGraphics renderer yet: returns nullptr, and the host draws no graph
        // rather than failing to open a repository.
        std::unique_ptr<render::IGraphRenderer> CreateGraphRenderer() const override;
    };
}
