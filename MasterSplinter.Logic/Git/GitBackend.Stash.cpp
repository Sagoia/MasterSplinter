// GitBackend — the stash.
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
    // ---- Stash, blame, search, reflog (Phase 8) ------------------------------------------------

    namespace
    {
        // A stash entry is addressed as stash@{N} — a reflog selector, not a ref name. Accepting
        // only that exact shape (or empty, meaning "the most recent") keeps anything the user or a
        // stale UI row could carry from reaching git's argv as a revision expression.
        bool IsStashRef(const std::string& s)
        {
            if (s.empty())
                return true; // empty = stash@{0}, git's own default
            const std::string prefix = "stash@{";
            if (s.size() < prefix.size() + 2 || s.compare(0, prefix.size(), prefix) != 0)
                return false;
            if (s.back() != '}')
                return false;
            std::size_t digits = s.size() - prefix.size() - 1;
            if (digits == 0)
                return false;
            for (std::size_t i = prefix.size(); i < s.size() - 1; ++i)
            {
                if (s[i] < '0' || s[i] > '9')
                    return false;
            }
            return true;
        }

        // LooksLikeOption moved to GitText.h — Blame, SearchLog and Reflog all need it now that
        // they live in different files.
    }

    std::string GitBackend::StashList(const std::string& root) const
    {
        if (root.empty())
            return std::string();
        // `git stash list` is a reflog walk over refs/stash, so it is log-family: %x1f escapes
        // apply (NOT for-each-ref's %1f). %gd is the selector ("stash@{0}"), %gs the reflog
        // subject, which is exactly the stash message git composed or the user supplied.
        // An empty stash prints nothing and exits 0 — naturally an empty list.
        // RunRead (not RunRaw): git writes its diagnostics to the same merged stream as its
        // records, so without the exit-code check a failure would be handed to the record parser
        // as if it were data.
        return RunRead(root, { "stash", "list",
                               "--format=%gd%x1f%H%x1f%h%x1f%gs%x1f%aI%x1f%an%x1e" });
    }

    std::string GitBackend::StashSave(const std::string& root, const std::string& message,
                                      bool includeUntracked, bool keepIndex) const
    {
        if (root.empty())
            return NoRoot();

        // The stash's own commit, or empty when refs/stash does not exist yet. Comparing it before
        // and after is how "stashed nothing" is told from "stashed something": `git stash push`
        // exits 0 either way, and matching its "No local changes to save" text would break under
        // any non-English locale.
        auto stashTip = [this, &root]()
        {
            bool ok = false;
            return RunValue(root, { "rev-parse", "--verify", "--quiet", "refs/stash" }, ok);
        };

        const std::string before = stashTip();

        GitArgs args{ "stash", "push" };
        args.AddIf(includeUntracked, "--include-untracked")
            .AddIf(keepIndex, "--keep-index");
        if (!IsBlank(message))
            args.Add("-m").Add(message);

        std::string pushed = RunWrite(root, std::move(args), "git stash failed");
        if (pushed != "OK")
            return pushed;

        // Exit 0 with an unchanged refs/stash means git had nothing to stash. Reporting that is
        // the difference between "your work is parked" and "your work is still sitting here".
        if (stashTip() == before)
            return Err("There were no local changes to save.");
        return "OK";
    }

    std::string GitBackend::StashApply(const std::string& root, const std::string& ref) const
    {
        return RunStashCommand(root, "apply", ref, "git stash apply failed");
    }

    std::string GitBackend::StashPop(const std::string& root, const std::string& ref) const
    {
        return RunStashCommand(root, "pop", ref, "git stash pop failed");
    }

    std::string GitBackend::StashDrop(const std::string& root, const std::string& ref) const
    {
        return RunStashCommand(root, "drop", ref, "git stash drop failed");
    }

    std::string GitBackend::RunStashCommand(const std::string& root, const char* action,
                                            const std::string& ref, const char* fallback) const
    {
        if (root.empty())
            return NoRoot();
        if (!IsStashRef(ref))
            return Err("Not a stash entry: " + ref);

        // A conflicting apply/pop exits non-zero with the markers already written; that reaches the
        // caller as ERR carrying git's own text, which the host turns into a conflict hint.
        return RunWrite(root, GitArgs{ "stash" }
            .Add(action)
            .AddIf(!ref.empty(), ref),
            fallback);
    }

}
