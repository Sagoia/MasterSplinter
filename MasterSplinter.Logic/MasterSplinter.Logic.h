// The following ifdef block is the standard way of creating macros which make exporting
// from a DLL simpler. All files within this DLL are compiled with the MASTERSPLINTERLOGIC_EXPORTS
// symbol defined on the command line. This symbol should not be defined on any project
// that uses this DLL. This way any other project whose source files include this file see
// MASTERSPLINTERLOGIC_API functions as being imported from a DLL, whereas this DLL sees symbols
// defined with this macro as being exported.
#ifdef MASTERSPLINTERLOGIC_EXPORTS
#define MASTERSPLINTERLOGIC_API __declspec(dllexport)
#else
#define MASTERSPLINTERLOGIC_API __declspec(dllimport)
#endif

// ---------------------------------------------------------------------------------------------
// Flat C ABI — this is the interop boundary consumed by the C# app via P/Invoke, and the same
// boundary any other OS's FFI can call. Keep it `extern "C"` (no name mangling, C-compatible
// types only) and keep the real, portable logic behind it (no <windows.h> in the core).
// ---------------------------------------------------------------------------------------------
extern "C" {
	// ---- Lifecycle (do DLL/state setup HERE, not in DllMain) --------------------------------
	// Call MsLogicInitialize() once after the library loads, before any other call; call
	// MsLogicShutdown() once at exit. These are portable (identical on every OS) and run outside
	// the loader lock, so real one-time work (opening libgit2, warming caches, etc.) is safe here.
	MASTERSPLINTERLOGIC_API bool MsLogicInitialize(void);
	MASTERSPLINTERLOGIC_API void MsLogicShutdown(void);

	// Returns a static, null-terminated UTF-8 version string. The caller must NOT free it.
	MASTERSPLINTERLOGIC_API const char* MsLogicVersion(void);

	// Trivial sample so the round-trip is verifiable from C#.
	MASTERSPLINTERLOGIC_API int MsLogicAdd(int a, int b);
}
