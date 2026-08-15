// GitApi.cpp — the flat C ABI (declared in MasterSplinter.Logic.h) consumed by C# via P/Invoke.
//
// These are thin shims. They own the only two things that must stay at the boundary: the char*
// heap ownership (DupString / MsGitFree) and the std::string <-> char* conversion. All git work
// is delegated to a single process-wide GitBackend, which is wired to this OS's IProcessRunner
// through the platform Abstract Factory (CreatePlatformFactory).
//
// KEEP PORTABLE: no <windows.h> here. Compiled with PrecompiledHeader=NotUsing.

#include "MasterSplinter.Logic.h"

#include "Git/GitBackend.h"
#include "Platform/IPlatformFactory.h"

#include <cstddef>
#include <cstdlib>
#include <cstring>
#include <memory>
#include <mutex>
#include <optional>
#include <string>
#include <vector>

namespace
{
    // Lazily-created, process-wide backend. Built on first use (defensive: the C ABI works even
    // if MsLogicInitialize was not called first) and rebuilt if it was torn down. Guarded because
    // the C# host issues these calls from thread-pool threads (Task.Run).
    std::unique_ptr<ms::GitBackend> g_backend;
    std::mutex g_backendMutex;

    ms::GitBackend& Backend()
    {
        std::lock_guard<std::mutex> lock(g_backendMutex);
        if (!g_backend)
        {
            std::unique_ptr<ms::IPlatformFactory> factory = ms::CreatePlatformFactory();
            g_backend = std::make_unique<ms::GitBackend>(factory->CreateProcessRunner());
        }
        return *g_backend;
    }

    // Null-safe conversion of an inbound C string to std::string (nullptr -> empty).
    std::string Str(const char* p) { return p ? std::string(p) : std::string(); }

    // Split an inbound 0x1E-separated path list into the vector the backend takes. Empty
    // segments are dropped, so a trailing separator is harmless.
    std::vector<std::string> SplitPaths(const char* paths)
    {
        std::vector<std::string> result;
        std::string s = Str(paths);
        size_t start = 0;
        while (start <= s.size())
        {
            size_t end = s.find('\x1e', start);
            if (end == std::string::npos)
                end = s.size();
            if (end > start)
                result.emplace_back(s, start, end - start);
            start = end + 1;
        }
        return result;
    }

    // Adapt the C-ABI progress callback to the backend's std::function sink. A null callback
    // yields an empty sink, which the runner reads as "buffer only" — so passing NULL from the
    // host costs nothing. The length is passed through explicitly: progress chunks are arbitrary
    // byte runs, not NUL-terminated strings.
    ms::GitBackend::ProgressSink MakeSink(MsGitProgressFn cb, void* userData)
    {
        if (!cb)
            return {};
        return [cb, userData](const char* bytes, std::size_t length) {
            return cb(userData, bytes, static_cast<int>(length)) != 0;
        };
    }

    // Heap copy the caller frees via MsGitFree (allocated inside this DLL, freed inside it).
    // memcpy (not strcpy) so embedded NULs survive for the binary byte payloads.
    char* DupString(const std::string& s)
    {
        char* p = static_cast<char*>(malloc(s.size() + 1));
        if (!p)
            return nullptr;
        memcpy(p, s.data(), s.size());
        p[s.size()] = '\0';
        return p;
    }
}

// Backend lifecycle hooks, called from MsLogicInitialize / MsLogicShutdown (MasterSplinter.Logic.cpp).
namespace msapi
{
    void InitBackend() { (void)Backend(); }

    void ShutdownBackend()
    {
        std::lock_guard<std::mutex> lock(g_backendMutex);
        g_backend.reset();
    }
}

// ------------------------------------------------------------------------------------------
// Flat C ABI. Each function guards its inputs exactly as before, then delegates to GitBackend.
// ------------------------------------------------------------------------------------------

extern "C" MASTERSPLINTERLOGIC_API bool MsGitIsRepository(const char* path)
{
    return Backend().IsRepository(Str(path));
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitOpenRepository(const char* path)
{
    return DupString(Backend().OpenRepository(Str(path)));
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitLog(const char* root, int order, int maxCount)
{
    return DupString(Backend().Log(Str(root), order, maxCount));
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitRefDetails(const char* root)
{
    return DupString(Backend().RefDetails(Str(root)));
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitCommitFiles(const char* root, const char* sha)
{
    return DupString(Backend().CommitFiles(Str(root), Str(sha)));
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitCommitShortStat(const char* root, const char* sha)
{
    return DupString(Backend().CommitShortStat(Str(root), Str(sha)));
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitFileDiff(const char* root, const char* sha, const char* path, int wsMode)
{
    return DupString(Backend().FileDiff(Str(root), Str(sha), Str(path), wsMode));
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitFileAtCommit(const char* root, const char* sha, const char* path)
{
    return DupString(Backend().FileAtCommit(Str(root), Str(sha), Str(path)));
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitRangeFiles(const char* root, const char* a, const char* b)
{
    return DupString(Backend().RangeFiles(Str(root), Str(a), Str(b)));
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitRangeShortStat(const char* root, const char* a, const char* b)
{
    return DupString(Backend().RangeShortStat(Str(root), Str(a), Str(b)));
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitRangeFileDiff(const char* root, const char* a, const char* b,
                                                            const char* path, int wsMode)
{
    return DupString(Backend().RangeFileDiff(Str(root), Str(a), Str(b), Str(path), wsMode));
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitStatus(const char* root)
{
    return DupString(Backend().Status(Str(root)));
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitWorkTreeFileDiff(const char* root, const char* path,
                                                               int area, int wsMode)
{
    return DupString(Backend().WorkTreeFileDiff(Str(root), Str(path), area, wsMode));
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitFileBytesAtCommit(const char* root, const char* sha,
                                                                const char* path, int* outLen)
{
    if (outLen)
        *outLen = 0;

    std::optional<std::string> bytes = Backend().FileBytesAt(Str(root), Str(sha), Str(path));
    if (!bytes)
        return nullptr;

    // DupString copies bytes->size() bytes via memcpy (binary-safe) plus a trailing NUL; the
    // caller uses *outLen, not strlen, so embedded NULs in image data survive the FFI boundary.
    char* p = DupString(*bytes);
    if (p && outLen)
        *outLen = static_cast<int>(bytes->size());
    return p;
}

// ---- Staging & commit (Phase 4) ----------------------------------------------------------

extern "C" MASTERSPLINTERLOGIC_API char* MsGitStagePaths(const char* root, const char* paths)
{
    return DupString(Backend().StagePaths(Str(root), SplitPaths(paths)));
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitStageAll(const char* root)
{
    return DupString(Backend().StageAll(Str(root)));
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitUnstagePaths(const char* root, const char* paths)
{
    return DupString(Backend().UnstagePaths(Str(root), SplitPaths(paths)));
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitDiscardPaths(const char* root, const char* paths)
{
    return DupString(Backend().DiscardPaths(Str(root), SplitPaths(paths)));
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitCommit(const char* root, const char* message, bool amend)
{
    return DupString(Backend().Commit(Str(root), Str(message), amend));
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitHeadMessage(const char* root)
{
    return DupString(Backend().HeadMessage(Str(root)));
}

// ---- Branches & tags (Phase 5) ------------------------------------------------------------

extern "C" MASTERSPLINTERLOGIC_API char* MsGitCheckout(const char* root, const char* refName, bool detach)
{
    return DupString(Backend().Checkout(Str(root), Str(refName), detach));
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitCreateBranch(const char* root, const char* name,
                                                           const char* startPoint, bool checkout)
{
    return DupString(Backend().CreateBranch(Str(root), Str(name), Str(startPoint), checkout));
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitDeleteBranch(const char* root, const char* name, bool force)
{
    return DupString(Backend().DeleteBranch(Str(root), Str(name), force));
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitRenameBranch(const char* root, const char* oldName,
                                                           const char* newName)
{
    return DupString(Backend().RenameBranch(Str(root), Str(oldName), Str(newName)));
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitCreateTag(const char* root, const char* name,
                                                        const char* commitish, const char* message)
{
    return DupString(Backend().CreateTag(Str(root), Str(name), Str(commitish), Str(message)));
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitDeleteTag(const char* root, const char* name)
{
    return DupString(Backend().DeleteTag(Str(root), Str(name)));
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitAheadBehind(const char* root, const char* a, const char* b)
{
    return DupString(Backend().AheadBehind(Str(root), Str(a), Str(b)));
}

// ---- Remotes (Phase 6) ---------------------------------------------------------------------

extern "C" MASTERSPLINTERLOGIC_API char* MsGitRemotes(const char* root)
{
    return DupString(Backend().Remotes(Str(root)));
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitSetRemoteUrl(const char* root, const char* name,
                                                           const char* url, bool pushUrl)
{
    return DupString(Backend().SetRemoteUrl(Str(root), Str(name), Str(url), pushUrl));
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitFetch(const char* root, const char* remote,
                                                    bool allRemotes, bool prune, bool tags,
                                                    MsGitProgressFn cb, void* userData)
{
    return DupString(Backend().Fetch(Str(root), Str(remote), allRemotes, prune, tags,
                                     MakeSink(cb, userData)));
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitPull(const char* root, const char* remote,
                                                   const char* branch,
                                                   MsGitProgressFn cb, void* userData)
{
    return DupString(Backend().Pull(Str(root), Str(remote), Str(branch), MakeSink(cb, userData)));
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitPush(const char* root, const char* remote,
                                                   const char* branch, bool setUpstream,
                                                   bool pushTags,
                                                   MsGitProgressFn cb, void* userData)
{
    return DupString(Backend().Push(Str(root), Str(remote), Str(branch), setUpstream, pushTags,
                                    MakeSink(cb, userData)));
}

// ---- Merge / rebase / cherry-pick / revert (Phase 7) ----------------------------------------

extern "C" MASTERSPLINTERLOGIC_API char* MsGitMerge(const char* root, const char* refName,
                                                    bool noFastForward, bool noCommit,
                                                    MsGitProgressFn cb, void* userData)
{
    return DupString(Backend().Merge(Str(root), Str(refName), noFastForward, noCommit,
                                     MakeSink(cb, userData)));
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitRebase(const char* root, const char* upstream,
                                                     MsGitProgressFn cb, void* userData)
{
    return DupString(Backend().Rebase(Str(root), Str(upstream), MakeSink(cb, userData)));
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitCherryPick(const char* root, const char* shas,
                                                         bool noCommit,
                                                         MsGitProgressFn cb, void* userData)
{
    // SplitPaths is the same 0x1E splitter the staging calls use; the payload here is commit ids
    // rather than paths, but the separator contract is identical.
    return DupString(Backend().CherryPick(Str(root), SplitPaths(shas), noCommit,
                                          MakeSink(cb, userData)));
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitRevert(const char* root, const char* sha,
                                                     int mainline, bool noCommit,
                                                     MsGitProgressFn cb, void* userData)
{
    return DupString(Backend().Revert(Str(root), Str(sha), mainline, noCommit,
                                      MakeSink(cb, userData)));
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitSequencerAction(const char* root,
                                                              const char* operation,
                                                              const char* action,
                                                              MsGitProgressFn cb, void* userData)
{
    return DupString(Backend().SequencerAction(Str(root), Str(operation), Str(action),
                                               MakeSink(cb, userData)));
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitMergeTool(const char* root, const char* path,
                                                        const char* tool,
                                                        MsGitProgressFn cb, void* userData)
{
    return DupString(Backend().MergeTool(Str(root), Str(path), Str(tool), MakeSink(cb, userData)));
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitRepositoryState(const char* root)
{
    return DupString(Backend().RepositoryState(Str(root)));
}

// ---- Stash, blame, search, reflog (Phase 8) -------------------------------------------------

extern "C" MASTERSPLINTERLOGIC_API char* MsGitStashList(const char* root)
{
    return DupString(Backend().StashList(Str(root)));
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitStashSave(const char* root, const char* message,
                                                        bool includeUntracked, bool keepIndex)
{
    return DupString(Backend().StashSave(Str(root), Str(message), includeUntracked, keepIndex));
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitStashApply(const char* root, const char* ref)
{
    return DupString(Backend().StashApply(Str(root), Str(ref)));
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitStashPop(const char* root, const char* ref)
{
    return DupString(Backend().StashPop(Str(root), Str(ref)));
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitStashDrop(const char* root, const char* ref)
{
    return DupString(Backend().StashDrop(Str(root), Str(ref)));
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitBlame(const char* root, const char* rev,
                                                    const char* path, bool ignoreWhitespace,
                                                    const char* detectMoves)
{
    return DupString(Backend().Blame(Str(root), Str(rev), Str(path), ignoreWhitespace,
                                     Str(detectMoves)));
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitSearchLog(const char* root, const char* mode,
                                                        const char* query, const char* pathFilter,
                                                        int order, int maxCount, bool matchCase,
                                                        bool useRegex, bool allBranches)
{
    return DupString(Backend().SearchLog(Str(root), Str(mode), Str(query), Str(pathFilter),
                                         order, maxCount, matchCase, useRegex, allBranches));
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitReflog(const char* root, const char* ref,
                                                     int maxCount)
{
    return DupString(Backend().Reflog(Str(root), Str(ref), maxCount));
}

extern "C" MASTERSPLINTERLOGIC_API void MsGitFree(char* ptr)
{
    free(ptr);
}
