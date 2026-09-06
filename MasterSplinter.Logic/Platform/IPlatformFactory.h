#pragma once
// IPlatformFactory — Abstract Factory for the platform-services family.
//
// Today the family has a single product (IProcessRunner). It is expressed as an Abstract
// Factory (rather than a lone free function) so more platform services — e.g. a git-executable
// locator, or filesystem/path normalization — can join the family later without touching any
// call site: each concrete factory just gains another Create* method.
//
// CreateProcessRunner() is the Factory Method each concrete factory overrides.
// CreatePlatformFactory() is the compile-time selector that returns the right concrete factory
// for the current OS (see PlatformFactory.cpp).
//
// KEEP PORTABLE: no <windows.h>. Compiled with PrecompiledHeader=NotUsing.

#include <memory>

#include "IProcessRunner.h"
#include "../Render/IGraphRenderer.h"

namespace ms
{
    class IPlatformFactory
    {
    public:
        virtual ~IPlatformFactory() = default;

        // Factory Method: build this platform's process runner.
        virtual std::unique_ptr<IProcessRunner> CreateProcessRunner() const = 0;

        // Factory Method: build this platform's commit-graph renderer, or nullptr where there is
        // not one yet. The family finally has a second product, which is what the single-product
        // note above anticipated -- and no existing call site changed to get it.
        virtual std::unique_ptr<render::IGraphRenderer> CreateGraphRenderer() const = 0;
    };

    // Returns the concrete platform factory for the current build target (chosen via #ifdef).
    std::unique_ptr<IPlatformFactory> CreatePlatformFactory();
}
