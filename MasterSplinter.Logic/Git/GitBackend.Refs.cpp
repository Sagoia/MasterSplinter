// GitBackend — branch and tag operations.
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
    // ---- Branches & tags (Phase 5, BR-001..007 / TAG-001..003) ---------------------------------

    std::string GitBackend::Checkout(const std::string& root, const std::string& refName,
                                     bool detach) const
    {
        if (root.empty())
            return NoRoot();
        if (IsBlank(refName))
            return Err("No branch or commit was provided");
        // `switch` rather than `checkout`: it only ever takes a ref, so a branch name can never be
        // mistaken for a pathspec. Deliberately NOT forced -- git carries uncommitted changes
        // across when it safely can and refuses otherwise, and that refusal is what the UI shows.
        return RunWrite(root, GitArgs{ "switch" }
            .AddIf(detach, "--detach")
            .Add(refName),
            "git switch failed");
    }

    std::string GitBackend::CreateBranch(const std::string& root, const std::string& name,
                                         const std::string& startPoint, bool checkout) const
    {
        if (root.empty())
            return NoRoot();
        if (IsBlank(name))
            return Err("No branch name was provided");
        // `switch -c` creates and checks out atomically, and sets the upstream automatically when
        // the start point is a remote-tracking ref. Never -C/-B: an existing name must fail loudly.
        // An empty start point means "from HEAD" -- git's own default when the argument is omitted.
        GitArgs args = checkout
            ? GitArgs{ "switch", "-c" }.Add(name)
            : GitArgs{ "branch" }.Add(name);
        return RunWrite(root, args.AddIf(!startPoint.empty(), startPoint), "git branch failed");
    }

    std::string GitBackend::DeleteBranch(const std::string& root, const std::string& name,
                                         bool force) const
    {
        if (root.empty())
            return NoRoot();
        if (IsBlank(name))
            return Err("No branch name was provided");
        // BR-005: callers must attempt force=false first, so an unmerged branch can only be
        // dropped after git has refused once and the user has confirmed a second time.
        return RunWrite(root, GitArgs{ "branch" }
            .Add(force ? "-D" : "-d")
            .Add(name),
            "git branch failed");
    }

    std::string GitBackend::RenameBranch(const std::string& root, const std::string& oldName,
                                         const std::string& newName) const
    {
        if (root.empty())
            return NoRoot();
        if (IsBlank(oldName) || IsBlank(newName))
            return Err("No branch name was provided");
        // -m, never -M: renaming onto an existing branch must fail instead of clobbering it.
        // Renaming the current branch is fine -- git rewrites .git/HEAD itself.
        return RunWrite(root, GitArgs{ "branch", "-m" }.Add(oldName).Add(newName),
                        "git branch failed");
    }

    std::string GitBackend::CreateTag(const std::string& root, const std::string& name,
                                      const std::string& commitish, const std::string& message) const
    {
        if (root.empty())
            return NoRoot();
        if (IsBlank(name))
            return Err("No tag name was provided");
        // A blank message means a lightweight tag (TAG-002); any real message makes it annotated,
        // fed through stdin like `commit -F -` so multi-line text and quoting are non-issues.
        // No --cleanup=: git has no tag.cleanup config, so the default is already deterministic.
        const bool annotated = !IsBlank(message);
        GitArgs args{ "tag" };
        if (annotated)
            args.Add({ "-a", "-F", "-" });
        args.Add(name).AddIf(!commitish.empty(), commitish);
        return RunWrite(root, std::move(args),
                        annotated ? std::optional<std::string>(message) : std::nullopt,
                        "git tag failed");
    }

    std::string GitBackend::DeleteTag(const std::string& root, const std::string& name) const
    {
        if (root.empty())
            return NoRoot();
        if (IsBlank(name))
            return Err("No tag name was provided");
        return RunWrite(root, GitArgs{ "tag", "-d" }.Add(name), "git tag failed");
    }

}
