// GitBackend — commit file lists, diffs, and two-commit comparison.
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
    std::string GitBackend::CommitFiles(const std::string& root, const std::string& sha) const
    {
        if (root.empty() || sha.empty())
            return std::string();
        // --root: the initial commit lists its files instead of being empty.
        // FirstParentMerges + RunPathList carry the merge and -z rules; see their declarations.
        return RunPathList(root, GitArgs{}
            .QuotePathOff()
            .Add({ "diff-tree", "--no-commit-id", "-r", "-M", "--root",
                   "--first-parent", "--name-status" })
            .FirstParentMerges()
            .Add(sha));
    }

    std::string GitBackend::CommitShortStat(const std::string& root, const std::string& sha) const
    {
        if (root.empty() || sha.empty())
            return std::string();
        return RunRaw(root, GitArgs{ "diff-tree", "--shortstat", "-M", "--first-parent",
                                     "--root", "--no-commit-id" }
            .FirstParentMerges()
            .Add(sha));
    }

    std::string GitBackend::FileDiff(const std::string& root, const std::string& sha,
                                     const std::string& path, int wsMode) const
    {
        if (root.empty() || sha.empty() || path.empty())
            return std::string();
        return RunRaw(root, GitArgs{ "diff-tree", "-p", "-M", "--first-parent", "--root",
                                     "--no-commit-id", "--no-color" }
            .FirstParentMerges()
            .Whitespace(wsMode)
            .Add(sha)
            .Path(path));
    }

    std::string GitBackend::FileAtCommit(const std::string& root, const std::string& sha,
                                         const std::string& path) const
    {
        if (root.empty() || sha.empty() || path.empty())
            return std::string();
        return RunRead(root, GitArgs{ "show" }.Add(sha + ":" + path));
    }

    // ---- Compare two commits / refs (a..b) -----------------------------------------------------

    std::string GitBackend::RangeFiles(const std::string& root, const std::string& a, const std::string& b) const
    {
        if (root.empty() || a.empty() || b.empty())
            return std::string();
        return RunPathList(root, GitArgs{}
            .QuotePathOff()
            .Add({ "diff", "--name-status", "-M" })
            .Add(a)
            .Add(b));
    }

    std::string GitBackend::RangeShortStat(const std::string& root, const std::string& a, const std::string& b) const
    {
        if (root.empty() || a.empty() || b.empty())
            return std::string();
        return RunRaw(root, GitArgs{ "diff", "--shortstat", "-M" }.Add(a).Add(b));
    }

    std::string GitBackend::RangeFileDiff(const std::string& root, const std::string& a, const std::string& b,
                                          const std::string& path, int wsMode) const
    {
        if (root.empty() || a.empty() || b.empty() || path.empty())
            return std::string();
        return RunRaw(root, GitArgs{ "diff", "-M", "--no-color" }
            .Whitespace(wsMode)
            .Add(a)
            .Add(b)
            .Path(path));
    }

    std::optional<std::string> GitBackend::FileBytesAt(const std::string& root, const std::string& sha,
                                                       const std::string& path) const
    {
        if (root.empty() || sha.empty() || path.empty())
            return std::nullopt;
        int code;
        std::string out = RunGitC(root, GitArgs{ "show" }.Add(sha + ":" + path).Take(), code);
        if (code != 0)
            return std::nullopt;
        return out;
    }

    std::string GitBackend::AheadBehind(const std::string& root, const std::string& a,
                                        const std::string& b) const
    {
        if (root.empty() || a.empty() || b.empty())
            return std::string();
        // "<left>\t<right>" for the symmetric difference: left = commits only in a, right = only
        // in b. The three-dot form is required; two dots would report just one side. This is a
        // read op (empty on failure) because it decorates the compare banner rather than acting.
        bool ok = false;
        return RunValue(root, GitArgs{ "rev-list", "--left-right", "--count" }.Add(a + "..." + b), ok);
    }

}
