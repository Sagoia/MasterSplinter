// GitBackend.cpp — the core of the Bridge abstraction: process plumbing and the terminal verbs
// every area file builds on. See GitBackend.h.
//
// The per-area git commands live in the sibling GitBackend.<Area>.cpp files (History, Diff,
// WorkTree, Refs, Remotes, Sequencer, Stash, Blame). They are the same class — GitBackend is
// stateless apart from the injected runner, so splitting by area needed no delegation layer and
// left the public API and the C ABI byte-identical.
//
// No <windows.h>: every process launch goes through the injected IProcessRunner, so the command
// building here is identical on Windows and macOS.
//
// KEEP PORTABLE. Compiled with PrecompiledHeader=NotUsing.

#include "GitBackend.h"
#include "GitText.h"

#include <string>
#include <vector>

namespace ms
{
    GitBackend::GitBackend(std::unique_ptr<IProcessRunner> runner)
        : runner_(std::move(runner))
    {
    }

    std::string GitBackend::RunGitC(const std::string& root, std::vector<std::string> args, int& code) const
    {
        return RunGitC(root, std::move(args), std::nullopt, code);
    }

    std::string GitBackend::RunGitC(const std::string& root, std::vector<std::string> args,
                                    const std::optional<std::string>& input, int& code) const
    {
        RunOptions options;
        options.input = input;
        return RunGitC(root, std::move(args), std::move(options), code);
    }

    std::string GitBackend::RunGitC(const std::string& root, std::vector<std::string> args,
                                    RunOptions options, int& code) const
    {
        std::vector<std::string> full = { "-C", root };
        full.insert(full.end(), args.begin(), args.end());
        std::string out;
        if (!runner_ || !runner_->Run("git", full, options, out, code))
        {
            code = -1;
            out.clear();
        }
        return out;
    }

    // ---- Terminal verbs ------------------------------------------------------------------------
    // These are what the methods below actually call. Each one owns the `int code;` out-param and
    // applies exactly one of the two return conventions, so no method has to restate it.

    std::string GitBackend::RunRaw(const std::string& root, GitArgs args) const
    {
        int code;
        return RunGitC(root, args.Take(), code);
    }

    std::string GitBackend::RunPathList(const std::string& root, GitArgs args) const
    {
        std::string out = RunRaw(root, std::move(args.Add("-z")));
        NulToRs(out);
        return out;
    }
    std::string GitBackend::RunRead(const std::string& root, GitArgs args) const
    {
        int code;
        std::string out = RunGitC(root, args.Take(), code);
        return code == 0 ? out : std::string();
    }

    std::string GitBackend::RunValue(const std::string& root, GitArgs args, bool& ok) const
    {
        int code;
        std::string out = RunGitC(root, args.Take(), code);
        TrimTrailingNewlines(out);
        ok = code == 0;
        return ok ? out : std::string();
    }

    std::string GitBackend::RunWrite(const std::string& root, GitArgs args, const char* fallback) const
    {
        return RunWrite(root, std::move(args), std::nullopt, fallback);
    }

    std::string GitBackend::RunWrite(const std::string& root, GitArgs args,
                                     const std::optional<std::string>& input,
                                     const char* fallback) const
    {
        int code;
        std::string out = RunGitC(root, args.Take(), input, code);
        return OkOrErr(std::move(out), code, fallback);
    }
}
