// GitBackend — repository open, commit log, refs, search and reflog.
//
// One area of GitBackend, split out of the original single 1300-line GitBackend.cpp.
// Same class, same public API — only the file boundary is new. Shared helpers live in
// GitText.h; the git argument builder in GitArgs.h.
//
// KEEP PORTABLE: no <windows.h>. Compiled with PrecompiledHeader=NotUsing.

#include "GitBackend.h"
#include "GitText.h"

#include "../Parse/LogParser.h"
#include "../Parse/RefParser.h"
#include "GitLogFormat.h"

namespace ms
{
    bool GitBackend::IsRepository(const std::string& path) const
    {
        if (path.empty())
            return false;
        bool ok = false;
        return RunValue(path, { "rev-parse", "--is-inside-work-tree" }, ok) == "true" && ok;
    }

    std::string GitBackend::OpenRepository(const std::string& path) const
    {
        if (path.empty())
            return Err("No folder was provided");

        bool ok = false;
        std::string top = RunValue(path, { "rev-parse", "--show-toplevel" }, ok);
        if (!ok || top.empty())
            return Err("The selected folder is not a Git repository");

        bool branchOk = false;
        std::string branch = RunValue(path, { "rev-parse", "--abbrev-ref", "HEAD" }, branchOk);
        if (!branchOk)
        {
            // Unborn branch (a fresh repo with no commits): rev-parse exits non-zero and its
            // merged output is git's "ambiguous argument 'HEAD'" text, which would otherwise be
            // displayed verbatim as the branch name. symbolic-ref still knows the branch.
            bool symOk = false;
            std::string sym = RunValue(path, { "symbolic-ref", "--short", "-q", "HEAD" }, symOk);
            branch = (symOk && !sym.empty())
                ? sym + " (no commits yet)"
                : std::string("(no commits yet)");
        }
        else if (branch == "HEAD") // detached HEAD -> show the short hash instead
        {
            bool shortOk = false;
            std::string sh = RunValue(path, { "rev-parse", "--short", "HEAD" }, shortOk);
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
            return parse::ParseLogRecords("");

        bool reverse = false;
        const char* orderFlag = LogOrderFlag(order, reverse);

        GitArgs args{ "log", "--all", "--parents" };
        args.Add(orderFlag)
            .AddIf(reverse, "--reverse")
            .AddIf(maxCount > 0, "-n" + std::to_string(maxCount));
        AddLogRecordFlags(args);

        // Exit code deliberately ignored: the field-count floor in Parse/LogParser drops anything
        // that is not a well-formed record, so git's error text can never reach the commit list.
        return parse::ParseLogRecords(RunRaw(root, std::move(args)));
    }

    std::string GitBackend::RefDetails(const std::string& root) const
    {
        if (root.empty())
            return parse::ParseRefDetails("");
        // Eight fixed fields per ref, US-separated, NUL-terminated (git adds a newline after each
        // record, which the parser trims). The field COUNT is constant even when several fields
        // are empty, so positional parsing stays stable.
        //
        // GOTCHA: for-each-ref escapes are "%xx" (two hex digits) -- NOT log --pretty's "%xNN".
        // Writing %x1f here emits the literal text "%x1f". The exact-argv gtest pins this.
        //
        // %(upstream:track,nobracket) is preferred over %(ahead-behind:<committish>): the latter
        // needs git 2.41 and measures every ref against ONE committish, whereas the sidebar wants
        // each branch against its own upstream. Per-branch rev-list would be N spawns per refresh.
        const std::string fmt =
            "--format=%(refname)%1f%(objectname)%1f%(*objectname)%1f%(objecttype)%1f"
            "%(upstream:short)%1f%(upstream:track,nobracket)%1f%(HEAD)%1f%(symref)%00";
        return parse::ParseRefDetails(RunRaw(root, GitArgs{ "for-each-ref", "--sort=refname" }
            .Add(fmt)
            .Add({ "refs/heads", "refs/tags", "refs/remotes" })));
    }

    std::string GitBackend::SearchLog(const std::string& root, const std::string& mode,
                                      const std::string& query, const std::string& pathFilter,
                                      int order, int maxCount, bool matchCase, bool useRegex,
                                      bool allBranches) const
    {
        // Every exit is a well-formed (possibly empty) packed buffer, so the host reads one shape.
        if (root.empty())
            return parse::ParseLogRecords("");

        const std::vector<std::string> modes = { "message", "author", "content", "path", "hash" };
        if (!Contains(modes, mode))
            return parse::ParseLogRecords("");

        const bool hasQuery = !IsBlank(query);
        const bool hasPath = !IsBlank(pathFilter);
        // Nothing to search for: an unfiltered `git log` here would look like a successful search
        // that happened to match everything.
        if (!hasQuery && !hasPath)
            return parse::ParseLogRecords("");

        // A hash is resolved, not matched: verify it names a commit first so a typo comes back as
        // an empty result rather than git's "unknown revision" text rendered as a commit list.
        if (mode == "hash")
        {
            if (!hasQuery || LooksLikeOption(query))
                return parse::ParseLogRecords("");
            bool resolved = false;
            std::string sha = RunValue(root,
                GitArgs{ "rev-parse", "--verify", "--quiet" }.Add(query + "^{commit}"), resolved);
            if (!resolved || sha.empty())
                return parse::ParseLogRecords("");

            GitArgs one{ "log", "--parents", "-n1" };
            AddLogRecordFlags(one).Add(sha);
            return parse::ParseLogRecords(RunRead(root, std::move(one)));
        }

        bool reverse = false;
        const char* orderFlag = LogOrderFlag(order, reverse);

        GitArgs args{ "log", "--parents" };
        args.Add(orderFlag)
            .AddIf(allBranches, "--all")
            .AddIf(reverse, "--reverse")
            .AddIf(maxCount > 0, "-n" + std::to_string(maxCount));

        // ONE predicate per mode. git ANDs --grep with --author rather than ORing them, so a single
        // box claiming to search "message or author" would quietly return the intersection; making
        // the field an explicit choice is the only honest way to spend one spawn.
        if (hasQuery && mode == "message")
        {
            args.AddIf(!useRegex, "--fixed-strings").Add("--grep=" + query);
        }
        else if (hasQuery && mode == "author")
        {
            args.Add("--author=" + query);
        }
        else if (hasQuery && mode == "content")
        {
            // -S counts occurrences (did this string appear or disappear); -G matches the diff text
            // itself, which is what a regex over a change means.
            args.Add((useRegex ? "-G" : "-S") + query);
        }

        args.AddIf(!matchCase, "--regexp-ignore-case");
        AddLogRecordFlags(args)
            .Separator()
            // In "path" mode the query IS the pathspec (SEARCH-002); any mode may additionally be
            // narrowed by an explicit path filter.
            .AddIf(mode == "path" && hasQuery, query)
            .AddIf(hasPath, pathFilter);

        // A rejected pattern (bad regex, unknown pathspec magic) must read as "no results", not as
        // git's complaint fed to the record parser — hence RunRead, not RunRaw.
        return parse::ParseLogRecords(RunRead(root, std::move(args)));
    }

    std::string GitBackend::Reflog(const std::string& root, const std::string& ref,
                                   int maxCount) const
    {
        if (root.empty())
            return parse::ParseReflog("");
        if (LooksLikeOption(ref))
            return parse::ParseReflog("");

        // %gd selector, %H/%h the commit, %at/%aI the author time and its offset, %an the author,
        // %s the commit subject, %gs the reflog subject ("commit: <subject>", "pull: Fast-forward")
        // -- which is often terser than the commit's own.
        //
        // -z, and the two free-form fields LAST: a reflog subject is user text and can contain
        // 0x1F. NUL separation makes a record boundary unmissable, and the bounded field split
        // keeps any stray separator inside %gs. %s is the one field a 0x1F could still shift into
        // %gs; the damage is then one row, never the stream.
        //
        // NOT --date=format:%z (which the commit log uses): that option also rewrites %gd, so the
        // selector "HEAD@{0}" comes back as "HEAD@{+0700}". The offset is read off %aI instead.
        //
        // A ref with no reflog makes git exit non-zero; the ""-on-error path already degrades to an
        // empty list, so no filesystem probing is needed to keep that quiet.
        // Checked, not assumed (RunRead): "not a valid ref" is the ORDINARY answer for refs/stash
        // in a repo that has never stashed, and it arrives on the same merged stream as the
        // records would.
        return parse::ParseReflog(RunRead(root, GitArgs{ "reflog", "show", "-z",
                                      "--format=%gd%x1f%H%x1f%h%x1f%at%x1f%aI%x1f%an%x1f%s%x1f%gs" }
            .AddIf(maxCount > 0, "-n" + std::to_string(maxCount))
            .Add(ref.empty() ? "HEAD" : ref)));
    }
}
