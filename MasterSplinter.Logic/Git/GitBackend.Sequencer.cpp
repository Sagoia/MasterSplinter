// GitBackend — merge, rebase, cherry-pick, revert, and the repository-state probe.
//
// One area of GitBackend, split out of the original single 1300-line GitBackend.cpp.
// Same class, same public API — only the file boundary is new. Shared helpers live in
// GitText.h; the git argument builder in GitArgs.h.
//
// KEEP PORTABLE: no <windows.h>. Compiled with PrecompiledHeader=NotUsing.

#include "GitBackend.h"
#include "GitText.h"
#include <filesystem>
#include <fstream>
#include <sstream>

namespace ms
{
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

    std::string GitBackend::RunSequencerCommand(const std::string& root, GitArgs args,
                                                const ProgressSink& progress,
                                                const char* fallback) const
    {
        RunOptions options;
        options.onOutput = progress;
        options.env = NonInteractiveEnv();
        int code;
        std::string out = RunGitC(root, args.Take(), std::move(options), code);
        return OkOrErr(std::move(out), code, fallback);
    }

    std::string GitBackend::Merge(const std::string& root, const std::string& refName,
                                  bool noFastForward, bool noCommit,
                                  const ProgressSink& progress) const
    {
        if (root.empty())
            return NoRoot();
        if (IsBlank(refName))
            return Err("No branch or commit was provided");

        // `--` before the ref (as TortoiseGit does) so a branch whose name also matches a path can
        // never be read as a pathspec. Never --squash: that would drop the second parent and quietly
        // turn a merge into a plain commit, which is not what "merge" was asked for.
        return RunSequencerCommand(root, GitArgs{ "merge", "--no-edit" }
            .AddIf(noFastForward, "--no-ff")
            .AddIf(noCommit, "--no-commit")
            .Path(refName),
            progress, "git merge failed");
    }

    std::string GitBackend::Rebase(const std::string& root, const std::string& upstream,
                                   const ProgressSink& progress) const
    {
        if (root.empty())
            return NoRoot();
        if (IsBlank(upstream))
            return Err("No upstream branch was provided");
        // Non-interactive only: no -i (there is no todo-list editor here), no --autosquash, and no
        // --autostash — a rebase that silently stashes and re-applies the working tree is exactly
        // the kind of hidden step this app does not take on the user's behalf. git rebase takes no
        // `--` separator, so the upstream is passed on its own.
        return RunSequencerCommand(root, GitArgs{ "rebase" }.Add(upstream),
                                   progress, "git rebase failed");
    }

    std::string GitBackend::CherryPick(const std::string& root,
                                       const std::vector<std::string>& shas, bool noCommit,
                                       const ProgressSink& progress) const
    {
        if (root.empty())
            return NoRoot();
        if (shas.empty())
            return Err("No commits were provided");

        // One command for the whole set: git applies them left to right and stops at the first
        // conflict with the rest still queued in .git/sequencer, which is what makes
        // continue/skip/abort work across a multi-commit pick. The caller passes them oldest-first.
        return RunSequencerCommand(root, GitArgs{ "cherry-pick", "--no-edit" }
            .AddIf(noCommit, "-n")
            .AddAll(shas),
            progress, "git cherry-pick failed");
    }

    std::string GitBackend::Revert(const std::string& root, const std::string& sha, int mainline,
                                   bool noCommit, const ProgressSink& progress) const
    {
        if (root.empty())
            return NoRoot();
        if (IsBlank(sha))
            return Err("No commit was provided");

        GitArgs args{ "revert", "--no-edit" };
        if (mainline > 0)
        {
            // Only for a merge commit, and then it is mandatory: git cannot know which side of the
            // merge "undoing it" should keep. Passing -m for an ordinary commit is an error, so a
            // mainline of 0 means "leave it out".
            args.Add("-m").Add(std::to_string(mainline));
        }
        return RunSequencerCommand(root, args.AddIf(noCommit, "-n").Add(sha),
                                   progress, "git revert failed");
    }

    std::string GitBackend::SequencerAction(const std::string& root, const std::string& operation,
                                            const std::string& action,
                                            const ProgressSink& progress) const
    {
        if (root.empty())
            return NoRoot();

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
        return RunSequencerCommand(root, GitArgs{}.Add(operation).Add("--" + action),
                                   progress, fallback.c_str());
    }

    std::string GitBackend::MergeTool(const std::string& root, const std::string& path,
                                      const std::string& tool, const ProgressSink& progress) const
    {
        if (root.empty())
            return NoRoot();
        if (IsBlank(path))
            return Err("No file was provided");
        if (!tool.empty() && !IsPlainToolName(tool))
            return Err("\"" + tool + "\" is not a merge tool name. Use a name git knows "
                       "(git mergetool --tool-help lists them).");

        // --no-prompt: git otherwise asks "Hit return to start merge resolution tool" on a terminal
        // that does not exist here. Git does the rest — extracting the BASE/LOCAL/REMOTE
        // temporaries, launching the tool, and `git add`-ing the file when the tool exits cleanly.
        // An empty tool name leaves the choice to the user's own merge.tool configuration.
        return RunSequencerCommand(root, GitArgs{ "mergetool", "--no-prompt" }
            .AddIf(!tool.empty(), "--tool=" + tool)
            .Path(path),
            progress, "git mergetool failed");
    }

    std::string GitBackend::RepositoryState(const std::string& root) const
    {
        if (root.empty())
            return NoRoot();

        // ONE spawn, then read the directory. The alternative — `rev-parse --verify` per candidate
        // ref — is five spawns on every refresh (~42 ms each, measured), and it still cannot answer
        // the rebase question: a rebase in progress is a DIRECTORY, not a ref, and no git command
        // reports it in a machine-readable, locale-independent form. TortoiseGit checks the same
        // paths. --absolute-git-dir (not "<root>/.git") is what makes this correct inside a linked
        // worktree, where these files live under .git/worktrees/<name>/.
        bool ok = false;
        std::string gitDirText = RunValue(root, { "rev-parse", "--absolute-git-dir" }, ok);
        if (!ok || gitDirText.empty())
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

}
