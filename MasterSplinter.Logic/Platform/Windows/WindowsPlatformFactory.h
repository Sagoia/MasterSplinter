#pragma once
// WindowsPlatformFactory — the concrete Abstract Factory for Windows.
// Builds a WindowsProcessRunner (and, in future, any other Windows platform services).

#include "../IPlatformFactory.h"

namespace ms
{
    class WindowsPlatformFactory final : public IPlatformFactory
    {
    public:
        std::unique_ptr<IProcessRunner> CreateProcessRunner() const override;
    };
}
