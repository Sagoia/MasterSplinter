#pragma once
// MacProcessRunner — the macOS Adapter.
//
// Adapts Apple's process API to IProcessRunner. The implementation lives in MacProcessRunner.mm
// (Objective-C++) and uses Foundation's NSTask + NSPipe — the idiomatic macOS way to launch a
// subprocess, in place of generic POSIX fork/exec. This header stays **pure C++** (no Objective-C
// types) so the portable factory and selector can include it without pulling in Foundation.
//
// Built only by the macOS toolchain (Xcode/CMake). The Windows .vcxproj does not compile it.

#include "../IProcessRunner.h"

namespace ms
{
    class MacProcessRunner final : public IProcessRunner
    {
    public:
        using IProcessRunner::Run; // keep the 4-arg convenience overload visible

        bool Run(const std::string& executable,
                 const std::vector<std::string>& args,
                 const std::optional<std::string>& input,
                 std::string& out,
                 int& exitCode) const override;
    };
}
