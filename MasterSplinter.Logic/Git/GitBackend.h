#pragma once
// GitBackend — the Bridge "abstraction".
//
// Builds git command lines and returns git's raw (UTF-8) output. It knows nothing about the
// operating system: process execution is delegated to an injected IProcessRunner (the Bridge
// "implementor"), so the exact same source compiles and runs on Windows and macOS. This mirrors
// TortoiseGit's model of shelling out to git and letting the host parse the delimited stream
// (parsing stays in the C# layer).
//
// The class is stateless with respect to any single repository — every method takes `root` —
// so one process-wide instance serves all calls, exactly as the flat C ABI did before.
//
// KEEP PORTABLE: no <windows.h>. Compiled with PrecompiledHeader=NotUsing.

#include <cstddef>
#include <functional>
#include <memory>
#include <optional>
#include <string>
#include <vector>

#include "../Platform/IProcessRunner.h"
#include "GitArgs.h"

namespace ms
{
    class GitBackend
    {
    public:
        // Live progress for the long-running network commands. Receives each chunk of git's
        // merged output plus periodic (nullptr, 0) heartbeats; returning false cancels the
        // command. See RunOptions::onOutput — this is that sink, passed straight through.
        using ProgressSink = std::function<bool(const char*, std::size_t)>;

        explicit GitBackend(std::unique_ptr<IProcessRunner> runner);

        // Each method corresponds 1:1 to an MsGit* C-ABI function and returns the same delimited
        // UTF-8 payload the ABI returned before (see MasterSplinter.Logic.h for the formats).
        bool IsRepository(const std::string& path) const;
        std::string OpenRepository(const std::string& path) const;
        std::string Log(const std::string& root, int order, int maxCount) const;
        std::string RefDetails(const std::string& root) const;
        std::string CommitFiles(const std::string& root, const std::string& sha) const;
        std::string CommitShortStat(const std::string& root, const std::string& sha) const;
        std::string FileDiff(const std::string& root, const std::string& sha,
                             const std::string& path, int wsMode) const;
        std::string FileAtCommit(const std::string& root, const std::string& sha,
                                 const std::string& path) const;
        std::string RangeFiles(const std::string& root, const std::string& a, const std::string& b) const;
        std::string RangeShortStat(const std::string& root, const std::string& a, const std::string& b) const;
        std::string RangeFileDiff(const std::string& root, const std::string& a, const std::string& b,
                                  const std::string& path, int wsMode) const;
        std::string Status(const std::string& root) const;
        std::string WorkTreeFileDiff(const std::string& root, const std::string& path,
                                     int area, int wsMode) const;

        // Raw bytes of a file at a commit/ref (binary-safe). nullopt if git failed (so the C ABI
        // can return nullptr, as before); otherwise the payload, whose length is value().size().
        std::optional<std::string> FileBytesAt(const std::string& root, const std::string& sha,
                                               const std::string& path) const;

        // ---- Write operations (Phase 4, COMMIT-001..007) ----
        // All return "OK" on success or "ERR" US <message> on failure (git's merged output,
        // trailing newlines trimmed). Empty root/paths/message short-circuit to ERR without
        // spawning git.
        std::string StagePaths(const std::string& root, const std::vector<std::string>& paths) const;
        std::string StageAll(const std::string& root) const;
        std::string UnstagePaths(const std::string& root, const std::vector<std::string>& paths) const;
        std::string DiscardPaths(const std::string& root, const std::vector<std::string>& paths) const;
        std::string Commit(const std::string& root, const std::string& message, bool amend) const;

        // "OK" US <subject> US <body> for the HEAD commit (amend pre-fill), or ERR (e.g. no
        // commits yet).
        std::string HeadMessage(const std::string& root) const;

        // ---- Branches & tags (Phase 5, BR-001..007 / TAG-001..003) ----
        // Same "OK" / "ERR" US <message> contract as the Phase 4 writes; empty root or blank
        // names short-circuit to ERR without spawning git. Ref-name validity is deliberately
        // NOT pre-checked here — git's own check-ref-format produces a better message than we
        // could, and it reaches the UI verbatim through the ERR channel.
        std::string Checkout(const std::string& root, const std::string& refName, bool detach) const;
        std::string CreateBranch(const std::string& root, const std::string& name,
                                 const std::string& startPoint, bool checkout) const;
        std::string DeleteBranch(const std::string& root, const std::string& name, bool force) const;
        std::string RenameBranch(const std::string& root, const std::string& oldName,
                                 const std::string& newName) const;
        // Blank message => lightweight tag; otherwise annotated, message fed via stdin.
        std::string CreateTag(const std::string& root, const std::string& name,
                              const std::string& commitish, const std::string& message) const;
        std::string DeleteTag(const std::string& root, const std::string& name) const;

        // Read op (empty on failure): "<onlyInA>\t<onlyInB>" for the symmetric difference a...b.
        std::string AheadBehind(const std::string& root, const std::string& a,
                                const std::string& b) const;

        // ---- Remotes (Phase 6, REMOTE-001..009) ------------------------------------------------

        // Raw `git remote -v` output ("<name>\t<url> (fetch|push)" per line); parsed in the host
        // layer, like every other read. Empty on failure.
        std::string Remotes(const std::string& root) const;

        // git remote set-url [--push] <name> <url>. Same "OK" / "ERR" US <message> contract as the
        // other writes. The URL is NOT validated here — the caller validates before offering to
        // save, and git rejects what it cannot use.
        std::string SetRemoteUrl(const std::string& root, const std::string& name,
                                 const std::string& url, bool pushUrl) const;

        // The three network commands. All pass --progress (git prints none when stderr is not a
        // tty, which it never is here) and set GIT_TERMINAL_PROMPT=0, so a missing credential
        // helper produces a readable error instead of a child blocked on a prompt no GUI can
        // answer. `progress` may be empty, in which case output is only buffered.
        std::string Fetch(const std::string& root, const std::string& remote, bool allRemotes,
                          bool prune, bool tags, const ProgressSink& progress) const;

        // Deliberately --ff-only (REMOTE-004): a diverged branch gets git's own refusal rather
        // than a merge commit or a rebase the user did not ask for. Empty remote/branch pulls
        // from the current branch's configured upstream.
        std::string Pull(const std::string& root, const std::string& remote,
                         const std::string& branch, const ProgressSink& progress) const;

        // setUpstream adds -u, which is what publishes a new branch with tracking (REMOTE-006).
        // Never forced: a rejected non-fast-forward push must reach the user as git wrote it.
        std::string Push(const std::string& root, const std::string& remote,
                         const std::string& branch, bool setUpstream, bool pushTags,
                         const ProgressSink& progress) const;

        // ---- Merge / rebase / cherry-pick / revert (Phase 7) -----------------------------------
        // Same "OK" / "ERR" US <message> contract. Every one of them runs with the non-interactive
        // environment (see NonInteractiveEnv) because these are the git commands that open an
        // editor, and a GUI child blocked on an editor nobody can see never comes back.
        //
        // NONE of them force or rewrite beyond what was asked: no --squash, no -X ours/theirs, no
        // rebase -i, no --autosquash, no --autostash, no --force-rebase. What git refuses reaches
        // the user as git's own refusal. A guard test pins this.

        // git merge --no-edit [--no-ff] [--no-commit] -- <ref>  (MERGE-001)
        std::string Merge(const std::string& root, const std::string& refName, bool noFastForward,
                          bool noCommit, const ProgressSink& progress) const;

        // git rebase <upstream>  (REBASE-001). Non-interactive only.
        std::string Rebase(const std::string& root, const std::string& upstream,
                           const ProgressSink& progress) const;

        // git cherry-pick --no-edit [-n] <sha>...  (CHERRY-001/002). Commits are applied in the
        // order given, so the caller must pass them oldest-first.
        std::string CherryPick(const std::string& root, const std::vector<std::string>& shas,
                               bool noCommit, const ProgressSink& progress) const;

        // git revert --no-edit [-m <mainline>] [-n] <sha>  (REVERT-001). `mainline` is 1-based and
        // 0 means "omit -m"; reverting a MERGE commit requires it, which is why it is plumbed
        // rather than hardcoded.
        std::string Revert(const std::string& root, const std::string& sha, int mainline,
                           bool noCommit, const ProgressSink& progress) const;

        // git <operation> --<action> : the continue/abort/skip half of all four operations
        // (MERGE-002, REBASE-002). Both arguments are validated against fixed allowlists and
        // anything else returns ERR without spawning git — deliberately named strings rather than
        // an int pair, because a silently-swapped integer contract has bitten this ABI before.
        //   merge       -> continue | abort            (git has no `merge --skip`)
        //   rebase      -> continue | abort | skip
        //   cherry-pick -> continue | abort | skip
        //   revert      -> continue | abort | skip
        std::string SequencerAction(const std::string& root, const std::string& operation,
                                    const std::string& action, const ProgressSink& progress) const;

        // git mergetool --no-prompt [--tool=<tool>] -- <path>  (MERGE-004). Git extracts the
        // BASE/LOCAL/REMOTE temporaries, launches the tool and stages the result itself when the
        // tool exits cleanly. An empty `tool` leaves the choice to the user's merge.tool config.
        std::string MergeTool(const std::string& root, const std::string& path,
                              const std::string& tool, const ProgressSink& progress) const;

        // Whether an operation is half-finished, and how far along it is:
        //   "OK" US <state> US <detail> US <step> US <total> US <message>
        // state is one of none | merging | rebasing | cherry-picking | reverting. `detail` names
        // the branch being rebased (or is empty), step/total are the rebase's position (0 when not
        // rebasing), and `message` is MERGE_MSG when git has written one (it pre-fills the commit
        // editor). ERR only when the path is not a repository at all.
        //
        // Costs ONE git spawn (rev-parse --absolute-git-dir); the rest is reading that directory.
        // See the implementation for why this is not five rev-parse calls.
        std::string RepositoryState(const std::string& root) const;

        // ---- Stash (Phase 8, STASH-001..004) ---------------------------------------------------
        // `ref` is a stash selector ("stash@{2}") or empty for the most recent entry; anything else
        // returns ERR without spawning git. NOTE for callers: dropping or popping renumbers every
        // later entry, so a selector is only valid until the next stash mutation.

        // git stash list --format=... : one record per entry (RS-separated), six US-separated
        // fields — selector %gd, full sha %H, short sha %h, reflog subject %gs, author ISO date,
        // author name. Empty string when there are no stashes (and on error).
        std::string StashList(const std::string& root) const;

        // git stash push [--include-untracked] [--keep-index] [-m <message>]  (STASH-001).
        // Returns ERR when git stashed nothing — it exits 0 on a clean tree, which would otherwise
        // read as success. Costs 3 spawns (probe, push, probe); see the implementation.
        std::string StashSave(const std::string& root, const std::string& message,
                              bool includeUntracked, bool keepIndex) const;

        // git stash apply|pop|drop [<ref>]  (STASH-002/003/004). A conflicting apply or pop exits
        // non-zero with the markers already written, so it arrives as ERR carrying git's own text.
        std::string StashApply(const std::string& root, const std::string& ref) const;
        std::string StashPop(const std::string& root, const std::string& ref) const;
        std::string StashDrop(const std::string& root, const std::string& ref) const;

        // ---- Blame (Phase 8, BLAME-001) --------------------------------------------------------
        // git blame --porcelain [-w] [<move flags>] <rev> -- <path>. Empty `rev` means HEAD.
        // `detectMoves` is one of "" / "none" (nothing), "file" (-M), "commit" (-C), "any" (-C -C);
        // anything else is ERR without spawning. Returns "OK" US <raw porcelain> or ERR <message> —
        // framed unlike the other reads because "path not in that revision" is worth reporting.
        // A binary file (any NUL in the output) is refused rather than truncated at the marshaller.
        std::string Blame(const std::string& root, const std::string& rev, const std::string& path,
                          bool ignoreWhitespace, const std::string& detectMoves) const;

        // ---- Search (Phase 8, SEARCH-001/002) --------------------------------------------------
        // Records are IDENTICAL to Log's, so the host parses both with one routine.
        // `mode` selects exactly one git predicate — deliberately one, because git ANDs --grep with
        // --author instead of ORing them:
        //   message -> --grep=<query>            author  -> --author=<query>
        //   content -> -S<query> / -G<query>     path    -> <query> as the pathspec (SEARCH-002)
        //   hash    -> resolve <query> to a commit and return that one record
        // `pathFilter` narrows any mode further. Empty query AND empty pathFilter, an unknown mode,
        // or a hash that resolves to nothing all return "" without a log walk.
        std::string SearchLog(const std::string& root, const std::string& mode,
                              const std::string& query, const std::string& pathFilter,
                              int order, int maxCount, bool matchCase, bool useRegex,
                              bool allBranches) const;

        // ---- Reflog (Phase 8, REFLOG-001) ------------------------------------------------------
        // git reflog show --format=... [-n<maxCount>] <ref>; empty `ref` means HEAD. Seven
        // US-separated fields per RS-separated record: selector %gd, full sha, short sha, reflog
        // subject %gs ("commit: <subject>"), author ISO date, author name, commit subject %s.
        // A ref with no reflog exits non-zero and therefore yields "" — an empty list, not an error.
        std::string Reflog(const std::string& root, const std::string& ref, int maxCount) const;

    private:
        // Run `git -C <root> <args...>`, returning the merged stdout/stderr. `code` receives git's
        // exit status (or -1 if the process could not be started). The `input` overload feeds the
        // child's stdin (e.g. `commit -F -`); the `options` overload exposes the rest of the
        // runner's knobs (environment, live output) for the network commands.
        std::string RunGitC(const std::string& root, std::vector<std::string> args, int& code) const;
        std::string RunGitC(const std::string& root, std::vector<std::string> args,
                            const std::optional<std::string>& input, int& code) const;
        std::string RunGitC(const std::string& root, std::vector<std::string> args,
                            RunOptions options, int& code) const;

        // ---- Terminal verbs -------------------------------------------------------------------
        // Run a built command and apply one of the two return conventions documented in
        // docs/abi.md, so no method has to spell out the `int code;` + trim + frame dance itself.

        // Raw: the merged output, exit code IGNORED. For the reads where a non-zero exit is a
        // normal outcome (`diff --no-index` exits 1 precisely when the files differ) or where the
        // host's field-count floor already discards git's error text.
        std::string RunRaw(const std::string& root, GitArgs args) const;

        // Path list: like RunRaw, but for commands that emit PATHS (--name-status and friends).
        // Appends -z and translates git's NUL separators to RS, so the two can never drift
        // apart. Without -z a path containing a quote, backslash or control character arrives
        // C-quoted -- core.quotePath=false does NOT prevent that -- and the host would then
        // address a file that does not exist. Records are not fixed width: a status token is
        // followed by one path, or by two (old then new) for R/C.
        std::string RunPathList(const std::string& root, GitArgs args) const;
        // Read: the merged output, or "" when git failed. The default for a read.
        std::string RunRead(const std::string& root, GitArgs args) const;

        // Read one value: output with trailing newlines trimmed, or "" when git failed. `ok`
        // receives whether git succeeded, since "" is also a legitimate result for some probes.
        std::string RunValue(const std::string& root, GitArgs args, bool& ok) const;

        // Write: "OK", or "ERR" US <git's output, or `fallback` when git printed nothing>.
        std::string RunWrite(const std::string& root, GitArgs args, const char* fallback) const;
        std::string RunWrite(const std::string& root, GitArgs args,
                             const std::optional<std::string>& input, const char* fallback) const;

        // Shared shape of fetch/pull/push: --progress + the no-terminal-prompt environment.
        std::string RunNetworkCommand(const std::string& root, GitArgs args,
                                      const ProgressSink& progress, const char* fallback) const;

        // Shared shape of the Phase 7 commands: the non-interactive environment + a live sink.
        std::string RunSequencerCommand(const std::string& root, GitArgs args,
                                        const ProgressSink& progress, const char* fallback) const;

        // Shared shape of stash apply/pop/drop: validate the selector, then one plain spawn.
        std::string RunStashCommand(const std::string& root, const char* action,
                                    const std::string& ref, const char* fallback) const;

        std::unique_ptr<IProcessRunner> runner_;
    };
}
