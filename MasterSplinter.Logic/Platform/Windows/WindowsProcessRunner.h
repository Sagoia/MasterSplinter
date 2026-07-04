#pragma once
// WindowsProcessRunner — the Windows Adapter.
//
// Adapts the Windows process API to IProcessRunner, mixing classic Win32 (CreateProcessW + pipes,
// arg quoting) with modern C++/WinRT (string conversion) and WIL (RAII handles). The .cpp is the
// ONLY translation unit in the core that includes <windows.h> (via framework.h) — all the
// platform-specific launch code lives there so nothing else depends on Windows headers.

#include "../IProcessRunner.h"

namespace ms
{
    class WindowsProcessRunner final : public IProcessRunner
    {
    public:
        bool Run(const std::string& executable,
                 const std::vector<std::string>& args,
                 std::string& out,
                 int& exitCode) const override;
    };
}
