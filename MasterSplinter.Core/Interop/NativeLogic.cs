using System;
using System.Runtime.InteropServices;

namespace MasterSplinter.Entrypoint.Interop
{
    /// <summary>
    /// P/Invoke bindings to the cross-platform C++ core (MasterSplinter.Logic, a native DLL).
    /// This is the only place that knows about the native boundary; ViewModels call these methods,
    /// keeping the rest of the app in clean MVVM C#.
    /// </summary>
    internal static class NativeLogic
    {
        // Matches TargetName "MasterSplinterLogic" in the vcxproj. The DLL is copied next to the
        // app by the ProjectReference (OutputItemType=Content), so a bare name resolves it.
        private const string Dll = "MasterSplinterLogic.dll";

        // ---- Lifecycle: call Initialize() once at startup, Shutdown() once at exit ----------
        // C++ `bool` is 1 byte, so marshal the return as I1 (not the default 4-byte Win32 BOOL).
        [DllImport(Dll, EntryPoint = "MsLogicInitialize", CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool Initialize();

        [DllImport(Dll, EntryPoint = "MsLogicShutdown", CallingConvention = CallingConvention.Cdecl)]
        public static extern void Shutdown();

        // extern "C" => __cdecl on x86; x64/ARM64 have a single calling convention.
        [DllImport(Dll, EntryPoint = "MsLogicVersion", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr MsLogicVersion();

        [DllImport(Dll, EntryPoint = "MsLogicAdd", CallingConvention = CallingConvention.Cdecl)]
        public static extern int Add(int a, int b);

        /// <summary>Version string from the C++ core. Returned pointer is static; we copy, never free.</summary>
        public static string Version() => Marshal.PtrToStringAnsi(MsLogicVersion()) ?? "(unknown)";

        // ---- Read-only git backend (GitBackend.cpp) -----------------------------------------
        // Strings are UTF-8 in both directions. char* returns are heap-allocated by the DLL;
        // TakeString copies then frees them via MsGitFree (never free across the boundary here).

        [DllImport(Dll, EntryPoint = "MsGitIsRepository", CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool GitIsRepository([MarshalAs(UnmanagedType.LPUTF8Str)] string path);

        [DllImport(Dll, EntryPoint = "MsGitOpenRepository", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr MsGitOpenRepository([MarshalAs(UnmanagedType.LPUTF8Str)] string path);

        [DllImport(Dll, EntryPoint = "MsGitLog", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr MsGitLog([MarshalAs(UnmanagedType.LPUTF8Str)] string root, int order,
                                              int maxCount, out int len);

        [DllImport(Dll, EntryPoint = "MsGitRefDetails", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr MsGitRefDetails([MarshalAs(UnmanagedType.LPUTF8Str)] string root,
                                                     out int len);

        [DllImport(Dll, EntryPoint = "MsGitCommitFiles", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr MsGitCommitFiles([MarshalAs(UnmanagedType.LPUTF8Str)] string root,
                                                      [MarshalAs(UnmanagedType.LPUTF8Str)] string sha,
                                                      out int len);

        [DllImport(Dll, EntryPoint = "MsGitCommitShortStat", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr MsGitCommitShortStat([MarshalAs(UnmanagedType.LPUTF8Str)] string root,
                                                          [MarshalAs(UnmanagedType.LPUTF8Str)] string sha,
                                                          out int len);

        [DllImport(Dll, EntryPoint = "MsGitFileDiff", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr MsGitFileDiff([MarshalAs(UnmanagedType.LPUTF8Str)] string root,
                                                   [MarshalAs(UnmanagedType.LPUTF8Str)] string sha,
                                                   [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
                                                   int wsMode,
                                                   out int len);

        [DllImport(Dll, EntryPoint = "MsGitFileAtCommit", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr MsGitFileAtCommit([MarshalAs(UnmanagedType.LPUTF8Str)] string root,
                                                       [MarshalAs(UnmanagedType.LPUTF8Str)] string sha,
                                                       [MarshalAs(UnmanagedType.LPUTF8Str)] string path);

        [DllImport(Dll, EntryPoint = "MsGitRangeFiles", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr MsGitRangeFiles([MarshalAs(UnmanagedType.LPUTF8Str)] string root,
                                                     [MarshalAs(UnmanagedType.LPUTF8Str)] string a,
                                                     [MarshalAs(UnmanagedType.LPUTF8Str)] string b,
                                                     out int len);

        [DllImport(Dll, EntryPoint = "MsGitRangeShortStat", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr MsGitRangeShortStat([MarshalAs(UnmanagedType.LPUTF8Str)] string root,
                                                         [MarshalAs(UnmanagedType.LPUTF8Str)] string a,
                                                         [MarshalAs(UnmanagedType.LPUTF8Str)] string b,
                                                         out int len);

        [DllImport(Dll, EntryPoint = "MsGitRangeFileDiff", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr MsGitRangeFileDiff([MarshalAs(UnmanagedType.LPUTF8Str)] string root,
                                                        [MarshalAs(UnmanagedType.LPUTF8Str)] string a,
                                                        [MarshalAs(UnmanagedType.LPUTF8Str)] string b,
                                                        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
                                                        int wsMode,
                                                        out int len);

        [DllImport(Dll, EntryPoint = "MsGitStatus", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr MsGitStatus([MarshalAs(UnmanagedType.LPUTF8Str)] string root,
                                                 out int len);

        [DllImport(Dll, EntryPoint = "MsGitWorkTreeFileDiff", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr MsGitWorkTreeFileDiff([MarshalAs(UnmanagedType.LPUTF8Str)] string root,
                                                           [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
                                                           int area,
                                                           int wsMode,
                                                           out int len);

        [DllImport(Dll, EntryPoint = "MsGitFileBytesAtCommit", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr MsGitFileBytesAtCommit([MarshalAs(UnmanagedType.LPUTF8Str)] string root,
                                                            [MarshalAs(UnmanagedType.LPUTF8Str)] string sha,
                                                            [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
                                                            out int len);

        // ---- Staging & commit (Phase 4) — the first write operations ------------------------
        // All return "OK" or "ERR\x1f<message>". `paths` is one or more repo-relative paths
        // separated by 0x1E (the record separator used throughout the ABI).

        [DllImport(Dll, EntryPoint = "MsGitStagePaths", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr MsGitStagePaths([MarshalAs(UnmanagedType.LPUTF8Str)] string root,
                                                     [MarshalAs(UnmanagedType.LPUTF8Str)] string paths);

        [DllImport(Dll, EntryPoint = "MsGitStageAll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr MsGitStageAll([MarshalAs(UnmanagedType.LPUTF8Str)] string root);

        [DllImport(Dll, EntryPoint = "MsGitUnstagePaths", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr MsGitUnstagePaths([MarshalAs(UnmanagedType.LPUTF8Str)] string root,
                                                       [MarshalAs(UnmanagedType.LPUTF8Str)] string paths);

        [DllImport(Dll, EntryPoint = "MsGitDiscardPaths", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr MsGitDiscardPaths([MarshalAs(UnmanagedType.LPUTF8Str)] string root,
                                                       [MarshalAs(UnmanagedType.LPUTF8Str)] string paths);

        [DllImport(Dll, EntryPoint = "MsGitCommit", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr MsGitCommit([MarshalAs(UnmanagedType.LPUTF8Str)] string root,
                                                 [MarshalAs(UnmanagedType.LPUTF8Str)] string message,
                                                 [MarshalAs(UnmanagedType.I1)] bool amend);

        [DllImport(Dll, EntryPoint = "MsGitHeadMessage", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr MsGitHeadMessage([MarshalAs(UnmanagedType.LPUTF8Str)] string root);

        // ---- Branches & tags (Phase 5) ------------------------------------------------------
        // Same "OK" / "ERR\x1f<message>" contract as the Phase 4 writes.

        [DllImport(Dll, EntryPoint = "MsGitCheckout", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr MsGitCheckout([MarshalAs(UnmanagedType.LPUTF8Str)] string root,
                                                   [MarshalAs(UnmanagedType.LPUTF8Str)] string refName,
                                                   [MarshalAs(UnmanagedType.I1)] bool detach);

        [DllImport(Dll, EntryPoint = "MsGitCreateBranch", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr MsGitCreateBranch([MarshalAs(UnmanagedType.LPUTF8Str)] string root,
                                                       [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
                                                       [MarshalAs(UnmanagedType.LPUTF8Str)] string startPoint,
                                                       [MarshalAs(UnmanagedType.I1)] bool checkout);

        [DllImport(Dll, EntryPoint = "MsGitDeleteBranch", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr MsGitDeleteBranch([MarshalAs(UnmanagedType.LPUTF8Str)] string root,
                                                       [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
                                                       [MarshalAs(UnmanagedType.I1)] bool force);

        [DllImport(Dll, EntryPoint = "MsGitRenameBranch", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr MsGitRenameBranch([MarshalAs(UnmanagedType.LPUTF8Str)] string root,
                                                       [MarshalAs(UnmanagedType.LPUTF8Str)] string oldName,
                                                       [MarshalAs(UnmanagedType.LPUTF8Str)] string newName);

        [DllImport(Dll, EntryPoint = "MsGitCreateTag", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr MsGitCreateTag([MarshalAs(UnmanagedType.LPUTF8Str)] string root,
                                                    [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
                                                    [MarshalAs(UnmanagedType.LPUTF8Str)] string commitish,
                                                    [MarshalAs(UnmanagedType.LPUTF8Str)] string message);

        [DllImport(Dll, EntryPoint = "MsGitDeleteTag", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr MsGitDeleteTag([MarshalAs(UnmanagedType.LPUTF8Str)] string root,
                                                    [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

        [DllImport(Dll, EntryPoint = "MsGitAheadBehind", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr MsGitAheadBehind([MarshalAs(UnmanagedType.LPUTF8Str)] string root,
                                                      [MarshalAs(UnmanagedType.LPUTF8Str)] string a,
                                                      [MarshalAs(UnmanagedType.LPUTF8Str)] string b);

        // ---- Remotes (Phase 6) --------------------------------------------------------------
        // Same "OK" / "ERR\x1f<message>" contract. The three network commands take a progress
        // callback; see GitFetch/GitPull/GitPush below for the managed-side wrapper.

        /// <summary>
        /// Native progress callback. <paramref name="length"/> is authoritative — the chunk is a
        /// byte run, not a NUL-terminated string — and a length of 0 is the periodic heartbeat
        /// that lets a stalled command still be cancelled. Return 0 to cancel, non-zero to go on.
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int ProgressFn(IntPtr userData, IntPtr bytes, int length);

        [DllImport(Dll, EntryPoint = "MsGitRemotes", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr MsGitRemotes([MarshalAs(UnmanagedType.LPUTF8Str)] string root);

        [DllImport(Dll, EntryPoint = "MsGitSetRemoteUrl", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr MsGitSetRemoteUrl([MarshalAs(UnmanagedType.LPUTF8Str)] string root,
                                                       [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
                                                       [MarshalAs(UnmanagedType.LPUTF8Str)] string url,
                                                       [MarshalAs(UnmanagedType.I1)] bool pushUrl);

        [DllImport(Dll, EntryPoint = "MsGitFetch", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr MsGitFetch([MarshalAs(UnmanagedType.LPUTF8Str)] string root,
                                                [MarshalAs(UnmanagedType.LPUTF8Str)] string remote,
                                                [MarshalAs(UnmanagedType.I1)] bool allRemotes,
                                                [MarshalAs(UnmanagedType.I1)] bool prune,
                                                [MarshalAs(UnmanagedType.I1)] bool tags,
                                                ProgressFn? cb, IntPtr userData);

        [DllImport(Dll, EntryPoint = "MsGitPull", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr MsGitPull([MarshalAs(UnmanagedType.LPUTF8Str)] string root,
                                               [MarshalAs(UnmanagedType.LPUTF8Str)] string remote,
                                               [MarshalAs(UnmanagedType.LPUTF8Str)] string branch,
                                               ProgressFn? cb, IntPtr userData);

        [DllImport(Dll, EntryPoint = "MsGitPush", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr MsGitPush([MarshalAs(UnmanagedType.LPUTF8Str)] string root,
                                               [MarshalAs(UnmanagedType.LPUTF8Str)] string remote,
                                               [MarshalAs(UnmanagedType.LPUTF8Str)] string branch,
                                               [MarshalAs(UnmanagedType.I1)] bool setUpstream,
                                               [MarshalAs(UnmanagedType.I1)] bool pushTags,
                                               ProgressFn? cb, IntPtr userData);

        // ---- Merge / rebase / cherry-pick / revert (Phase 7) --------------------------------
        // Same "OK" / "ERR\x1f<message>" contract and the same progress callback. A conflict comes
        // back as ERR carrying git's "CONFLICT (…)" text — an expected outcome, not a failure.

        [DllImport(Dll, EntryPoint = "MsGitMerge", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr MsGitMerge([MarshalAs(UnmanagedType.LPUTF8Str)] string root,
                                                [MarshalAs(UnmanagedType.LPUTF8Str)] string refName,
                                                [MarshalAs(UnmanagedType.I1)] bool noFastForward,
                                                [MarshalAs(UnmanagedType.I1)] bool noCommit,
                                                ProgressFn? cb, IntPtr userData);

        [DllImport(Dll, EntryPoint = "MsGitRebase", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr MsGitRebase([MarshalAs(UnmanagedType.LPUTF8Str)] string root,
                                                 [MarshalAs(UnmanagedType.LPUTF8Str)] string upstream,
                                                 ProgressFn? cb, IntPtr userData);

        [DllImport(Dll, EntryPoint = "MsGitCherryPick", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr MsGitCherryPick([MarshalAs(UnmanagedType.LPUTF8Str)] string root,
                                                     [MarshalAs(UnmanagedType.LPUTF8Str)] string shas,
                                                     [MarshalAs(UnmanagedType.I1)] bool noCommit,
                                                     ProgressFn? cb, IntPtr userData);

        [DllImport(Dll, EntryPoint = "MsGitRevert", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr MsGitRevert([MarshalAs(UnmanagedType.LPUTF8Str)] string root,
                                                 [MarshalAs(UnmanagedType.LPUTF8Str)] string sha,
                                                 int mainline,
                                                 [MarshalAs(UnmanagedType.I1)] bool noCommit,
                                                 ProgressFn? cb, IntPtr userData);

        [DllImport(Dll, EntryPoint = "MsGitSequencerAction", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr MsGitSequencerAction([MarshalAs(UnmanagedType.LPUTF8Str)] string root,
                                                          [MarshalAs(UnmanagedType.LPUTF8Str)] string operation,
                                                          [MarshalAs(UnmanagedType.LPUTF8Str)] string action,
                                                          ProgressFn? cb, IntPtr userData);

        [DllImport(Dll, EntryPoint = "MsGitMergeTool", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr MsGitMergeTool([MarshalAs(UnmanagedType.LPUTF8Str)] string root,
                                                    [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
                                                    [MarshalAs(UnmanagedType.LPUTF8Str)] string tool,
                                                    ProgressFn? cb, IntPtr userData);

        [DllImport(Dll, EntryPoint = "MsGitRepositoryState", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr MsGitRepositoryState([MarshalAs(UnmanagedType.LPUTF8Str)] string root);

        // ---- Stash, blame, search, reflog (Phase 8) ------------------------------------------

        [DllImport(Dll, EntryPoint = "MsGitStashList", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr MsGitStashList([MarshalAs(UnmanagedType.LPUTF8Str)] string root,
                                                    out int len);

        [DllImport(Dll, EntryPoint = "MsGitStashSave", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr MsGitStashSave([MarshalAs(UnmanagedType.LPUTF8Str)] string root,
                                                    [MarshalAs(UnmanagedType.LPUTF8Str)] string message,
                                                    [MarshalAs(UnmanagedType.I1)] bool includeUntracked,
                                                    [MarshalAs(UnmanagedType.I1)] bool keepIndex);

        [DllImport(Dll, EntryPoint = "MsGitStashApply", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr MsGitStashApply([MarshalAs(UnmanagedType.LPUTF8Str)] string root,
                                                     [MarshalAs(UnmanagedType.LPUTF8Str)] string refName);

        [DllImport(Dll, EntryPoint = "MsGitStashPop", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr MsGitStashPop([MarshalAs(UnmanagedType.LPUTF8Str)] string root,
                                                   [MarshalAs(UnmanagedType.LPUTF8Str)] string refName);

        [DllImport(Dll, EntryPoint = "MsGitStashDrop", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr MsGitStashDrop([MarshalAs(UnmanagedType.LPUTF8Str)] string root,
                                                    [MarshalAs(UnmanagedType.LPUTF8Str)] string refName);

        [DllImport(Dll, EntryPoint = "MsGitBlame", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr MsGitBlame([MarshalAs(UnmanagedType.LPUTF8Str)] string root,
                                                [MarshalAs(UnmanagedType.LPUTF8Str)] string rev,
                                                [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
                                                [MarshalAs(UnmanagedType.I1)] bool ignoreWhitespace,
                                                [MarshalAs(UnmanagedType.LPUTF8Str)] string detectMoves,
                                                out int len);

        [DllImport(Dll, EntryPoint = "MsGitSearchLog", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr MsGitSearchLog([MarshalAs(UnmanagedType.LPUTF8Str)] string root,
                                                    [MarshalAs(UnmanagedType.LPUTF8Str)] string mode,
                                                    [MarshalAs(UnmanagedType.LPUTF8Str)] string query,
                                                    [MarshalAs(UnmanagedType.LPUTF8Str)] string pathFilter,
                                                    int order, int maxCount,
                                                    [MarshalAs(UnmanagedType.I1)] bool matchCase,
                                                    [MarshalAs(UnmanagedType.I1)] bool useRegex,
                                                    [MarshalAs(UnmanagedType.I1)] bool allBranches,
                                                    out int len);

        [DllImport(Dll, EntryPoint = "MsGitReflog", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr MsGitReflog([MarshalAs(UnmanagedType.LPUTF8Str)] string root,
                                                 [MarshalAs(UnmanagedType.LPUTF8Str)] string refName,
                                                 int maxCount,
                                                 out int len);

        [DllImport(Dll, EntryPoint = "MsGitFree", CallingConvention = CallingConvention.Cdecl)]
        private static extern void MsGitFree(IntPtr ptr);

        /// <summary>Copies a UTF-8 string returned by the native git backend, then frees it.</summary>
        private static string TakeString(IntPtr ptr)
        {
            if (ptr == IntPtr.Zero)
                return string.Empty;
            try { return Marshal.PtrToStringUTF8(ptr) ?? string.Empty; }
            finally { MsGitFree(ptr); }
        }

        /// <summary>
        /// Copies a length-prefixed byte payload returned by the native core, then frees it.
        /// <para>
        /// This is the counterpart to <see cref="TakeString"/> for the exports that publish an
        /// explicit length: their payload may contain NULs, so <c>PtrToStringUTF8</c> (which is
        /// strlen-based) would truncate it. Used by the raw file-bytes export and by every packed
        /// export -- see <see cref="PackedBuffer"/>.
        /// </para>
        /// </summary>
        internal static byte[] TakeBytes(IntPtr ptr, int len)
        {
            if (ptr == IntPtr.Zero)
                return Array.Empty<byte>();
            try
            {
                if (len <= 0)
                    return Array.Empty<byte>();
                var buffer = new byte[len];
                Marshal.Copy(ptr, buffer, 0, len);
                return buffer;
            }
            finally { MsGitFree(ptr); }
        }

        public static string GitOpenRepository(string path) => TakeString(MsGitOpenRepository(path));
        /// <summary>Parsed commit records as a packed buffer.</summary>
        public static PackedBuffer GitLog(string root, int order, int maxCount)
        {
            IntPtr ptr = MsGitLog(root, order, maxCount, out int len);
            return PackedBuffer.Wrap(TakeBytes(ptr, len));
        }
        /// <summary>Branches, tags and remote-tracking refs, as a packed buffer.</summary>
        public static PackedBuffer GitRefDetails(string root)
        {
            IntPtr ptr = MsGitRefDetails(root, out int len);
            return PackedBuffer.Wrap(TakeBytes(ptr, len));
        }
        /// <summary>Files changed in one commit, as a packed buffer.</summary>
        public static PackedBuffer GitCommitFiles(string root, string sha)
        {
            IntPtr ptr = MsGitCommitFiles(root, sha, out int len);
            return PackedBuffer.Wrap(TakeBytes(ptr, len));
        }
        public static PackedBuffer GitCommitShortStat(string root, string sha)
        {
            IntPtr ptr = MsGitCommitShortStat(root, sha, out int len);
            return PackedBuffer.Wrap(TakeBytes(ptr, len));
        }
        /// <summary>Parsed unified diff for one file in a commit, as a packed buffer.</summary>
        public static PackedBuffer GitFileDiff(string root, string sha, string path, int wsMode)
        {
            IntPtr ptr = MsGitFileDiff(root, sha, path, wsMode, out int len);
            return PackedBuffer.Wrap(TakeBytes(ptr, len));
        }
        public static string GitFileAtCommit(string root, string sha, string path) => TakeString(MsGitFileAtCommit(root, sha, path));
        /// <summary>Files changed between two commits, as a packed buffer.</summary>
        public static PackedBuffer GitRangeFiles(string root, string a, string b)
        {
            IntPtr ptr = MsGitRangeFiles(root, a, b, out int len);
            return PackedBuffer.Wrap(TakeBytes(ptr, len));
        }
        public static PackedBuffer GitRangeShortStat(string root, string a, string b)
        {
            IntPtr ptr = MsGitRangeShortStat(root, a, b, out int len);
            return PackedBuffer.Wrap(TakeBytes(ptr, len));
        }
        /// <summary>Parsed unified diff for one file between two commits, as a packed buffer.</summary>
        public static PackedBuffer GitRangeFileDiff(string root, string a, string b, string path, int wsMode)
        {
            IntPtr ptr = MsGitRangeFileDiff(root, a, b, path, wsMode, out int len);
            return PackedBuffer.Wrap(TakeBytes(ptr, len));
        }
        /// <summary>Working-tree status, one record per (file, section), as a packed buffer.</summary>
        public static PackedBuffer GitStatus(string root)
        {
            IntPtr ptr = MsGitStatus(root, out int len);
            return PackedBuffer.Wrap(TakeBytes(ptr, len));
        }
        /// <summary>Parsed unified diff for one working-tree file, as a packed buffer.
        /// <paramref name="area"/> follows the ABI numbering (0 = unstaged), NOT the enum order --
        /// always go through GitRepository.AreaFlag.</summary>
        public static PackedBuffer GitWorkTreeFileDiff(string root, string path, int area, int wsMode)
        {
            IntPtr ptr = MsGitWorkTreeFileDiff(root, path, area, wsMode, out int len);
            return PackedBuffer.Wrap(TakeBytes(ptr, len));
        }
        public static string GitStagePaths(string root, string paths) => TakeString(MsGitStagePaths(root, paths));
        public static string GitStageAll(string root) => TakeString(MsGitStageAll(root));
        public static string GitUnstagePaths(string root, string paths) => TakeString(MsGitUnstagePaths(root, paths));
        public static string GitDiscardPaths(string root, string paths) => TakeString(MsGitDiscardPaths(root, paths));
        public static string GitCommit(string root, string message, bool amend) => TakeString(MsGitCommit(root, message, amend));
        public static string GitHeadMessage(string root) => TakeString(MsGitHeadMessage(root));
        public static string GitCheckout(string root, string refName, bool detach) => TakeString(MsGitCheckout(root, refName, detach));
        public static string GitCreateBranch(string root, string name, string startPoint, bool checkout) => TakeString(MsGitCreateBranch(root, name, startPoint, checkout));
        public static string GitDeleteBranch(string root, string name, bool force) => TakeString(MsGitDeleteBranch(root, name, force));
        public static string GitRenameBranch(string root, string oldName, string newName) => TakeString(MsGitRenameBranch(root, oldName, newName));
        public static string GitCreateTag(string root, string name, string commitish, string message) => TakeString(MsGitCreateTag(root, name, commitish, message));
        public static string GitDeleteTag(string root, string name) => TakeString(MsGitDeleteTag(root, name));
        public static string GitAheadBehind(string root, string a, string b) => TakeString(MsGitAheadBehind(root, a, b));
        public static string GitRemotes(string root) => TakeString(MsGitRemotes(root));
        public static string GitSetRemoteUrl(string root, string name, string url, bool pushUrl)
            => TakeString(MsGitSetRemoteUrl(root, name, url, pushUrl));

        // ---- Stash, blame, search, reflog (Phase 8) ------------------------------------------

        /// <summary>The stash, newest first, as a packed buffer.</summary>
        public static PackedBuffer GitStashList(string root)
        {
            IntPtr ptr = MsGitStashList(root, out int len);
            return PackedBuffer.Wrap(TakeBytes(ptr, len));
        }
        public static string GitStashSave(string root, string message, bool includeUntracked, bool keepIndex)
            => TakeString(MsGitStashSave(root, message, includeUntracked, keepIndex));
        public static string GitStashApply(string root, string refName) => TakeString(MsGitStashApply(root, refName));
        public static string GitStashPop(string root, string refName) => TakeString(MsGitStashPop(root, refName));
        public static string GitStashDrop(string root, string refName) => TakeString(MsGitStashDrop(root, refName));
        /// <summary>Per-line authorship as a packed buffer; failure travels in its header.</summary>
        public static PackedBuffer GitBlame(string root, string rev, string path, bool ignoreWhitespace,
                                            string detectMoves)
        {
            IntPtr ptr = MsGitBlame(root, rev, path, ignoreWhitespace, detectMoves, out int len);
            return PackedBuffer.Wrap(TakeBytes(ptr, len));
        }

        /// <summary>Search results as a packed buffer, byte-identical in shape to GitLog.</summary>
        public static PackedBuffer GitSearchLog(string root, string mode, string query, string pathFilter,
                                                int order, int maxCount, bool matchCase, bool useRegex,
                                                bool allBranches)
        {
            IntPtr ptr = MsGitSearchLog(root, mode, query, pathFilter, order, maxCount, matchCase,
                                        useRegex, allBranches, out int len);
            return PackedBuffer.Wrap(TakeBytes(ptr, len));
        }

        /// <summary>Where a ref has been, as a packed buffer.</summary>
        public static PackedBuffer GitReflog(string root, string refName, int maxCount)
        {
            IntPtr ptr = MsGitReflog(root, refName, maxCount, out int len);
            return PackedBuffer.Wrap(TakeBytes(ptr, len));
        }

        /// <summary>
        /// Wraps a managed progress handler as a native callback for the duration of one call.
        /// <paramref name="onOutput"/> receives each chunk of git's output (empty string for the
        /// heartbeat) and returns false to cancel. Null means "no progress" — the native side
        /// then just buffers, as the local commands do.
        /// </summary>
        private static string RunWithProgress(Func<string, bool>? onOutput,
                                              Func<ProgressFn?, IntPtr> call)
        {
            if (onOutput == null)
                return TakeString(call(null));

            // The native side calls back on its own threads for as long as the P/Invoke below is
            // on the stack. The marshalled function pointer is NOT a GC root, so the delegate is
            // held in a local and kept alive explicitly past the call.
            ProgressFn callback = (_, bytes, length) =>
            {
                string chunk = length > 0 && bytes != IntPtr.Zero
                    ? Marshal.PtrToStringUTF8(bytes, length)
                    : string.Empty; // length 0 => heartbeat
                return onOutput(chunk) ? 1 : 0;
            };
            try { return TakeString(call(callback)); }
            finally { GC.KeepAlive(callback); }
        }

        public static string GitFetch(string root, string remote, bool allRemotes, bool prune,
                                      bool tags, Func<string, bool>? onOutput)
            => RunWithProgress(onOutput, cb => MsGitFetch(root, remote, allRemotes, prune, tags, cb, IntPtr.Zero));

        public static string GitPull(string root, string remote, string branch,
                                     Func<string, bool>? onOutput)
            => RunWithProgress(onOutput, cb => MsGitPull(root, remote, branch, cb, IntPtr.Zero));

        public static string GitPush(string root, string remote, string branch, bool setUpstream,
                                     bool pushTags, Func<string, bool>? onOutput)
            => RunWithProgress(onOutput, cb => MsGitPush(root, remote, branch, setUpstream, pushTags, cb, IntPtr.Zero));

        // ---- Merge / rebase / cherry-pick / revert (Phase 7) --------------------------------

        public static string GitMerge(string root, string refName, bool noFastForward, bool noCommit,
                                      Func<string, bool>? onOutput)
            => RunWithProgress(onOutput, cb => MsGitMerge(root, refName, noFastForward, noCommit, cb, IntPtr.Zero));

        public static string GitRebase(string root, string upstream, Func<string, bool>? onOutput)
            => RunWithProgress(onOutput, cb => MsGitRebase(root, upstream, cb, IntPtr.Zero));

        /// <summary><paramref name="shas"/> is 0x1E-separated and applied in the order given.</summary>
        public static string GitCherryPick(string root, string shas, bool noCommit,
                                           Func<string, bool>? onOutput)
            => RunWithProgress(onOutput, cb => MsGitCherryPick(root, shas, noCommit, cb, IntPtr.Zero));

        public static string GitRevert(string root, string sha, int mainline, bool noCommit,
                                       Func<string, bool>? onOutput)
            => RunWithProgress(onOutput, cb => MsGitRevert(root, sha, mainline, noCommit, cb, IntPtr.Zero));

        public static string GitSequencerAction(string root, string operation, string action,
                                                Func<string, bool>? onOutput)
            => RunWithProgress(onOutput, cb => MsGitSequencerAction(root, operation, action, cb, IntPtr.Zero));

        public static string GitMergeTool(string root, string path, string tool,
                                          Func<string, bool>? onOutput)
            => RunWithProgress(onOutput, cb => MsGitMergeTool(root, path, tool, cb, IntPtr.Zero));

        public static string GitRepositoryState(string root) => TakeString(MsGitRepositoryState(root));

        /// <summary>Raw bytes of a file at a commit/ref (binary-safe; uses an explicit length, not strlen).</summary>
        public static byte[] GitFileBytesAtCommit(string root, string sha, string path)
        {
            IntPtr ptr = MsGitFileBytesAtCommit(root, sha, path, out int len);
            return TakeBytes(ptr, len);
        }
    }
}
