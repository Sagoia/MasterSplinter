// MasterSplinter.Logic.cpp : Defines the exported functions for the DLL.
//

#include "pch.h"
#include "MasterSplinter.Logic.h"

// NOTE: This file is the Windows DLL export layer only. The real cross-platform logic should
// live in plain .cpp/.h files that do NOT include <windows.h>, so the same sources compile into
// a .so/.dylib on Linux/macOS behind this same flat C ABI.

namespace
{
    // Module-wide state managed by the explicit lifecycle below (NOT by DllMain).
    bool g_initialized = false;
}

extern "C" MASTERSPLINTERLOGIC_API bool MsLogicInitialize(void)
{
    if (g_initialized)
        return true;
    // TODO: real one-time setup goes here (e.g. open a libgit2 session, build caches).
    g_initialized = true;
    return true;
}

extern "C" MASTERSPLINTERLOGIC_API void MsLogicShutdown(void)
{
    if (!g_initialized)
        return;
    // TODO: release everything acquired in MsLogicInitialize().
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
