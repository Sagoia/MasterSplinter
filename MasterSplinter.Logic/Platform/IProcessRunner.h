#pragma once
// IProcessRunner — the Bridge "implementor" (and the target of the platform Adapters).
//
// Portable abstraction for launching a child process and capturing its output. Concrete
// implementations adapt an OS-specific process API (Win32 CreateProcessW on Windows,
// Foundation's NSTask on macOS) to this one shape, so the portable GitBackend that depends on
// it never sees a line of platform code.
//
// KEEP PORTABLE: no <windows.h>, no platform headers. Builds unchanged on Windows and macOS.

#include <cstddef>
#include <functional>
#include <optional>
#include <string>
#include <utility>
#include <vector>

namespace ms
{
    // Everything a launch can be customized with. Grouped into one struct so adding a knob does
    // not change the virtual's signature again (Phase 6 added env + streaming to what was a
    // stdin-only parameter list).
    struct RunOptions
    {
        // Bytes to feed the child's stdin, after which stdin is closed (EOF) — used for e.g.
        // `git commit -F -`. std::nullopt wires stdin to the null device so the child can never
        // block waiting on input. Implementations must write stdin concurrently with draining
        // stdout (writer thread / dispatch queue) — writing first and reading after deadlocks
        // once either pipe buffer fills.
        std::optional<std::string> input;

        // Environment variables added to (or overriding) the ones this process already has. Used
        // by the network commands to set GIT_TERMINAL_PROMPT=0, so git fails with a readable
        // message instead of blocking on a credential prompt no GUI can answer. Empty => the
        // child simply inherits our environment.
        std::vector<std::pair<std::string, std::string>> env;

        // Live output sink. Called with each chunk of merged stdout/stderr as it arrives, AND
        // with (nullptr, 0) as a periodic heartbeat while the child runs — the heartbeat is what
        // makes cancellation work for a command that has stalled without printing anything (a
        // hung SSH handshake). Return false from any invocation to cancel: the implementation
        // terminates the child, and the call then completes normally with whatever exit status
        // that produced. An empty sink means "buffer only", the pre-Phase-6 behavior.
        //
        // Implementations may invoke this from more than one thread, but must serialize the
        // invocations so the sink itself never needs a lock.
        std::function<bool(const char*, std::size_t)> onOutput;
    };

    class IProcessRunner
    {
    public:
        virtual ~IProcessRunner() = default;

        // Launch `executable` (resolved via PATH) with `args`, capturing stdout and stderr
        // (merged) into `out` as raw UTF-8 bytes — binary-safe, may contain NUL bytes. `out`
        // accumulates the complete output even when `options.onOutput` is streaming it.
        // `exitCode` receives the process's exit status. Returns false only if the process
        // could not be started (mirrors the original RunGit contract exactly).
        virtual bool Run(const std::string& executable,
                         const std::vector<std::string>& args,
                         const RunOptions& options,
                         std::string& out,
                         int& exitCode) const = 0;

        // Convenience for the common no-stdin case; existing call sites keep this shape.
        // (Derived overrides hide these by name — implementations re-expose them with
        // `using IProcessRunner::Run;`.)
        bool Run(const std::string& executable,
                 const std::vector<std::string>& args,
                 std::string& out,
                 int& exitCode) const
        {
            return Run(executable, args, RunOptions{}, out, exitCode);
        }

        bool Run(const std::string& executable,
                 const std::vector<std::string>& args,
                 const std::optional<std::string>& input,
                 std::string& out,
                 int& exitCode) const
        {
            RunOptions options;
            options.input = input;
            return Run(executable, args, options, out, exitCode);
        }
    };
}
