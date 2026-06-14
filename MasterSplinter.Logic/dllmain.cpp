// dllmain.cpp : Defines the entry point for the DLL application.
#include "pch.h"

// DllMain runs under the Windows *loader lock*, so it must stay tiny: no real initialization, no
// calls into other DLLs, no thread/COM work. It is also Windows-only (no equivalent on Linux or
// macOS). Real, portable lifecycle therefore lives in MsLogicInitialize()/MsLogicShutdown()
// (see MasterSplinter.Logic.cpp), which the host calls explicitly. DllMain is used here only for
// the one thing it's well-suited to: opting out of per-thread loader callbacks.
BOOL APIENTRY DllMain(HMODULE hModule,
                      DWORD  ul_reason_for_call,
                      LPVOID lpReserved)
{
    switch (ul_reason_for_call)
    {
    case DLL_PROCESS_ATTACH:
        // We do no per-thread setup, so skip THREAD_ATTACH/DETACH notifications (small perf win).
        DisableThreadLibraryCalls(hModule);
        break;
    case DLL_PROCESS_DETACH:
        // Last-resort only. Prefer MsLogicShutdown(); DETACH is loader-locked and may be skipped
        // on fast process exit (lpReserved != nullptr).
        break;
    }
    return TRUE;
}
