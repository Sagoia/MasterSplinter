// GitBackend.cpp — portable git-command construction (the Bridge abstraction). See GitBackend.h.
//
// No <windows.h>: every process launch goes through the injected IProcessRunner, so the command
// building here is identical on Windows and macOS. The command lines below are unchanged from the
// original single-file GitBackend.cpp; only the process launch moved out to the platform runners.
//
// KEEP PORTABLE. Compiled with PrecompiledHeader=NotUsing.

#include "GitBackend.h"

#include <string>
#include <vector>

namespace ms
{
    namespace
    {
        // Field separator inside a record is 0x1F; records are separated by 0x1E (emitted directly
        // by git's --pretty=format below). These bytes never occur in normal commit text.
        constexpr char US = '\x1f';

        void TrimTrailingNewlines(std::string& s)
        {
            while (!s.empty() && (s.back() == '\n' || s.back() == '\r'))
                s.pop_back();
        }
    }

    GitBackend::GitBackend(std::unique_ptr<IProcessRunner> runner)
        : runner_(std::move(runner))
    {
    }

    std::string GitBackend::RunGitC(const std::string& root, std::vector<std::string> args, int& code) const
    {
        std::vector<std::string> full = { "-C", root };
        full.insert(full.end(), args.begin(), args.end());
        std::string out;
        if (!runner_ || !runner_->Run("git", full, out, code))
        {
            code = -1;
            out.clear();
        }
        return out;
    }

    bool GitBackend::IsRepository(const std::string& path) const
    {
        if (path.empty())
            return false;
        int code;
        std::string out = RunGitC(path, { "rev-parse", "--is-inside-work-tree" }, code);
        TrimTrailingNewlines(out);
        return code == 0 && out == "true";
    }

    std::string GitBackend::OpenRepository(const std::string& path) const
    {
        if (path.empty())
            return std::string("ERR") + US + "No folder was provided";

        int code;
        std::string top = RunGitC(path, { "rev-parse", "--show-toplevel" }, code);
        TrimTrailingNewlines(top);
        if (code != 0 || top.empty())
            return std::string("ERR") + US + "The selected folder is not a Git repository";

        int code2;
        std::string branch = RunGitC(path, { "rev-parse", "--abbrev-ref", "HEAD" }, code2);
        TrimTrailingNewlines(branch);
        if (branch == "HEAD") // detached HEAD -> show the short hash instead
        {
            int code3;
            std::string sh = RunGitC(path, { "rev-parse", "--short", "HEAD" }, code3);
            TrimTrailingNewlines(sh);
            branch = sh.empty() ? std::string("(detached)") : "(detached " + sh + ")";
        }
        else if (branch.empty())
        {
            branch = "(no commits yet)";
        }

        return std::string("OK") + US + top + US + branch;
    }

    std::string GitBackend::Log(const std::string& root, int order, int maxCount) const
    {
        if (root.empty())
            return std::string();

        const char* orderFlag = "--date-order";
        bool reverse = false;
        switch (order)
        {
        case 1: orderFlag = "--topo-order"; break;
        case 2: orderFlag = "--date-order"; reverse = true; break; // "Reverse Date Order"
        case 3: orderFlag = "--author-date-order"; break;
        default: orderFlag = "--date-order"; break;
        }

        // full %H, short %h, parents %P, author name/email/ISO date, committer name/email/ISO date,
        // ref decorations %D (for description badges), subject %s, body %b. Records end with RS.
        const std::string fmt =
            "--pretty=format:%H%x1f%h%x1f%P%x1f%an%x1f%ae%x1f%aI%x1f%cn%x1f%ce%x1f%cI%x1f%D%x1f%s%x1f%b%x1e";

        std::vector<std::string> args = { "log", "--all", "--parents", orderFlag };
        if (reverse)
            args.push_back("--reverse");
        if (maxCount > 0)
            args.push_back("-n" + std::to_string(maxCount));
        args.push_back(fmt);

        int code;
        return RunGitC(root, args, code);
    }

    std::string GitBackend::Refs(const std::string& root) const
    {
        if (root.empty())
            return std::string();
        // One ref per line (the C# side splits on newline). NOTE: do NOT use NUL separators here
        // or anywhere a char* is returned -- the managed marshaller stops at the first NUL byte.
        int code;
        return RunGitC(root,
            { "for-each-ref", "--format=%(refname)", "refs/heads", "refs/tags", "refs/remotes" },
            code);
    }

    std::string GitBackend::CommitFiles(const std::string& root, const std::string& sha) const
    {
        if (root.empty() || sha.empty())
            return std::string();
        // --first-parent -m: for merges, show changes vs the first parent (Phase 1 simple).
        // --root: the initial commit lists its files instead of being empty.
        // Output is line-based "status<TAB>path" (entries separated by newlines). We deliberately
        // do NOT use -z: a NUL-separated payload would be truncated at the first NUL by the managed
        // string marshaller. core.quotePath=false keeps non-ASCII paths literal.
        int code;
        return RunGitC(root,
            { "-c", "core.quotePath=false", "diff-tree", "--no-commit-id", "-r", "-M", "--root",
              "--first-parent", "-m", "--name-status", sha },
            code);
    }

    std::string GitBackend::CommitShortStat(const std::string& root, const std::string& sha) const
    {
        if (root.empty() || sha.empty())
            return std::string();
        int code;
        return RunGitC(root,
            { "diff-tree", "--shortstat", "-M", "--first-parent", "--root", "--no-commit-id", sha },
            code);
    }

    std::string GitBackend::FileDiff(const std::string& root, const std::string& sha,
                                     const std::string& path, int wsMode) const
    {
        if (root.empty() || sha.empty() || path.empty())
            return std::string();
        std::vector<std::string> args =
            { "diff-tree", "-p", "-M", "--first-parent", "--root", "--no-commit-id", "--no-color" };
        if (wsMode == 1) args.push_back("--ignore-space-change");
        else if (wsMode == 2) args.push_back("--ignore-all-space");
        args.push_back(sha);
        args.push_back("--");
        args.push_back(path);
        int code;
        return RunGitC(root, args, code);
    }

    std::string GitBackend::FileAtCommit(const std::string& root, const std::string& sha,
                                         const std::string& path) const
    {
        if (root.empty() || sha.empty() || path.empty())
            return std::string();
        int code;
        std::string out = RunGitC(root, { "show", sha + ":" + path }, code);
        if (code != 0)
            return std::string();
        return out;
    }

    // ---- Compare two commits / refs (a..b) -----------------------------------------------------

    std::string GitBackend::RangeFiles(const std::string& root, const std::string& a, const std::string& b) const
    {
        if (root.empty() || a.empty() || b.empty())
            return std::string();
        int code;
        return RunGitC(root,
            { "-c", "core.quotePath=false", "diff", "--name-status", "-M", a, b },
            code);
    }

    std::string GitBackend::RangeShortStat(const std::string& root, const std::string& a, const std::string& b) const
    {
        if (root.empty() || a.empty() || b.empty())
            return std::string();
        int code;
        return RunGitC(root, { "diff", "--shortstat", "-M", a, b }, code);
    }

    std::string GitBackend::RangeFileDiff(const std::string& root, const std::string& a, const std::string& b,
                                          const std::string& path, int wsMode) const
    {
        if (root.empty() || a.empty() || b.empty() || path.empty())
            return std::string();
        std::vector<std::string> args = { "diff", "-M", "--no-color" };
        if (wsMode == 1) args.push_back("--ignore-space-change");
        else if (wsMode == 2) args.push_back("--ignore-all-space");
        args.push_back(a);
        args.push_back(b);
        args.push_back("--");
        args.push_back(path);
        int code;
        return RunGitC(root, args, code);
    }

    // ---- Working tree (Phase 3) ----------------------------------------------------------------

    std::string GitBackend::Status(const std::string& root) const
    {
        if (root.empty())
            return std::string();
        // --no-optional-locks: a plain `git status` opportunistically rewrites .git/index, which
        // would re-trigger the app's own file watcher and loop forever.
        // -z: NUL-separated records, paths unquoted; a rename record is "XY new\0orig\0" (new path
        // first — reversed vs the human-readable format).
        int code;
        std::string out = RunGitC(root,
            { "--no-optional-locks", "-c", "core.quotePath=false", "status", "--porcelain=v1",
              "-z", "--untracked-files=all" },
            code);
        if (code != 0)
            return std::string();
        // A char* return is truncated at the first NUL by the managed marshaller, so translate
        // every NUL separator to the RS (0x1E) record separator the C# side already splits on.
        for (char& c : out)
        {
            if (c == '\0')
                c = '\x1e';
        }
        return out;
    }

    std::string GitBackend::WorkTreeFileDiff(const std::string& root, const std::string& path,
                                             int area, int wsMode) const
    {
        if (root.empty() || path.empty() || area < 0 || area > 2)
            return std::string();
        // --no-optional-locks: like `status`, a worktree `diff` opportunistically refreshes the
        // index stat cache (writing .git/index), which would re-trigger the app's file watcher.
        std::vector<std::string> args = { "--no-optional-locks", "diff" };
        if (area == 1)
            args.push_back("--cached"); // staged: index vs HEAD
        args.push_back("--no-color");
        if (wsMode == 1) args.push_back("--ignore-space-change");
        else if (wsMode == 2) args.push_back("--ignore-all-space");
        if (area == 2)
        {
            // Untracked: synthesize an all-added unified diff. Git (including Git for Windows)
            // special-cases the literal path /dev/null. NOTE: --no-index exits 1 when the files
            // differ — that is the success case here, so the exit code is deliberately ignored.
            args.push_back("--no-index");
            args.push_back("--");
            args.push_back("/dev/null");
        }
        else
        {
            args.push_back("--");
        }
        args.push_back(path);
        int code;
        return RunGitC(root, args, code);
    }

    std::optional<std::string> GitBackend::FileBytesAt(const std::string& root, const std::string& sha,
                                                       const std::string& path) const
    {
        if (root.empty() || sha.empty() || path.empty())
            return std::nullopt;
        int code;
        std::string out = RunGitC(root, { "show", sha + ":" + path }, code);
        if (code != 0)
            return std::nullopt;
        return out;
    }
}
