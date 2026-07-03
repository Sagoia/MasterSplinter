// MasterSplinter.Logic.cpp : the DLL lifecycle / version part of the flat C ABI.
//
// This is portable (no <windows.h>, PrecompiledHeader=NotUsing): the same source builds the
// .dll on Windows and a .dylib on macOS. The Windows-only pieces are isolated in
// dllmain.cpp and Platform/Windows/*. The read-only git C ABI lives in GitApi.cpp; the actual
// git command building is in Git/GitBackend.cpp, and process launch behind Platform/IProcessRunner.

#include "MasterSplinter.Logic.h"

// Backend lifecycle helpers implemented in GitApi.cpp (wires up the platform Abstract Factory
// -> IProcessRunner -> GitBackend). Declared here to avoid pulling GitBackend into this TU.
namespace msapi
{
    void InitBackend();
    void ShutdownBackend();
}

namespace
{
    // Module-wide state managed by the explicit lifecycle below (NOT by DllMain).
    bool g_initialized = false;
}

extern "C" MASTERSPLINTERLOGIC_API bool MsLogicInitialize(void)
{
    if (g_initialized)
        return true;
    // Real one-time setup: build the process-wide git backend for this platform now, so the
    // first git call does not pay for it (it is lazy-safe either way). Runs outside the loader
    // lock, so this is the right place for it — never in DllMain.
    msapi::InitBackend();
    g_initialized = true;
    return true;
}

extern "C" MASTERSPLINTERLOGIC_API void MsLogicShutdown(void)
{
    if (!g_initialized)
        return;
    // Release everything acquired in MsLogicInitialize().
    msapi::ShutdownBackend();
    g_initialized = false;
}

extern "C" MASTERSPLINTERLOGIC_API const char* MsLogicVersion(void)
{
    return "MasterSplinter.Logic 0.1 (C++ core)";
}

extern "C" MASTERSPLINTERLOGIC_API int MsLogicAdd(int a, int b)
{
    return a + b;
}
