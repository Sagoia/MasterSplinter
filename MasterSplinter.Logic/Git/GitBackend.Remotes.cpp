// GitBackend — remotes: URL editing plus the three network commands.
//
// One area of GitBackend, split out of the original single 1300-line GitBackend.cpp.
// Same class, same public API — only the file boundary is new. Shared helpers live in
// GitText.h; the git argument builder in GitArgs.h.
//
// KEEP PORTABLE: no <windows.h>. Compiled with PrecompiledHeader=NotUsing.

#include "GitBackend.h"
#include "GitText.h"

namespace ms
{
    // ---- Remotes (Phase 6, REMOTE-001..009) ----------------------------------------------------

    std::string GitBackend::Remotes(const std::string& root) const
    {
        if (root.empty())
            return std::string();
        // -v prints two lines per remote ("<name>\t<url> (fetch)" / "(push)"). Tab/newline
        // delimited and NUL-free, so it crosses the FFI boundary intact; the pairing of the two
        // lines is parsing, which belongs in the host layer.
        return RunRead(root, { "remote", "-v" });
    }

    std::string GitBackend::SetRemoteUrl(const std::string& root, const std::string& name,
                                         const std::string& url, bool pushUrl) const
    {
        if (root.empty())
            return NoRoot();
        if (IsBlank(name))
            return Err("No remote name was provided");
        if (IsBlank(url))
            return Err("No URL was provided");
        // set-url, never add/remove: Phase 6 edits existing remotes only.
        return RunWrite(root, GitArgs{ "remote", "set-url" }
            .AddIf(pushUrl, "--push")
            .Add(name)
            .Add(url),
            "git remote set-url failed");
    }

    std::string GitBackend::RunNetworkCommand(const std::string& root, GitArgs args,
                                              const ProgressSink& progress, const char* fallback) const
    {
        RunOptions options;
        options.onOutput = progress;
        // A GUI process has no terminal for git to prompt on, so without this git can sit waiting
        // for a username that will never arrive. Set to 0 it fails immediately with a message the
        // host turns into an actionable hint (REMOTE-009). Credential helpers with their own UI
        // (Git Credential Manager) are unaffected — this only disables the *terminal* prompt.
        options.env.emplace_back("GIT_TERMINAL_PROMPT", "0");
        int code;
        std::string out = RunGitC(root, args.Take(), std::move(options), code);
        return OkOrErr(std::move(out), code, fallback);
    }

    std::string GitBackend::Fetch(const std::string& root, const std::string& remote,
                                  bool allRemotes, bool prune, bool tags,
                                  const ProgressSink& progress) const
    {
        if (root.empty())
            return NoRoot();
        if (!allRemotes && IsBlank(remote))
            return Err("No remote was provided");

        // --all and a remote name are mutually exclusive, so --all replaces the name rather than
        // joining it.
        GitArgs args{ "fetch", "--progress" };
        if (allRemotes)
            args.Add("--all");
        else
            args.Add(remote);
        return RunNetworkCommand(root, args.AddIf(prune, "--prune").AddIf(tags, "--tags"),
                                 progress, "git fetch failed");
    }

    std::string GitBackend::Pull(const std::string& root, const std::string& remote,
                                 const std::string& branch, const ProgressSink& progress) const
    {
        if (root.empty())
            return NoRoot();
        // --ff-only is the whole point (REMOTE-004): if the branch has diverged, git refuses and
        // its refusal is the message the user sees, rather than this app silently merging or
        // rebasing. Remote and branch are optional -- omitting both pulls from the branch's
        // configured upstream, which is git's own default.
        GitArgs args{ "pull", "--ff-only", "--progress" };
        if (!IsBlank(remote) && !IsBlank(branch))
            args.Add(remote).Add(branch);
        return RunNetworkCommand(root, std::move(args), progress, "git pull failed");
    }

    std::string GitBackend::Push(const std::string& root, const std::string& remote,
                                 const std::string& branch, bool setUpstream, bool pushTags,
                                 const ProgressSink& progress) const
    {
        if (root.empty())
            return NoRoot();
        if (IsBlank(remote))
            return Err("No remote was provided");
        if (IsBlank(branch))
            return Err("No branch was provided");
        // Never --force / --force-with-lease: a rejected non-fast-forward push must surface as
        // git's own refusal so the user fetches first, not be quietly overridden.
        return RunNetworkCommand(root, GitArgs{ "push", "--progress" }
            .AddIf(setUpstream, "--set-upstream") // REMOTE-006: publishes the branch with tracking
            .AddIf(pushTags, "--tags")
            .Add(remote)
            .Add(branch),
            progress, "git push failed");
    }

}
