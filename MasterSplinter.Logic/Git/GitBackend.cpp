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
        if (code2 != 0)
        {
            // Unborn branch (a fresh repo with no commits): rev-parse exits non-zero and its
            // merged output is git's "ambiguous argument 'HEAD'" text, which would otherwise be
            // displayed verbatim as the branch name. symbolic-ref still knows the branch.
            int code3;
            std::string sym = RunGitC(path, { "symbolic-ref", "--short", "-q", "HEAD" }, code3);
            TrimTrailingNewlines(sym);
            branch = (code3 == 0 && !sym.empty())
                ? sym + " (no commits yet)"
                : std::string("(no commits yet)");
        }
        else if (branch == "HEAD") // detached HEAD -> show the short hash instead
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

    std::string GitBackend::RefDetails(const std::string& root) const
    {
        if (root.empty())
            return std::string();
        // Eight fixed fields per ref, US-separated, RS-terminated (git adds a newline after each
        // record, which the C# splitter trims). The field COUNT is constant even when several
        // fields are empty, so positional parsing stays stable.
        //
        // GOTCHA: for-each-ref escapes are "%xx" (two hex digits) -- NOT log --pretty's "%xNN".
        // Writing %x1f here emits the literal text "%x1f". The exact-argv gtest pins this.
        //
        // %(upstream:track,nobracket) is preferred over %(ahead-behind:<committish>): the latter
        // needs git 2.41 and measures every ref against ONE committish, whereas the sidebar wants
        // each branch against its own upstream. Per-branch rev-list would be N spawns per refresh.
        const std::string fmt =
            "--format=%(refname)%1f%(objectname)%1f%(*objectname)%1f%(objecttype)%1f"
            "%(upstream:short)%1f%(upstream:track,nobracket)%1f%(HEAD)%1f%(symref)%1e";
        int code;
        return RunGitC(root,
            { "for-each-ref", "--sort=refname", fmt, "refs/heads", "refs/tags", "refs/remotes" },
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

    // ---- Write operations (Phase 4, COMMIT-001..007) -------------------------------------------
    // NOTE: no --no-optional-locks here — that flag suppresses *opportunistic* index writes on
    // read commands; these commands write the index/HEAD on purpose, and the app's file watcher
    // reacting to that is the desired refresh signal.

    namespace
    {
        std::string Err(std::string message)
        {
            return std::string("ERR") + US + std::move(message);
        }

        // Map a finished git command to the "OK" / "ERR<US>message" contract.
        std::string OkOrErr(std::string out, int code, const char* fallback)
        {
            if (code == 0)
                return "OK";
            TrimTrailingNewlines(out);
            return Err(out.empty() ? std::string(fallback) : std::move(out));
        }

        bool IsBlank(const std::string& s)
        {
            return s.find_first_not_of(" \t\r\n") == std::string::npos;
        }
    }

    std::string GitBackend::StagePaths(const std::string& root, const std::vector<std::string>& paths) const
    {
        if (root.empty())
            return Err("No repository root was provided");
        if (paths.empty())
            return Err("No paths were provided");
        // -A scoped by pathspec stages modifications, deletions, and untracked files alike, so
        // one command covers every row type the status view shows.
        std::vector<std::string> args = { "add", "-A", "--" };
        args.insert(args.end(), paths.begin(), paths.end());
        int code;
        std::string out = RunGitC(root, args, code);
        return OkOrErr(std::move(out), code, "git add failed");
    }

    std::string GitBackend::StageAll(const std::string& root) const
    {
        if (root.empty())
            return Err("No repository root was provided");
        int code;
        std::string out = RunGitC(root, { "add", "-A" }, code);
        return OkOrErr(std::move(out), code, "git add failed");
    }

    std::string GitBackend::UnstagePaths(const std::string& root, const std::vector<std::string>& paths) const
    {
        if (root.empty())
            return Err("No repository root was provided");
        if (paths.empty())
            return Err("No paths were provided");
        // `restore --staged` resolves HEAD, which does not exist yet in a freshly-init'd repo
        // (unborn branch) — there, dropping the index entries via `rm --cached` is the equivalent.
        int probeCode;
        RunGitC(root, { "rev-parse", "--verify", "--quiet", "HEAD" }, probeCode);
        std::vector<std::string> args = probeCode == 0
            ? std::vector<std::string>{ "restore", "--staged", "--" }
            : std::vector<std::string>{ "rm", "-r", "--cached", "-q", "--" };
        args.insert(args.end(), paths.begin(), paths.end());
        int code;
        std::string out = RunGitC(root, args, code);
        return OkOrErr(std::move(out), code, "git unstage failed");
    }

    std::string GitBackend::DiscardPaths(const std::string& root, const std::vector<std::string>& paths) const
    {
        if (root.empty())
            return Err("No repository root was provided");
        if (paths.empty())
            return Err("No paths were provided");
        // Restore from the index (the default source), so staged content is never touched and
        // this works on an unborn branch too.
        std::vector<std::string> args = { "restore", "--" };
        args.insert(args.end(), paths.begin(), paths.end());
        int code;
        std::string out = RunGitC(root, args, code);
        return OkOrErr(std::move(out), code, "git restore failed");
    }

    std::string GitBackend::Commit(const std::string& root, const std::string& message, bool amend) const
    {
        if (root.empty())
            return Err("No repository root was provided");
        if (IsBlank(message))
            return Err("Commit message is empty");
        // The message travels via stdin (`-F -`): immune to command-line length limits and
        // quoting edge cases. --cleanup=strip pins message post-processing regardless of the
        // user's commit.cleanup config, so Subject+blank+Body round-trips predictably.
        std::vector<std::string> args = { "commit" };
        if (amend)
            args.push_back("--amend");
        args.push_back("--cleanup=strip");
        args.push_back("-F");
        args.push_back("-");
        int code;
        std::string out = RunGitC(root, args, message, code);
        return OkOrErr(std::move(out), code, "git commit failed");
    }

    std::string GitBackend::HeadMessage(const std::string& root) const
    {
        if (root.empty())
            return Err("No repository root was provided");
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

    // ---- Branches & tags (Phase 5, BR-001..007 / TAG-001..003) ---------------------------------

    std::string GitBackend::Checkout(const std::string& root, const std::string& refName,
                                     bool detach) const
    {
        if (root.empty())
            return Err("No repository root was provided");
        if (IsBlank(refName))
            return Err("No branch or commit was provided");
        // `switch` rather than `checkout`: it only ever takes a ref, so a branch name can never be
        // mistaken for a pathspec. Deliberately NOT forced -- git carries uncommitted changes
        // across when it safely can and refuses otherwise, and that refusal is what the UI shows.
        std::vector<std::string> args = { "switch" };
        if (detach)
            args.push_back("--detach");
        args.push_back(refName);
        int code;
        std::string out = RunGitC(root, args, code);
        return OkOrErr(std::move(out), code, "git switch failed");
    }

    std::string GitBackend::CreateBranch(const std::string& root, const std::string& name,
                                         const std::string& startPoint, bool checkout) const
    {
        if (root.empty())
            return Err("No repository root was provided");
        if (IsBlank(name))
            return Err("No branch name was provided");
        // `switch -c` creates and checks out atomically, and sets the upstream automatically when
        // the start point is a remote-tracking ref. Never -C/-B: an existing name must fail loudly.
        // An empty start point means "from HEAD" -- git's own default when the argument is omitted.
        std::vector<std::string> args = checkout
            ? std::vector<std::string>{ "switch", "-c", name }
            : std::vector<std::string>{ "branch", name };
        if (!startPoint.empty())
            args.push_back(startPoint);
        int code;
        std::string out = RunGitC(root, args, code);
        return OkOrErr(std::move(out), code, "git branch failed");
    }

    std::string GitBackend::DeleteBranch(const std::string& root, const std::string& name,
                                         bool force) const
    {
        if (root.empty())
            return Err("No repository root was provided");
        if (IsBlank(name))
            return Err("No branch name was provided");
        // BR-005: callers must attempt force=false first, so an unmerged branch can only be
        // dropped after git has refused once and the user has confirmed a second time.
        int code;
        std::string out = RunGitC(root, { "branch", force ? "-D" : "-d", name }, code);
        return OkOrErr(std::move(out), code, "git branch failed");
    }

    std::string GitBackend::RenameBranch(const std::string& root, const std::string& oldName,
                                         const std::string& newName) const
    {
        if (root.empty())
            return Err("No repository root was provided");
        if (IsBlank(oldName) || IsBlank(newName))
            return Err("No branch name was provided");
        // -m, never -M: renaming onto an existing branch must fail instead of clobbering it.
        // Renaming the current branch is fine -- git rewrites .git/HEAD itself.
        int code;
        std::string out = RunGitC(root, { "branch", "-m", oldName, newName }, code);
        return OkOrErr(std::move(out), code, "git branch failed");
    }

    std::string GitBackend::CreateTag(const std::string& root, const std::string& name,
                                      const std::string& commitish, const std::string& message) const
    {
        if (root.empty())
            return Err("No repository root was provided");
        if (IsBlank(name))
            return Err("No tag name was provided");
        // A blank message means a lightweight tag (TAG-002); any real message makes it annotated,
        // fed through stdin like `commit -F -` so multi-line text and quoting are non-issues.
        // No --cleanup=: git has no tag.cleanup config, so the default is already deterministic.
        const bool annotated = !IsBlank(message);
        std::vector<std::string> args = { "tag" };
        if (annotated)
        {
            args.push_back("-a");
            args.push_back("-F");
            args.push_back("-");
        }
        args.push_back(name);
        if (!commitish.empty())
            args.push_back(commitish);
        int code;
        std::string out = annotated
            ? RunGitC(root, args, message, code)
            : RunGitC(root, args, code);
        return OkOrErr(std::move(out), code, "git tag failed");
    }

    std::string GitBackend::DeleteTag(const std::string& root, const std::string& name) const
    {
        if (root.empty())
            return Err("No repository root was provided");
        if (IsBlank(name))
            return Err("No tag name was provided");
        int code;
        std::string out = RunGitC(root, { "tag", "-d", name }, code);
        return OkOrErr(std::move(out), code, "git tag failed");
    }

    std::string GitBackend::AheadBehind(const std::string& root, const std::string& a,
                                        const std::string& b) const
    {
        if (root.empty() || a.empty() || b.empty())
            return std::string();
        // "<left>\t<right>" for the symmetric difference: left = commits only in a, right = only
        // in b. The three-dot form is required; two dots would report just one side. This is a
        // read op (empty on failure) because it decorates the compare banner rather than acting.
        int code;
        std::string out = RunGitC(root, { "rev-list", "--left-right", "--count", a + "..." + b }, code);
        if (code != 0)
            return std::string();
        TrimTrailingNewlines(out);
        return out;
    }

    // ---- Remotes (Phase 6, REMOTE-001..009) ----------------------------------------------------

    std::string GitBackend::Remotes(const std::string& root) const
    {
        if (root.empty())
            return std::string();
        // -v prints two lines per remote ("<name>\t<url> (fetch)" / "(push)"). Tab/newline
        // delimited and NUL-free, so it crosses the FFI boundary intact; the pairing of the two
        // lines is parsing, which belongs in the host layer.
        int code;
        std::string out = RunGitC(root, { "remote", "-v" }, code);
        if (code != 0)
            return std::string();
        return out;
    }

    std::string GitBackend::SetRemoteUrl(const std::string& root, const std::string& name,
                                         const std::string& url, bool pushUrl) const
    {
        if (root.empty())
            return Err("No repository root was provided");
        if (IsBlank(name))
            return Err("No remote name was provided");
        if (IsBlank(url))
            return Err("No URL was provided");
        // set-url, never add/remove: Phase 6 edits existing remotes only.
        std::vector<std::string> args = { "remote", "set-url" };
        if (pushUrl)
            args.push_back("--push");
        args.push_back(name);
        args.push_back(url);
        int code;
        std::string out = RunGitC(root, args, code);
        return OkOrErr(std::move(out), code, "git remote set-url failed");
    }

    std::string GitBackend::RunNetworkCommand(const std::string& root, std::vector<std::string> args,
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
        std::string out = RunGitC(root, std::move(args), std::move(options), code);
        return OkOrErr(std::move(out), code, fallback);
    }

    std::string GitBackend::Fetch(const std::string& root, const std::string& remote,
                                  bool allRemotes, bool prune, bool tags,
                                  const ProgressSink& progress) const
    {
        if (root.empty())
            return Err("No repository root was provided");
        if (!allRemotes && IsBlank(remote))
            return Err("No remote was provided");

        std::vector<std::string> args = { "fetch", "--progress" };
        if (allRemotes)
            args.push_back("--all");
        else
            args.push_back(remote);
        if (prune)
            args.push_back("--prune");
        if (tags)
            args.push_back("--tags");
        return RunNetworkCommand(root, std::move(args), progress, "git fetch failed");
    }

    std::string GitBackend::Pull(const std::string& root, const std::string& remote,
                                 const std::string& branch, const ProgressSink& progress) const
    {
        if (root.empty())
            return Err("No repository root was provided");
        // --ff-only is the whole point (REMOTE-004): if the branch has diverged, git refuses and
        // its refusal is the message the user sees, rather than this app silently merging or
        // rebasing. Remote and branch are optional -- omitting both pulls from the branch's
        // configured upstream, which is git's own default.
        std::vector<std::string> args = { "pull", "--ff-only", "--progress" };
        if (!IsBlank(remote) && !IsBlank(branch))
        {
            args.push_back(remote);
            args.push_back(branch);
        }
        return RunNetworkCommand(root, std::move(args), progress, "git pull failed");
    }

    std::string GitBackend::Push(const std::string& root, const std::string& remote,
                                 const std::string& branch, bool setUpstream, bool pushTags,
                                 const ProgressSink& progress) const
    {
        if (root.empty())
            return Err("No repository root was provided");
        if (IsBlank(remote))
            return Err("No remote was provided");
        if (IsBlank(branch))
            return Err("No branch was provided");
        // Never --force / --force-with-lease: a rejected non-fast-forward push must surface as
        // git's own refusal so the user fetches first, not be quietly overridden.
        std::vector<std::string> args = { "push", "--progress" };
        if (setUpstream)
            args.push_back("--set-upstream"); // REMOTE-006: publishes the branch with tracking
        if (pushTags)
            args.push_back("--tags");
        args.push_back(remote);
        args.push_back(branch);
        return RunNetworkCommand(root, std::move(args), progress, "git push failed");
    }
}
