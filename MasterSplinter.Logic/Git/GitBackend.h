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

#include <memory>
#include <optional>
#include <string>
#include <vector>

#include "../Platform/IProcessRunner.h"

namespace ms
{
    class GitBackend
    {
    public:
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

    private:
        // Run `git -C <root> <args...>`, returning the merged stdout/stderr. `code` receives git's
        // exit status (or -1 if the process could not be started). The `input` overload feeds the
        // child's stdin (e.g. `commit -F -`).
        std::string RunGitC(const std::string& root, std::vector<std::string> args, int& code) const;
        std::string RunGitC(const std::string& root, std::vector<std::string> args,
                            const std::optional<std::string>& input, int& code) const;

        std::unique_ptr<IProcessRunner> runner_;
    };
}
