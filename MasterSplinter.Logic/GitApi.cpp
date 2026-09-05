// GitApi.cpp — the flat C ABI (declared in MasterSplinter.Logic.h) consumed by C# via P/Invoke.
//
// These are thin shims. They own the only three things that must stay at the boundary: the char*
// heap ownership (DupString / MsGitFree), the inbound ABI-type -> GitBackend-type conversion, and
// the exception guard. All git work is delegated to a single process-wide GitBackend, which is
// wired to this OS's IProcessRunner through the platform Abstract Factory (CreatePlatformFactory).
//
// KEEP PORTABLE: no <windows.h> here. Compiled with PrecompiledHeader=NotUsing.

#include "MasterSplinter.Logic.h"

#include "Git/GitBackend.h"
#include "Platform/IPlatformFactory.h"

#include <atomic>
#include <cstddef>
#include <cstdlib>
#include <cstring>
#include <memory>
#include <mutex>
#include <optional>
#include <string>
#include <utility>
#include <vector>

namespace
{
    // ---- Backend lifetime ------------------------------------------------------------------
    //
    // Lazily created, process-wide. Built on first use (defensive: the C ABI works even if
    // MsLogicInitialize was not called first) and rebuildable after a shutdown, which is why this
    // is not a plain function-local static.
    //
    // The host issues these calls from thread-pool threads (Task.Run), so construction is guarded.
    // The guard is double-checked: once built, the hot path is a single acquire load rather than a
    // mutex acquisition on EVERY ABI call. Teardown is not concurrency-safe against in-flight
    // calls and never was — MsLogicShutdown runs at exit, after the UI has stopped issuing work.
    std::unique_ptr<ms::GitBackend> g_backend;
    std::atomic<ms::GitBackend*> g_ready{ nullptr };
    std::mutex g_backendMutex;

    ms::GitBackend& Backend()
    {
        if (ms::GitBackend* ready = g_ready.load(std::memory_order_acquire))
            return *ready;

        std::lock_guard<std::mutex> lock(g_backendMutex);
        if (!g_backend)
        {
            std::unique_ptr<ms::IPlatformFactory> factory = ms::CreatePlatformFactory();
            g_backend = std::make_unique<ms::GitBackend>(factory->CreateProcessRunner());
        }
        g_ready.store(g_backend.get(), std::memory_order_release);
        return *g_backend;
    }

    // ---- Heap ownership --------------------------------------------------------------------

    // Heap copy the caller frees via MsGitFree (allocated inside this DLL, freed inside it).
    // memcpy (not strcpy) so embedded NULs survive for the binary byte payloads. noexcept and
    // allocation-free apart from the malloc, so it is safe to call from a catch block.
    char* DupBytes(const char* data, std::size_t size) noexcept
    {
        char* p = static_cast<char*>(malloc(size + 1));
        if (!p)
            return nullptr;
        if (size)
            memcpy(p, data, size);
        p[size] = '\0';
        return p;
    }

    char* DupString(const std::string& s) noexcept { return DupBytes(s.data(), s.size()); }

    // What a guarded call returns when the C++ side throws, per the two ABI conventions
    // (see docs/abi.md). Plain reads signal failure with an empty string; OK/ERR-framed calls
    // need a message, and "internal" is honest — this path means a bug or an allocation failure,
    // not something git said.
    constexpr char kInternalError[] = "ERR\x1f" "The operation failed unexpectedly.";

    // ---- Inbound argument adaptation -------------------------------------------------------
    //
    // Tag types for the two ABI arguments whose C type does not say what they mean: a 0x1E-joined
    // list, and the (callback, userData) progress pair.
    struct Joined { const char* value; };                       // 0x1E-separated list
    struct Sink { MsGitProgressFn cb; void* userData; };         // progress callback pair

    // const char* -> std::string (nullptr -> empty); scalars pass through. Every overload runs
    // INSIDE the exception guard below, which is why conversion is not done at the call site.
    std::string Adapt(const char* p) { return p ? std::string(p) : std::string(); }
    int Adapt(int v) noexcept { return v; }
    bool Adapt(bool v) noexcept { return v; }

    // Split a 0x1E-separated list into the vector the backend takes. Empty segments are dropped,
    // so a trailing separator is harmless. Used for path lists AND for the cherry-pick commit-id
    // list — different payloads, identical separator contract.
    std::vector<std::string> Adapt(Joined joined)
    {
        std::vector<std::string> result;
        std::string s = Adapt(joined.value);
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
    ms::GitBackend::ProgressSink Adapt(Sink s)
    {
        if (!s.cb)
            return {};
        return [cb = s.cb, userData = s.userData](const char* bytes, std::size_t length) {
            return cb(userData, bytes, static_cast<int>(length)) != 0;
        };
    }

    // ---- The exception guard ---------------------------------------------------------------
    //
    // An exception unwinding across the C ABI is undefined behavior and would take the host
    // process down, so NOTHING may escape an entry point. GitBackend does not throw deliberately,
    // but std::string/std::vector growth and std::filesystem both can — and this is the last
    // frame that can still turn that into a value the host understands.
    //
    // Argument adaptation happens inside the try, so a bad_alloc converting an inbound string is
    // caught too. `fallback` is the convention-appropriate failure value.
    template <typename Method, typename... Args>
    char* Call(const char* fallback, std::size_t fallbackSize, Method method, Args... args) noexcept
    {
        try
        {
            return DupString((Backend().*method)(Adapt(args)...));
        }
        catch (...)
        {
            return DupBytes(fallback, fallbackSize);
        }
    }

    // A plain read: empty string means failure.
    template <typename Method, typename... Args>
    char* CallRead(Method method, Args... args) noexcept
    {
        return Call("", 0, method, args...);
    }

    // An OK/ERR-framed call: failure carries a message.
    template <typename Method, typename... Args>
    char* CallWrite(Method method, Args... args) noexcept
    {
        return Call(kInternalError, sizeof(kInternalError) - 1, method, args...);
    }
}

// Backend lifecycle hooks, called from MsLogicInitialize / MsLogicShutdown (MasterSplinter.Logic.cpp).
namespace msapi
{
    void InitBackend() { (void)Backend(); }

    void ShutdownBackend()
    {
        std::lock_guard<std::mutex> lock(g_backendMutex);
        g_ready.store(nullptr, std::memory_order_release);
        g_backend.reset();
    }
}

// ------------------------------------------------------------------------------------------
// Flat C ABI. Each function delegates to GitBackend through the guard above; the guard variant
// (CallRead / CallWrite) is what declares which return convention the export follows.
// ------------------------------------------------------------------------------------------

// ---- Repository ----------------------------------------------------------------------------

extern "C" MASTERSPLINTERLOGIC_API bool MsGitIsRepository(const char* path)
{
    // Not a char* return, so it cannot use the shared guard: false is its failure value.
    try
    {
        return Backend().IsRepository(Adapt(path));
    }
    catch (...)
    {
        return false;
    }
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitOpenRepository(const char* path)
{
    return CallWrite(&ms::GitBackend::OpenRepository, path);
}

// ---- History -------------------------------------------------------------------------------

extern "C" MASTERSPLINTERLOGIC_API char* MsGitLog(const char* root, int order, int maxCount)
{
    return CallRead(&ms::GitBackend::Log, root, order, maxCount);
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitRefDetails(const char* root)
{
    return CallRead(&ms::GitBackend::RefDetails, root);
}

// ---- Commit inspection ---------------------------------------------------------------------

extern "C" MASTERSPLINTERLOGIC_API char* MsGitCommitFiles(const char* root, const char* sha)
{
    return CallRead(&ms::GitBackend::CommitFiles, root, sha);
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitCommitShortStat(const char* root, const char* sha)
{
    return CallRead(&ms::GitBackend::CommitShortStat, root, sha);
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitFileDiff(const char* root, const char* sha, const char* path, int wsMode)
{
    return CallRead(&ms::GitBackend::FileDiff, root, sha, path, wsMode);
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitFileAtCommit(const char* root, const char* sha, const char* path)
{
    return CallRead(&ms::GitBackend::FileAtCommit, root, sha, path);
}

// ---- Compare two commits / refs --------------------------------------------------------------

extern "C" MASTERSPLINTERLOGIC_API char* MsGitRangeFiles(const char* root, const char* a, const char* b)
{
    return CallRead(&ms::GitBackend::RangeFiles, root, a, b);
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitRangeShortStat(const char* root, const char* a, const char* b)
{
    return CallRead(&ms::GitBackend::RangeShortStat, root, a, b);
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitRangeFileDiff(const char* root, const char* a, const char* b,
                                                            const char* path, int wsMode)
{
    return CallRead(&ms::GitBackend::RangeFileDiff, root, a, b, path, wsMode);
}

// ---- Working tree (Phase 3) ------------------------------------------------------------------

extern "C" MASTERSPLINTERLOGIC_API char* MsGitStatus(const char* root)
{
    return CallRead(&ms::GitBackend::Status, root);
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitWorkTreeFileDiff(const char* root, const char* path,
                                                               int area, int wsMode)
{
    return CallRead(&ms::GitBackend::WorkTreeFileDiff, root, path, area, wsMode);
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitFileBytesAtCommit(const char* root, const char* sha,
                                                                const char* path, int* outLen)
{
    // The one binary-safe return, so it cannot use the shared guard: it must publish a length and
    // signal failure with nullptr rather than with an empty string.
    if (outLen)
        *outLen = 0;

    try
    {
        std::optional<std::string> bytes = Backend().FileBytesAt(Adapt(root), Adapt(sha), Adapt(path));
        if (!bytes)
            return nullptr;

        // DupBytes copies bytes->size() bytes via memcpy (binary-safe) plus a trailing NUL; the
        // caller uses *outLen, not strlen, so embedded NULs in image data survive the boundary.
        char* p = DupBytes(bytes->data(), bytes->size());
        if (p && outLen)
            *outLen = static_cast<int>(bytes->size());
        return p;
    }
    catch (...)
    {
        return nullptr;
    }
}

// ---- Staging & commit (Phase 4) ----------------------------------------------------------

extern "C" MASTERSPLINTERLOGIC_API char* MsGitStagePaths(const char* root, const char* paths)
{
    return CallWrite(&ms::GitBackend::StagePaths, root, Joined{ paths });
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitStageAll(const char* root)
{
    return CallWrite(&ms::GitBackend::StageAll, root);
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitUnstagePaths(const char* root, const char* paths)
{
    return CallWrite(&ms::GitBackend::UnstagePaths, root, Joined{ paths });
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitDiscardPaths(const char* root, const char* paths)
{
    return CallWrite(&ms::GitBackend::DiscardPaths, root, Joined{ paths });
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitCommit(const char* root, const char* message, bool amend)
{
    return CallWrite(&ms::GitBackend::Commit, root, message, amend);
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitHeadMessage(const char* root)
{
    return CallWrite(&ms::GitBackend::HeadMessage, root);
}

// ---- Branches & tags (Phase 5) ------------------------------------------------------------

extern "C" MASTERSPLINTERLOGIC_API char* MsGitCheckout(const char* root, const char* refName, bool detach)
{
    return CallWrite(&ms::GitBackend::Checkout, root, refName, detach);
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitCreateBranch(const char* root, const char* name,
                                                           const char* startPoint, bool checkout)
{
    return CallWrite(&ms::GitBackend::CreateBranch, root, name, startPoint, checkout);
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitDeleteBranch(const char* root, const char* name, bool force)
{
    return CallWrite(&ms::GitBackend::DeleteBranch, root, name, force);
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitRenameBranch(const char* root, const char* oldName,
                                                           const char* newName)
{
    return CallWrite(&ms::GitBackend::RenameBranch, root, oldName, newName);
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitCreateTag(const char* root, const char* name,
                                                        const char* commitish, const char* message)
{
    return CallWrite(&ms::GitBackend::CreateTag, root, name, commitish, message);
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitDeleteTag(const char* root, const char* name)
{
    return CallWrite(&ms::GitBackend::DeleteTag, root, name);
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitAheadBehind(const char* root, const char* a, const char* b)
{
    return CallRead(&ms::GitBackend::AheadBehind, root, a, b);
}

// ---- Remotes (Phase 6) ---------------------------------------------------------------------

extern "C" MASTERSPLINTERLOGIC_API char* MsGitRemotes(const char* root)
{
    return CallRead(&ms::GitBackend::Remotes, root);
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitSetRemoteUrl(const char* root, const char* name,
                                                           const char* url, bool pushUrl)
{
    return CallWrite(&ms::GitBackend::SetRemoteUrl, root, name, url, pushUrl);
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitFetch(const char* root, const char* remote,
                                                    bool allRemotes, bool prune, bool tags,
                                                    MsGitProgressFn cb, void* userData)
{
    return CallWrite(&ms::GitBackend::Fetch, root, remote, allRemotes, prune, tags,
                     Sink{ cb, userData });
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitPull(const char* root, const char* remote,
                                                   const char* branch,
                                                   MsGitProgressFn cb, void* userData)
{
    return CallWrite(&ms::GitBackend::Pull, root, remote, branch, Sink{ cb, userData });
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitPush(const char* root, const char* remote,
                                                   const char* branch, bool setUpstream,
                                                   bool pushTags,
                                                   MsGitProgressFn cb, void* userData)
{
    return CallWrite(&ms::GitBackend::Push, root, remote, branch, setUpstream, pushTags,
                     Sink{ cb, userData });
}

// ---- Merge / rebase / cherry-pick / revert (Phase 7) ----------------------------------------

extern "C" MASTERSPLINTERLOGIC_API char* MsGitMerge(const char* root, const char* refName,
                                                    bool noFastForward, bool noCommit,
                                                    MsGitProgressFn cb, void* userData)
{
    return CallWrite(&ms::GitBackend::Merge, root, refName, noFastForward, noCommit,
                     Sink{ cb, userData });
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitRebase(const char* root, const char* upstream,
                                                     MsGitProgressFn cb, void* userData)
{
    return CallWrite(&ms::GitBackend::Rebase, root, upstream, Sink{ cb, userData });
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitCherryPick(const char* root, const char* shas,
                                                         bool noCommit,
                                                         MsGitProgressFn cb, void* userData)
{
    // Joined is the same 0x1E splitter the staging calls use; the payload here is commit ids
    // rather than paths, but the separator contract is identical.
    return CallWrite(&ms::GitBackend::CherryPick, root, Joined{ shas }, noCommit,
                     Sink{ cb, userData });
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitRevert(const char* root, const char* sha,
                                                     int mainline, bool noCommit,
                                                     MsGitProgressFn cb, void* userData)
{
    return CallWrite(&ms::GitBackend::Revert, root, sha, mainline, noCommit, Sink{ cb, userData });
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitSequencerAction(const char* root,
                                                              const char* operation,
                                                              const char* action,
                                                              MsGitProgressFn cb, void* userData)
{
    return CallWrite(&ms::GitBackend::SequencerAction, root, operation, action,
                     Sink{ cb, userData });
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitMergeTool(const char* root, const char* path,
                                                        const char* tool,
                                                        MsGitProgressFn cb, void* userData)
{
    return CallWrite(&ms::GitBackend::MergeTool, root, path, tool, Sink{ cb, userData });
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitRepositoryState(const char* root)
{
    return CallWrite(&ms::GitBackend::RepositoryState, root);
}

// ---- Stash, blame, search, reflog (Phase 8) -------------------------------------------------

extern "C" MASTERSPLINTERLOGIC_API char* MsGitStashList(const char* root)
{
    return CallRead(&ms::GitBackend::StashList, root);
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitStashSave(const char* root, const char* message,
                                                        bool includeUntracked, bool keepIndex)
{
    return CallWrite(&ms::GitBackend::StashSave, root, message, includeUntracked, keepIndex);
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitStashApply(const char* root, const char* ref)
{
    return CallWrite(&ms::GitBackend::StashApply, root, ref);
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitStashPop(const char* root, const char* ref)
{
    return CallWrite(&ms::GitBackend::StashPop, root, ref);
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitStashDrop(const char* root, const char* ref)
{
    return CallWrite(&ms::GitBackend::StashDrop, root, ref);
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitBlame(const char* root, const char* rev,
                                                    const char* path, bool ignoreWhitespace,
                                                    const char* detectMoves)
{
    return CallWrite(&ms::GitBackend::Blame, root, rev, path, ignoreWhitespace, detectMoves);
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitSearchLog(const char* root, const char* mode,
                                                        const char* query, const char* pathFilter,
                                                        int order, int maxCount, bool matchCase,
                                                        bool useRegex, bool allBranches)
{
    return CallRead(&ms::GitBackend::SearchLog, root, mode, query, pathFilter,
                    order, maxCount, matchCase, useRegex, allBranches);
}

extern "C" MASTERSPLINTERLOGIC_API char* MsGitReflog(const char* root, const char* ref,
                                                     int maxCount)
{
    return CallRead(&ms::GitBackend::Reflog, root, ref, maxCount);
}

extern "C" MASTERSPLINTERLOGIC_API void MsGitFree(char* ptr)
{
    free(ptr);
}
