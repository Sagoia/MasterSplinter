// GitBackend — working-tree status, worktree diffs, staging and commit.
//
// One area of GitBackend, split out of the original single 1300-line GitBackend.cpp.
// Same class, same public API — only the file boundary is new. Shared helpers live in
// GitText.h; the git argument builder in GitArgs.h.
//
// KEEP PORTABLE: no <windows.h>. Compiled with PrecompiledHeader=NotUsing.

#include "GitBackend.h"
#include "GitText.h"

#include "../Parse/DiffParser.h"

namespace ms
{
    // ---- Working tree (Phase 3) ----------------------------------------------------------------

    std::string GitBackend::Status(const std::string& root) const
    {
        if (root.empty())
            return std::string();
        // NO --no-optional-locks, deliberately. It suppresses git's opportunistic rewrite of
        // .git/index — which sounds like it protects the app's file watcher, but measured on a
        // 3000-file repo it costs ~4.6x on every call (629/835/688/440 ms with the flag vs
        // 687/120/144/153 ms without): the flag stops git PERSISTING the refreshed stat cache,
        // so every call redoes the whole refresh.
        //
        // The watcher loop it was defending against does not occur. Verified by watching the
        // index mtime across repeated runs: git rewrites .git/index ONCE after the worktree
        // changes and then leaves it alone — a single event the 500 ms debounce absorbs.
        //
        // -z: NUL-separated records, paths unquoted; a rename record is "XY new\0orig\0" (new path
        // first — reversed vs the human-readable format).
        //
        // NOT RunPathList, deliberately: that verb is RunRaw-based (exit code ignored), and a
        // failed `status` must yield "" rather than a half-payload the host would render as a
        // clean tree. Porcelain also packs "XY <path>" into ONE token, where --name-status emits
        // the status and each path separately — different record shape, same NUL translation.
        std::string out = RunRead(root, GitArgs{}
            .QuotePathOff()
            .Add({ "status", "--porcelain=v1", "-z", "--untracked-files=all" }));
        NulToRs(out);
        return out;
    }

    std::string GitBackend::WorkTreeFileDiff(const std::string& root, const std::string& path,
                                             int area, int wsMode) const
    {
        if (root.empty() || path.empty() || area < 0 || area > 2)
            return std::string();
        // No --no-optional-locks here either, for the reason on Status above: a worktree diff
        // refreshes the same stat cache, and suppressing the write makes every call redo it.
        GitArgs args;
        args.Add("diff")
            .AddIf(area == 1, "--cached")   // staged: index vs HEAD
            .Add("--no-color")
            .Whitespace(wsMode);
        if (area == 2)
        {
            // Untracked: synthesize an all-added unified diff. Git (including Git for Windows)
            // special-cases the literal path /dev/null. NOTE: --no-index exits 1 when the files
            // differ — that is the success case here, so RunRaw (exit code ignored) is required.
            args.Add("--no-index").Separator().Add("/dev/null");
        }
        else
        {
            args.Separator();
        }
        return parse::ParseUnifiedDiff(RunRaw(root, args.Add(path)));
    }

    // ---- Write operations (Phase 4, COMMIT-001..007) -------------------------------------------
    // NOTE: no --no-optional-locks here — that flag suppresses *opportunistic* index writes on
    // read commands; these commands write the index/HEAD on purpose, and the app's file watcher
    // reacting to that is the desired refresh signal.

    std::string GitBackend::StagePaths(const std::string& root, const std::vector<std::string>& paths) const
    {
        if (root.empty())
            return NoRoot();
        if (paths.empty())
            return Err("No paths were provided");
        // -A scoped by pathspec stages modifications, deletions, and untracked files alike, so
        // one command covers every row type the status view shows.
        return RunWrite(root, GitArgs{ "add", "-A" }.Paths(paths), "git add failed");
    }

    std::string GitBackend::StageAll(const std::string& root) const
    {
        if (root.empty())
            return NoRoot();
        return RunWrite(root, { "add", "-A" }, "git add failed");
    }

    std::string GitBackend::UnstagePaths(const std::string& root, const std::vector<std::string>& paths) const
    {
        if (root.empty())
            return NoRoot();
        if (paths.empty())
            return Err("No paths were provided");
        // `restore --staged` resolves HEAD, which does not exist yet in a freshly-init'd repo
        // (unborn branch) — there, dropping the index entries via `rm --cached` is the equivalent.
        bool hasHead = false;
        RunValue(root, { "rev-parse", "--verify", "--quiet", "HEAD" }, hasHead);
        GitArgs args = hasHead
            ? GitArgs{ "restore", "--staged" }
            : GitArgs{ "rm", "-r", "--cached", "-q" };
        return RunWrite(root, args.Paths(paths), "git unstage failed");
    }

    std::string GitBackend::DiscardPaths(const std::string& root, const std::vector<std::string>& paths) const
    {
        if (root.empty())
            return NoRoot();
        if (paths.empty())
            return Err("No paths were provided");
        // Restore from the index (the default source), so staged content is never touched and
        // this works on an unborn branch too.
        return RunWrite(root, GitArgs{ "restore" }.Paths(paths), "git restore failed");
    }

    std::string GitBackend::Commit(const std::string& root, const std::string& message, bool amend) const
    {
        if (root.empty())
            return NoRoot();
        if (IsBlank(message))
            return Err("Commit message is empty");
        // The message travels via stdin (`-F -`): immune to command-line length limits and
        // quoting edge cases. --cleanup=strip pins message post-processing regardless of the
        // user's commit.cleanup config, so Subject+blank+Body round-trips predictably.
        return RunWrite(root, GitArgs{ "commit" }
            .AddIf(amend, "--amend")
            .Add({ "--cleanup=strip", "-F", "-" }),
            message, "git commit failed");
    }

    std::string GitBackend::HeadMessage(const std::string& root) const
    {
        if (root.empty())
            return NoRoot();
        int code;
        std::string out = RunGitC(root, { "log", "-1", "--pretty=format:%s%x1f%b" }, code);
        if (code != 0)
        {
            TrimTrailingNewlines(out);
            return Err(out.empty() ? std::string("There is no commit yet") : std::move(out));
        }
        TrimTrailingNewlines(out);
        return std::string("OK") + US + out; // OK US subject US body
    }

}
