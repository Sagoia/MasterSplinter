#pragma once
// FakeProcessRunner — a test double for ms::IProcessRunner.
//
// This is what makes the Bridge unit-testable: instead of spawning git.exe, GitBackend is given
// this fake, which (a) RECORDS every Run() call — the executable and the exact argv GitBackend
// built — so tests can assert the command, and (b) returns SCRIPTED (out, exitCode) responses so
// tests can feed "what git would have printed" without any process or repository.

#include <cstddef>
#include <optional>
#include <string>
#include <utility>
#include <vector>

#include "Platform/IProcessRunner.h"

namespace mstest
{
    class FakeProcessRunner final : public ms::IProcessRunner
    {
    public:
        struct Call
        {
            std::string executable;
            std::vector<std::string> args;
            std::optional<std::string> input; // stdin payload, nullopt for the no-stdin overload
            std::vector<std::pair<std::string, std::string>> env; // RunOptions::env, as passed
            bool hadSink = false;             // whether a live-output sink was supplied
            bool sinkCancelled = false;       // whether that sink asked to cancel
        };

        struct Response
        {
            std::string out;
            int exitCode = 0;
        };

        // Recorded calls, in order. mutable because IProcessRunner::Run is const.
        mutable std::vector<Call> calls;

        // Scripted responses, consumed in order. When exhausted, Run yields ("", 0).
        std::vector<Response> responses;

        // When false, Run simulates "the process could not be started": returns false, out="",
        // exitCode=-1 (mirrors the real runner contract).
        bool processStarts = true;

        // Convenience for the common single-response case.
        void SetResponse(std::string out, int exitCode)
        {
            responses.clear();
            responses.push_back({ std::move(out), exitCode });
        }

        void AddResponse(std::string out, int exitCode)
        {
            responses.push_back({ std::move(out), exitCode });
        }

        // Exit code reported when a supplied sink asks to cancel. The real runners terminate the
        // child, so the command always ends non-zero; 1 mirrors WindowsProcessRunner.
        int cancelExitCode = 1;

        using ms::IProcessRunner::Run; // keep the convenience overloads visible

        bool Run(const std::string& executable,
                 const std::vector<std::string>& args,
                 const ms::RunOptions& options,
                 std::string& out,
                 int& exitCode) const override
        {
            calls.push_back({ executable, args, options.input, options.env,
                              static_cast<bool>(options.onOutput), false });
            Call& call = calls.back();

            if (!processStarts)
            {
                out.clear();
                exitCode = -1;
                return false;
            }

            const size_t i = calls.size() - 1; // this call's index
            if (i < responses.size())
            {
                out = responses[i].out;
                exitCode = responses[i].exitCode;
            }
            else
            {
                out.clear();
                exitCode = 0;
            }

            // Replay the scripted output through the sink exactly as a real runner would (one
            // chunk, then a heartbeat), so streaming and cancellation are testable without a
            // process. A sink that cancels ends the command non-zero, like a terminated child.
            if (options.onOutput)
            {
                bool keepGoing = out.empty() || options.onOutput(out.data(), out.size());
                if (keepGoing)
                    keepGoing = options.onOutput(nullptr, 0); // the heartbeat
                if (!keepGoing)
                {
                    call.sinkCancelled = true;
                    exitCode = cancelExitCode;
                }
            }
            return true;
        }

        // ---- Small assertion helpers used by the tests ----
        size_t CallCount() const { return calls.size(); }
        const std::vector<std::string>& ArgsOf(size_t callIndex) const { return calls.at(callIndex).args; }
        const std::optional<std::string>& InputOf(size_t callIndex) const { return calls.at(callIndex).input; }
        bool HadSink(size_t callIndex) const { return calls.at(callIndex).hadSink; }
        bool SinkCancelled(size_t callIndex) const { return calls.at(callIndex).sinkCancelled; }

        // Value of an environment override on call `callIndex`, or nullopt when it was not set.
        std::optional<std::string> EnvOf(size_t callIndex, const std::string& name) const
        {
            for (const auto& kv : calls.at(callIndex).env)
                if (kv.first == name)
                    return kv.second;
            return std::nullopt;
        }

        // True if `flag` appears anywhere in call `callIndex`'s argv.
        bool ArgsContain(size_t callIndex, const std::string& flag) const
        {
            const auto& a = calls.at(callIndex).args;
            for (const auto& s : a)
                if (s == flag)
                    return true;
            return false;
        }
    };
}
