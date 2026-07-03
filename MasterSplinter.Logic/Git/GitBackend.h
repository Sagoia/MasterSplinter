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
        std::string Refs(const std::string& root) const;
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

        // Raw bytes of a file at a commit/ref (binary-safe). nullopt if git failed (so the C ABI
        // can return nullptr, as before); otherwise the payload, whose length is value().size().
        std::optional<std::string> FileBytesAt(const std::string& root, const std::string& sha,
                                               const std::string& path) const;

    private:
        // Run `git -C <root> <args...>`, returning the merged stdout/stderr. `code` receives git's
        // exit status (or -1 if the process could not be started).
        std::string RunGitC(const std::string& root, std::vector<std::string> args, int& code) const;

        std::unique_ptr<IProcessRunner> runner_;
    };
}
