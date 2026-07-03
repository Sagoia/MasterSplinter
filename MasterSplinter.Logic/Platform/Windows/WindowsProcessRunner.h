#pragma once
// WindowsProcessRunner — the Windows Adapter.
//
// Adapts the Win32 process API (CreateProcessW + anonymous pipes, plus UTF-8<->UTF-16
// conversion and CommandLineToArgvW-compatible argument quoting) to IProcessRunner. The .cpp
// is the ONLY translation unit in the core that includes <windows.h> (via pch.h) — all the
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
