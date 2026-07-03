#pragma once
// FakeProcessRunner — a test double for ms::IProcessRunner.
//
// This is what makes the Bridge unit-testable: instead of spawning git.exe, GitBackend is given
// this fake, which (a) RECORDS every Run() call — the executable and the exact argv GitBackend
// built — so tests can assert the command, and (b) returns SCRIPTED (out, exitCode) responses so
// tests can feed "what git would have printed" without any process or repository.

#include <string>
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

        bool Run(const std::string& executable,
                 const std::vector<std::string>& args,
                 std::string& out,
                 int& exitCode) const override
        {
            calls.push_back({ executable, args });

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
            return true;
        }

        // ---- Small assertion helpers used by the tests ----
        size_t CallCount() const { return calls.size(); }
        const std::vector<std::string>& ArgsOf(size_t callIndex) const { return calls.at(callIndex).args; }

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
