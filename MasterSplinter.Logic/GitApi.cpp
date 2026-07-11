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

#include <cstdlib>
#include <cstring>
#include <memory>
#include <mutex>
#include <optional>
#include <string>

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

extern "C" MASTERSPLINTERLOGIC_API char* MsGitRefs(const char* root)
{
    return DupString(Backend().Refs(Str(root)));
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

extern "C" MASTERSPLINTERLOGIC_API void MsGitFree(char* ptr)
{
    free(ptr);
}
