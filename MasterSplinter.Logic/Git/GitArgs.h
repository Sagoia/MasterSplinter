#pragma once
// GitArgs — a git argument list under construction.
//
// Collapses the pieces nearly every GitBackend method used to spell out by hand: the
// `-c core.quotePath=false` prefix, the `--no-optional-locks` prefix, the whitespace-mode flag
// mapping, and the `--` separator followed by a pathspec list.
//
// Order is preserved exactly as called, because git cares: `-c` and `--no-optional-locks` are
// git-level options that must precede the subcommand, while everything else follows it. The
// builder deliberately does NOT reorder, deduplicate or validate — the argv it produces is the
// contract the unit tests assert, so it has to be predictable rather than clever.
//
// KEEP PORTABLE: no <windows.h>.

#include <initializer_list>
#include <string>
#include <utility>
#include <vector>

namespace ms
{
    class GitArgs
    {
    public:
        GitArgs() = default;

        // The common case: a literal subcommand + flags, e.g. GitArgs{ "diff", "--shortstat" }.
        GitArgs(std::initializer_list<const char*> args) { Add(args); }

        // ---- git-level options (must precede the subcommand) ----

        // NOTE: there is deliberately no NoOptionalLocks() helper. `--no-optional-locks` stops git
        // persisting its refreshed stat cache, which measured ~4.6x slower on every status/diff call
        // and guarded against a watcher loop that does not actually happen. See GitBackend.WorkTree.cpp
        // and docs/architecture.md before reintroducing it.

        // Keeps non-ASCII paths literal instead of C-quoted, so the host parses them as-is.
        GitArgs& QuotePathOff() { return Add({ "-c", "core.quotePath=false" }); }

        // For a merge, diff against the FIRST PARENT.
        //
        // TRAP: `-m` does NOT do this. It emits one section PER PARENT even alongside
        // --first-parent, which made the file list the union of both parents' changes and made
        // --shortstat print one line per parent (the host's regex parser then blended two diffs
        // into one wrong stat). Omitting both flags is worse still: diff-tree prints NOTHING for
        // a merge, so every file listed under a merge opened to an empty diff pane.
        //
        // Every command describing the same commit must use this, or they describe different
        // diffs. Needs git 2.31+.
        GitArgs& FirstParentMerges() { return Add("--diff-merges=first-parent"); }
        // ---- arguments ----

        GitArgs& Add(std::string arg)
        {
            args_.push_back(std::move(arg));
            return *this;
        }

        GitArgs& Add(std::initializer_list<const char*> args)
        {
            for (const char* a : args)
                args_.emplace_back(a);
            return *this;
        }

        GitArgs& AddIf(bool condition, const char* arg)
        {
            if (condition)
                args_.emplace_back(arg);
            return *this;
        }

        // Same, for a computed argument (e.g. "-n" + count). Kept separate from the const char*
        // overload so a plain literal never pays for a std::string temporary.
        GitArgs& AddIf(bool condition, std::string arg)
        {
            if (condition)
                args_.push_back(std::move(arg));
            return *this;
        }

        GitArgs& AddAll(const std::vector<std::string>& args)
        {
            args_.insert(args_.end(), args.begin(), args.end());
            return *this;
        }

        // wsMode: 0 = honor whitespace, 1 = --ignore-space-change, 2 = --ignore-all-space.
        // Anything else adds nothing, matching the hand-written if/else-if chains this replaces.
        GitArgs& Whitespace(int wsMode)
        {
            if (wsMode == 1)
                args_.emplace_back("--ignore-space-change");
            else if (wsMode == 2)
                args_.emplace_back("--ignore-all-space");
            return *this;
        }

        // The `--` that separates revisions from pathspecs.
        GitArgs& Separator() { return Add("--"); }

        // `-- <path>` / `-- <paths...>`: how every pathspec-taking command here ends.
        GitArgs& Path(std::string path) { return Separator().Add(std::move(path)); }
        GitArgs& Paths(const std::vector<std::string>& paths) { return Separator().AddAll(paths); }

        std::vector<std::string> Take() { return std::move(args_); }
        const std::vector<std::string>& Peek() const { return args_; }

    private:
        std::vector<std::string> args_;
    };
}
