#pragma once
// IProcessRunner — the Bridge "implementor" (and the target of the platform Adapters).
//
// Portable abstraction for launching a child process and capturing its output. Concrete
// implementations adapt an OS-specific process API (Win32 CreateProcessW on Windows,
// Foundation's NSTask on macOS) to this one shape, so the portable GitBackend that depends on
// it never sees a line of platform code.
//
// KEEP PORTABLE: no <windows.h>, no platform headers. Builds unchanged on Windows and macOS.

#include <optional>
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
        //
        // `input`: bytes to feed the child's stdin, after which stdin is closed (EOF) — used
        // for e.g. `git commit -F -`. std::nullopt wires stdin to the null device so the child
        // can never block waiting on input. Implementations must write stdin concurrently with
        // draining stdout (writer thread / dispatch queue) — writing first and reading after
        // deadlocks once either pipe buffer fills.
        virtual bool Run(const std::string& executable,
                         const std::vector<std::string>& args,
                         const std::optional<std::string>& input,
                         std::string& out,
                         int& exitCode) const = 0;

        // Convenience for the common no-stdin case; existing call sites keep this shape.
        // (Derived overrides hide this by name — implementations re-expose it with
        // `using IProcessRunner::Run;`.)
        bool Run(const std::string& executable,
                 const std::vector<std::string>& args,
                 std::string& out,
                 int& exitCode) const
        {
            return Run(executable, args, std::nullopt, out, exitCode);
        }
    };
}
