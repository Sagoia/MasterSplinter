// GitBackend.cpp — portable git-command construction (the Bridge abstraction). See GitBackend.h.
//
// No <windows.h>: every process launch goes through the injected IProcessRunner, so the command
// building here is identical on Windows and macOS. The command lines below are unchanged from the
// original single-file GitBackend.cpp; only the process launch moved out to the platform runners.
//
// KEEP PORTABLE. Compiled with PrecompiledHeader=NotUsing.

#include "GitBackend.h"

#include <algorithm>
#include <filesystem>
#include <fstream>
#include <sstream>
#include <string>
#include <vector>

namespace ms
{
    namespace
    {
        // Field separator inside a record is 0x1F; records are separated by 0x1E (emitted directly
        // by git's --pretty=format below). These bytes never occur in normal commit text.
        constexpr char US = '\x1f';

        // The 12-field commit record: full %H, short %h, parents %P, author name/email/ISO date,
        // committer name/email/ISO date, ref decorations %D (description badges), subject %s,
        // body %b. Records end with RS.
        //
        // SHARED by Log and SearchLog on purpose: C# parses these positionally with a field-count
        // floor, so if the two ever drifted, search results would silently mis-map into columns.
        constexpr const char* kLogFormat =
            "--pretty=format:%H%x1f%h%x1f%P%x1f%an%x1f%ae%x1f%aI%x1f%cn%x1f%ce%x1f%cI%x1f%D%x1f%s%x1f%b%x1e";

        // order: 0 = date, 1 = topo, 2 = reverse-date, 3 = author-date. `reverse` receives whether
        // --reverse must be appended (mode 2 is date order, walked backwards).
        const char* LogOrderFlag(int order, bool& reverse)
        {
            reverse = false;
            switch (order)
            {
            case 1: return "--topo-order";
            case 2: reverse = true; return "--date-order"; // "Reverse Date Order"
            case 3: return "--author-date-order";
            default: return "--date-order";
            }
        }

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

        bool reverse = false;
        const char* orderFlag = LogOrderFlag(order, reverse);

        std::vector<std::string> args = { "log", "--all", "--parents", orderFlag };
        if (reverse)
            args.push_back("--reverse");
        if (maxCount > 0)
            args.push_back("-n" + std::to_string(maxCount));
        args.push_back(kLogFormat);

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

    // ---- Merge / rebase / cherry-pick / revert (Phase 7) ---------------------------------------

    namespace
    {
        // Every command in this section can decide it needs an editor: merge writes a merge
        // message, `rebase --continue` and `cherry-pick --continue` re-open the commit message,
        // and `rebase` may reach for the sequence editor. A GUI process has nowhere to put one, so
        // a child that launches it never returns and the progress dialog hangs with no output.
        // Pointing all three at `true` makes git accept the message it already has.
        // (`--no-edit` is passed as well wherever the flag exists — this is the belt to that's
        // braces, and the only cover for the --continue commands, which have no such flag.)
        std::vector<std::pair<std::string, std::string>> NonInteractiveEnv()
        {
            return {
                { "GIT_EDITOR", "true" },
                { "GIT_SEQUENCE_EDITOR", "true" },
                { "GIT_TERMINAL_PROMPT", "0" },
            };
        }

        // std::filesystem::path built from a plain std::string reads it in the OS's narrow
        // encoding (the ANSI code page on Windows). git hands us UTF-8, so a repository under a
        // non-ASCII path would silently look empty. The u8string overload pins the encoding.
        std::filesystem::path FsPath(const std::string& utf8)
        {
            return std::filesystem::path(
                std::u8string(reinterpret_cast<const char8_t*>(utf8.data()), utf8.size()));
        }

        bool PathExists(const std::filesystem::path& p)
        {
            std::error_code ec;
            return std::filesystem::exists(p, ec) && !ec;
        }

        bool DirExists(const std::filesystem::path& p)
        {
            std::error_code ec;
            return std::filesystem::is_directory(p, ec) && !ec;
        }

        // Contents of a small git control file (HEAD-name, msgnum, ...), trimmed; empty when the
        // file is absent or unreadable. These are always tiny and always ASCII/UTF-8.
        std::string ReadControlFile(const std::filesystem::path& p)
        {
            std::ifstream in(p, std::ios::binary);
            if (!in)
                return std::string();
            std::ostringstream buffer;
            buffer << in.rdbuf();
            std::string text = buffer.str();
            TrimTrailingNewlines(text);
            return text;
        }

        int ReadControlInt(const std::filesystem::path& p)
        {
            std::string text = ReadControlFile(p);
            if (text.empty())
                return 0;
            try { return std::stoi(text); }
            catch (...) { return 0; }
        }

        std::string ShortSha(const std::string& sha)
        {
            // *_HEAD holds one full sha per line (an octopus merge writes several); the banner
            // wants something short, so take the first and abbreviate it the way git does.
            std::string first = sha.substr(0, sha.find('\n'));
            TrimTrailingNewlines(first);
            return first.size() > 7 ? first.substr(0, 7) : first;
        }

        // MERGE_MSG as a commit message: git's own comment lines ("# Conflicts:", "#\tfile") are
        // instructions to the editor, not message text. `commit --cleanup=strip` would drop them
        // anyway — dropping them here is what keeps them out of the editor the user actually sees.
        std::string StripCommentLines(const std::string& text)
        {
            std::string result;
            std::size_t start = 0;
            while (start <= text.size())
            {
                std::size_t end = text.find('\n', start);
                if (end == std::string::npos)
                    end = text.size();
                std::string line = text.substr(start, end - start);
                if (line.empty() || line[0] != '#')
                {
                    result += line;
                    result += '\n';
                }
                start = end + 1;
            }
            TrimTrailingNewlines(result);
            return result;
        }

        bool Contains(const std::vector<std::string>& haystack, const std::string& needle)
        {
            return std::find(haystack.begin(), haystack.end(), needle) != haystack.end();
        }

        // A merge-tool name reaches git as `--tool=<name>`; git looks it up in its own table and in
        // mergetool.<name>.cmd. Anything outside this character set is a typo, not a tool, and
        // saying so beats git's rather opaque "Unknown merge tool" for a stray quote.
        bool IsPlainToolName(const std::string& s)
        {
            for (char c : s)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
                          || c == '.' || c == '_' || c == '-' || c == '+';
                if (!ok)
                    return false;
            }
            return !s.empty();
        }
    }

    std::string GitBackend::RunSequencerCommand(const std::string& root,
                                                std::vector<std::string> args,
                                                const ProgressSink& progress,
                                                const char* fallback) const
    {
        RunOptions options;
        options.onOutput = progress;
        options.env = NonInteractiveEnv();
        int code;
        std::string out = RunGitC(root, std::move(args), std::move(options), code);
        return OkOrErr(std::move(out), code, fallback);
    }

    std::string GitBackend::Merge(const std::string& root, const std::string& refName,
                                  bool noFastForward, bool noCommit,
                                  const ProgressSink& progress) const
    {
        if (root.empty())
            return Err("No repository root was provided");
        if (IsBlank(refName))
            return Err("No branch or commit was provided");

        // `--` before the ref (as TortoiseGit does) so a branch whose name also matches a path can
        // never be read as a pathspec. Never --squash: that would drop the second parent and quietly
        // turn a merge into a plain commit, which is not what "merge" was asked for.
        std::vector<std::string> args = { "merge", "--no-edit" };
        if (noFastForward)
            args.push_back("--no-ff");
        if (noCommit)
            args.push_back("--no-commit");
        args.push_back("--");
        args.push_back(refName);
        return RunSequencerCommand(root, std::move(args), progress, "git merge failed");
    }

    std::string GitBackend::Rebase(const std::string& root, const std::string& upstream,
                                   const ProgressSink& progress) const
    {
        if (root.empty())
            return Err("No repository root was provided");
        if (IsBlank(upstream))
            return Err("No upstream branch was provided");
        // Non-interactive only: no -i (there is no todo-list editor here), no --autosquash, and no
        // --autostash — a rebase that silently stashes and re-applies the working tree is exactly
        // the kind of hidden step this app does not take on the user's behalf. git rebase takes no
        // `--` separator, so the upstream is passed on its own.
        return RunSequencerCommand(root, { "rebase", upstream }, progress, "git rebase failed");
    }

    std::string GitBackend::CherryPick(const std::string& root,
                                       const std::vector<std::string>& shas, bool noCommit,
                                       const ProgressSink& progress) const
    {
        if (root.empty())
            return Err("No repository root was provided");
        if (shas.empty())
            return Err("No commits were provided");

        // One command for the whole set: git applies them left to right and stops at the first
        // conflict with the rest still queued in .git/sequencer, which is what makes
        // continue/skip/abort work across a multi-commit pick. The caller passes them oldest-first.
        std::vector<std::string> args = { "cherry-pick", "--no-edit" };
        if (noCommit)
            args.push_back("-n");
        args.insert(args.end(), shas.begin(), shas.end());
        return RunSequencerCommand(root, std::move(args), progress, "git cherry-pick failed");
    }

    std::string GitBackend::Revert(const std::string& root, const std::string& sha, int mainline,
                                   bool noCommit, const ProgressSink& progress) const
    {
        if (root.empty())
            return Err("No repository root was provided");
        if (IsBlank(sha))
            return Err("No commit was provided");

        std::vector<std::string> args = { "revert", "--no-edit" };
        if (mainline > 0)
        {
            // Only for a merge commit, and then it is mandatory: git cannot know which side of the
            // merge "undoing it" should keep. Passing -m for an ordinary commit is an error, so a
            // mainline of 0 means "leave it out".
            args.push_back("-m");
            args.push_back(std::to_string(mainline));
        }
        if (noCommit)
            args.push_back("-n");
        args.push_back(sha);
        return RunSequencerCommand(root, std::move(args), progress, "git revert failed");
    }

    std::string GitBackend::SequencerAction(const std::string& root, const std::string& operation,
                                            const std::string& action,
                                            const ProgressSink& progress) const
    {
        if (root.empty())
            return Err("No repository root was provided");

        // Allowlists, not pass-through: these two strings become the git subcommand and its flag,
        // and the set of legal pairs is small, fixed, and not the same for every operation —
        // `git merge --skip` does not exist.
        const std::vector<std::string> mergeActions = { "continue", "abort" };
        const std::vector<std::string> sequencerActions = { "continue", "abort", "skip" };

        const std::vector<std::string>* allowed = nullptr;
        if (operation == "merge")
            allowed = &mergeActions;
        else if (operation == "rebase" || operation == "cherry-pick" || operation == "revert")
            allowed = &sequencerActions;

        if (allowed == nullptr)
            return Err("Unknown operation: " + operation);
        if (!Contains(*allowed, action))
        {
            // Quote the action rather than splicing it behind "--": a rejected value is arbitrary
            // text, and gluing dashes onto it produces nonsense like "no ----exec=…".
            return Err("\"" + action + "\" is not a valid action for git " + operation);
        }

        const std::string fallback = "git " + operation + " --" + action + " failed";
        return RunSequencerCommand(root, { operation, "--" + action }, progress, fallback.c_str());
    }

    std::string GitBackend::MergeTool(const std::string& root, const std::string& path,
                                      const std::string& tool, const ProgressSink& progress) const
    {
        if (root.empty())
            return Err("No repository root was provided");
        if (IsBlank(path))
            return Err("No file was provided");
        if (!tool.empty() && !IsPlainToolName(tool))
            return Err("\"" + tool + "\" is not a merge tool name. Use a name git knows "
                       "(git mergetool --tool-help lists them).");

        // --no-prompt: git otherwise asks "Hit return to start merge resolution tool" on a terminal
        // that does not exist here. Git does the rest — extracting the BASE/LOCAL/REMOTE
        // temporaries, launching the tool, and `git add`-ing the file when the tool exits cleanly.
        // An empty tool name leaves the choice to the user's own merge.tool configuration.
        std::vector<std::string> args = { "mergetool", "--no-prompt" };
        if (!tool.empty())
            args.push_back("--tool=" + tool);
        args.push_back("--");
        args.push_back(path);
        return RunSequencerCommand(root, std::move(args), progress, "git mergetool failed");
    }

    std::string GitBackend::RepositoryState(const std::string& root) const
    {
        if (root.empty())
            return Err("No repository root was provided");

        // ONE spawn, then read the directory. The alternative — `rev-parse --verify` per candidate
        // ref — is five spawns on every refresh (~42 ms each, measured), and it still cannot answer
        // the rebase question: a rebase in progress is a DIRECTORY, not a ref, and no git command
        // reports it in a machine-readable, locale-independent form. TortoiseGit checks the same
        // paths. --absolute-git-dir (not "<root>/.git") is what makes this correct inside a linked
        // worktree, where these files live under .git/worktrees/<name>/.
        int code;
        std::string gitDirText = RunGitC(root, { "rev-parse", "--absolute-git-dir" }, code);
        TrimTrailingNewlines(gitDirText);
        if (code != 0 || gitDirText.empty())
            return Err("The folder is not a Git repository");

        const std::filesystem::path gitDir = FsPath(gitDirText);

        std::string state = "none";
        std::string detail;
        int step = 0;
        int total = 0;

        const std::filesystem::path rebaseMerge = gitDir / "rebase-merge";
        const std::filesystem::path rebaseApply = gitDir / "rebase-apply";

        if (DirExists(rebaseMerge) || DirExists(rebaseApply))
        {
            state = "rebasing";
            // The merge backend (git's default since 2.26) writes rebase-merge/*; the apply backend
            // (`--apply`, or an `am`-based rebase) writes rebase-apply/* with different file names.
            if (DirExists(rebaseMerge))
            {
                detail = ReadControlFile(rebaseMerge / "head-name");
                step = ReadControlInt(rebaseMerge / "msgnum");
                total = ReadControlInt(rebaseMerge / "end");
            }
            else
            {
                detail = ReadControlFile(rebaseApply / "head-name");
                step = ReadControlInt(rebaseApply / "next");
                total = ReadControlInt(rebaseApply / "last");
            }
            const std::string heads = "refs/heads/";
            if (detail.rfind(heads, 0) == 0)
                detail = detail.substr(heads.size());
        }
        else if (PathExists(gitDir / "CHERRY_PICK_HEAD"))
        {
            state = "cherry-picking";
            detail = ShortSha(ReadControlFile(gitDir / "CHERRY_PICK_HEAD"));
        }
        else if (PathExists(gitDir / "REVERT_HEAD"))
        {
            state = "reverting";
            detail = ShortSha(ReadControlFile(gitDir / "REVERT_HEAD"));
        }
        else if (PathExists(gitDir / "MERGE_HEAD"))
        {
            state = "merging";
            detail = ShortSha(ReadControlFile(gitDir / "MERGE_HEAD"));
        }

        // MERGE_MSG is written by merge, cherry-pick and revert alike, and is what the commit
        // editor should open with. It survives after the conflict is resolved, which is the point:
        // the message must still be there when the user finally commits.
        std::string message = state == "none"
            ? std::string()
            : StripCommentLines(ReadControlFile(gitDir / "MERGE_MSG"));

        return std::string("OK") + US + state + US + detail + US + std::to_string(step) + US
             + std::to_string(total) + US + message;
    }

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

        // A leading '-' would be read as an option rather than a revision/path. git's
        // --end-of-options needs 2.24, so refuse instead of relying on it.
        bool LooksLikeOption(const std::string& s)
        {
            return !s.empty() && s[0] == '-';
        }

        // Blame content lines are raw file bytes. A NUL would truncate the whole payload at the
        // managed marshaller, so a binary file has to be refused rather than silently half-shown.
        bool ContainsNul(const std::string& s)
        {
            return s.find('\0') != std::string::npos;
        }

    }

    std::string GitBackend::StashList(const std::string& root) const
    {
        if (root.empty())
            return std::string();
        // `git stash list` is a reflog walk over refs/stash, so it is log-family: %x1f escapes
        // apply (NOT for-each-ref's %1f). %gd is the selector ("stash@{0}"), %gs the reflog
        // subject, which is exactly the stash message git composed or the user supplied.
        // An empty stash prints nothing and exits 0 — naturally an empty list.
        int code;
        std::string out = RunGitC(root,
            { "stash", "list", "--format=%gd%x1f%H%x1f%h%x1f%gs%x1f%aI%x1f%an%x1e" },
            code);
        // Checked explicitly: git writes its diagnostics to the same merged stream as its records,
        // so without this a failure would be handed to the record parser as if it were data.
        return code == 0 ? out : std::string();
    }

    std::string GitBackend::StashSave(const std::string& root, const std::string& message,
                                      bool includeUntracked, bool keepIndex) const
    {
        if (root.empty())
            return Err("No repository root was provided");

        // The stash's own commit, or empty when refs/stash does not exist yet. Comparing it before
        // and after is how "stashed nothing" is told from "stashed something": `git stash push`
        // exits 0 either way, and matching its "No local changes to save" text would break under
        // any non-English locale.
        auto stashTip = [this, &root]()
        {
            int code;
            std::string sha = RunGitC(root, { "rev-parse", "--verify", "--quiet", "refs/stash" }, code);
            TrimTrailingNewlines(sha);
            return code == 0 ? sha : std::string();
        };

        const std::string before = stashTip();

        std::vector<std::string> args = { "stash", "push" };
        if (includeUntracked)
            args.push_back("--include-untracked");
        if (keepIndex)
            args.push_back("--keep-index");
        if (!IsBlank(message))
        {
            args.push_back("-m");
            args.push_back(message);
        }

        int code;
        std::string out = RunGitC(root, args, code);
        if (code != 0)
            return OkOrErr(std::move(out), code, "git stash failed");

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
            return Err("No repository root was provided");
        if (!IsStashRef(ref))
            return Err("Not a stash entry: " + ref);

        std::vector<std::string> args = { "stash", action };
        if (!ref.empty())
            args.push_back(ref);
        int code;
        std::string out = RunGitC(root, args, code);
        // A conflicting apply/pop exits non-zero with the markers already written; that reaches the
        // caller as ERR carrying git's own text, which the host turns into a conflict hint.
        return OkOrErr(std::move(out), code, fallback);
    }

    std::string GitBackend::Blame(const std::string& root, const std::string& rev,
                                  const std::string& path, bool ignoreWhitespace,
                                  const std::string& detectMoves) const
    {
        if (root.empty())
            return Err("No repository root was provided");
        if (IsBlank(path))
            return Err("No file was provided");
        if (LooksLikeOption(path) || LooksLikeOption(rev))
            return Err("Invalid revision or path");

        // Named modes rather than an int ladder, for the reason MsGitSequencerAction spells out.
        // The flags mirror TortoiseGit's detect-moved-or-copied setting.
        std::vector<std::string> moveFlags;
        if (detectMoves.empty() || detectMoves == "none")
            ; // no flags
        else if (detectMoves == "file")
            moveFlags = { "-M" };                 // moved within the same file
        else if (detectMoves == "commit")
            moveFlags = { "-C" };                 // copied from files modified in the same commit
        else if (detectMoves == "any")
            moveFlags = { "-C", "-C" };           // ...and from any file in the commit that created it
        else
            return Err("Unknown move detection mode: " + detectMoves);

        // --porcelain, not --line-porcelain: the latter repeats every header on every line, which
        // is many times the output for the same information. The per-commit header dedup it implies
        // is unpacked by the host's parser.
        std::vector<std::string> args = { "-c", "core.quotePath=false", "blame", "--porcelain" };
        if (ignoreWhitespace)
            args.push_back("-w");
        args.insert(args.end(), moveFlags.begin(), moveFlags.end());
        args.push_back(rev.empty() ? "HEAD" : rev);
        args.push_back("--");
        args.push_back(path);

        int code;
        std::string out = RunGitC(root, args, code);
        if (code != 0)
        {
            TrimTrailingNewlines(out);
            return Err(out.empty() ? std::string("git blame failed") : std::move(out));
        }
        if (ContainsNul(out))
            return Err("This file is binary; blame is not available.");

        // OK/ERR-framed even though this is a read: "path not in that revision" is a routine,
        // actionable failure and the message is worth keeping. The host splits on the FIRST US
        // only, so 0x1F bytes inside file content stay intact.
        return std::string("OK") + US + out;
    }

    std::string GitBackend::SearchLog(const std::string& root, const std::string& mode,
                                      const std::string& query, const std::string& pathFilter,
                                      int order, int maxCount, bool matchCase, bool useRegex,
                                      bool allBranches) const
    {
        if (root.empty())
            return std::string();

        const std::vector<std::string> modes = { "message", "author", "content", "path", "hash" };
        if (!Contains(modes, mode))
            return std::string();

        const bool hasQuery = !IsBlank(query);
        const bool hasPath = !IsBlank(pathFilter);
        // Nothing to search for: an unfiltered `git log` here would look like a successful search
        // that happened to match everything.
        if (!hasQuery && !hasPath)
            return std::string();

        // A hash is resolved, not matched: verify it names a commit first so a typo comes back as
        // an empty result rather than git's "unknown revision" text rendered as a commit list.
        if (mode == "hash")
        {
            if (!hasQuery || LooksLikeOption(query))
                return std::string();
            int probeCode;
            std::string sha = RunGitC(root,
                { "rev-parse", "--verify", "--quiet", query + "^{commit}" }, probeCode);
            TrimTrailingNewlines(sha);
            if (probeCode != 0 || sha.empty())
                return std::string();
            int code;
            std::string one = RunGitC(root, { "log", "--parents", "-n1", kLogFormat, sha }, code);
            return code == 0 ? one : std::string();
        }

        bool reverse = false;
        const char* orderFlag = LogOrderFlag(order, reverse);

        std::vector<std::string> args = { "log", "--parents", orderFlag };
        if (allBranches)
            args.push_back("--all");
        if (reverse)
            args.push_back("--reverse");
        if (maxCount > 0)
            args.push_back("-n" + std::to_string(maxCount));

        // ONE predicate per mode. git ANDs --grep with --author rather than ORing them, so a single
        // box claiming to search "message or author" would quietly return the intersection; making
        // the field an explicit choice is the only honest way to spend one spawn.
        if (hasQuery && mode == "message")
        {
            if (!useRegex)
                args.push_back("--fixed-strings");
            args.push_back("--grep=" + query);
        }
        else if (hasQuery && mode == "author")
        {
            args.push_back("--author=" + query);
        }
        else if (hasQuery && mode == "content")
        {
            // -S counts occurrences (did this string appear or disappear); -G matches the diff text
            // itself, which is what a regex over a change means.
            args.push_back((useRegex ? "-G" : "-S") + query);
        }
        if (!matchCase)
            args.push_back("--regexp-ignore-case");

        args.push_back(kLogFormat);
        args.push_back("--");
        // In "path" mode the query IS the pathspec (SEARCH-002); any mode may additionally be
        // narrowed by an explicit path filter.
        if (mode == "path" && hasQuery)
            args.push_back(query);
        if (hasPath)
            args.push_back(pathFilter);

        int code;
        std::string out = RunGitC(root, args, code);
        // A rejected pattern (bad regex, unknown pathspec magic) must read as "no results", not as
        // git's complaint fed to the record parser.
        return code == 0 ? out : std::string();
    }

    std::string GitBackend::Reflog(const std::string& root, const std::string& ref,
                                   int maxCount) const
    {
        if (root.empty())
            return std::string();
        if (LooksLikeOption(ref))
            return std::string();

        // %gd selector, %H/%h the commit, %gs the reflog subject ("commit: <subject>",
        // "pull: Fast-forward"), %aI/%an the commit's author, %s the commit subject — the reflog
        // subject is often terser than the commit's own.
        //
        // A ref with no reflog makes git exit non-zero; the ""-on-error path already degrades to an
        // empty list, so no filesystem probing is needed to keep that quiet.
        std::vector<std::string> args = {
            "reflog", "show", "--format=%gd%x1f%H%x1f%h%x1f%gs%x1f%aI%x1f%an%x1f%s%x1e"
        };
        if (maxCount > 0)
            args.push_back("-n" + std::to_string(maxCount));
        args.push_back(ref.empty() ? "HEAD" : ref);

        int code;
        std::string out = RunGitC(root, args, code);
        // Checked, not assumed: "not a valid ref" is the ORDINARY answer for refs/stash in a repo
        // that has never stashed, and it arrives on the same merged stream as the records would.
        return code == 0 ? out : std::string();
    }
}
