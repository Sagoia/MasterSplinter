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

	// ---- Read-only git backend (implemented in GitBackend.cpp) ------------------------------
	// All strings are UTF-8. Every char* return is heap-allocated by this DLL and MUST be
	// released by the caller via MsGitFree() (never freed across the FFI boundary in C#).
	// These shell out to git.exe (resolved via PATH), mirroring how TortoiseGit reads data.
	//
	// Field separator inside a record is 0x1F; records are separated by 0x1E.

	// True if 'path' is inside a git work tree (git rev-parse --is-inside-work-tree).
	MASTERSPLINTERLOGIC_API bool MsGitIsRepository(const char* path);

	// "OK\x1f<toplevel>\x1f<branch>" on success, or "ERR\x1f<message>" if not a repository.
	MASTERSPLINTERLOGIC_API char* MsGitOpenRepository(const char* path);

	// One record per commit (separated by 0x1E); fields (separated by 0x1F) are:
	// fullHash, shortHash, parents, authorName, authorEmail, authorDateISO,
	// committerName, committerEmail, committerDateISO, refDecorations, subject, body.
	// order: 0=date, 1=topo, 2=reverse-date, 3=author-date. maxCount<=0 means no limit.
	MASTERSPLINTERLOGIC_API char* MsGitLog(const char* root, int order, int maxCount);

	// Newline-free list of full ref names (refs/heads, refs/tags, refs/remotes), 0x1E-separated.
	MASTERSPLINTERLOGIC_API char* MsGitRefs(const char* root);

	// Tab-separated git name-status for one commit: "<status>\t<path>[\t<newPath>]" per line.
	MASTERSPLINTERLOGIC_API char* MsGitCommitFiles(const char* root, const char* sha);

	// One-line "--shortstat" summary for a commit ("N files changed, X insertions(+), Y deletions(-)").
	MASTERSPLINTERLOGIC_API char* MsGitCommitShortStat(const char* root, const char* sha);

	// Unified diff text for one file in one commit (no commit header).
	// wsMode: 0 = honor whitespace, 1 = --ignore-space-change, 2 = --ignore-all-space.
	MASTERSPLINTERLOGIC_API char* MsGitFileDiff(const char* root, const char* sha, const char* path, int wsMode);

	// Full file content as of that commit (git show <sha>:<path>); empty if absent.
	MASTERSPLINTERLOGIC_API char* MsGitFileAtCommit(const char* root, const char* sha, const char* path);

	// ---- Compare two commits / refs (a..b) — also used for branch/tag/HEAD comparison ----------
	// a and b may be full SHAs OR ref names (branch/tag/HEAD); git diff accepts either.

	// Tab-separated name-status for the diff between a and b ("<status>\t<path>[\t<newPath>]").
	MASTERSPLINTERLOGIC_API char* MsGitRangeFiles(const char* root, const char* a, const char* b);

	// One-line "--shortstat" summary for the diff between a and b.
	MASTERSPLINTERLOGIC_API char* MsGitRangeShortStat(const char* root, const char* a, const char* b);

	// Unified diff text for one file between a and b. wsMode as in MsGitFileDiff.
	MASTERSPLINTERLOGIC_API char* MsGitRangeFileDiff(const char* root, const char* a, const char* b,
	                                                 const char* path, int wsMode);

	// Raw bytes of a file as of a commit/ref (git show <sha>:<path>), for binary/image previews.
	// Unlike the char*-as-string returns, the payload MAY contain NUL bytes; *outLen holds the
	// length and the caller must copy exactly that many bytes (not strlen). Returns nullptr on error.
	MASTERSPLINTERLOGIC_API char* MsGitFileBytesAtCommit(const char* root, const char* sha,
	                                                     const char* path, int* outLen);

	// Frees any char* returned by the MsGit* functions above.
	MASTERSPLINTERLOGIC_API void MsGitFree(char* ptr);
}
