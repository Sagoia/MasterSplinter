#pragma once
// IProcessRunner — the Bridge "implementor" (and the target of the platform Adapters).
//
// Portable abstraction for launching a child process and capturing its output. Concrete
// implementations adapt an OS-specific process API (Win32 CreateProcessW on Windows,
// Foundation's NSTask on macOS) to this one shape, so the portable GitBackend that depends on
// it never sees a line of platform code.
//
// KEEP PORTABLE: no <windows.h>, no platform headers. Builds unchanged on Windows and macOS.

#include <string>
#include <vector>

namespace ms
{
    class IProcessRunner
    {
    public:
        virtual ~IProcessRunner() = default;

        // Launch `executable` (resolved via PATH) with `args`, capturing stdout and stderr
        // (merged) into `out` as raw UTF-8 bytes — binary-safe, may contain NUL bytes.
        // `exitCode` receives the process's exit status. Returns false only if the process
        // could not be started (mirrors the original RunGit contract exactly).
        virtual bool Run(const std::string& executable,
                         const std::vector<std::string>& args,
                         std::string& out,
                         int& exitCode) const = 0;
    };
}
