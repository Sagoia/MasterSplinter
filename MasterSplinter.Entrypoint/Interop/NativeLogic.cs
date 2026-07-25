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
        private static extern IntPtr MsGitLog([MarshalAs(UnmanagedType.LPUTF8Str)] string root, int order, int maxCount);

        [DllImport(Dll, EntryPoint = "MsGitRefDetails", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr MsGitRefDetails([MarshalAs(UnmanagedType.LPUTF8Str)] string root);

        [DllImport(Dll, EntryPoint = "MsGitCommitFiles", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr MsGitCommitFiles([MarshalAs(UnmanagedType.LPUTF8Str)] string root,
                                                      [MarshalAs(UnmanagedType.LPUTF8Str)] string sha);

        [DllImport(Dll, EntryPoint = "MsGitCommitShortStat", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr MsGitCommitShortStat([MarshalAs(UnmanagedType.LPUTF8Str)] string root,
                                                          [MarshalAs(UnmanagedType.LPUTF8Str)] string sha);

        [DllImport(Dll, EntryPoint = "MsGitFileDiff", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr MsGitFileDiff([MarshalAs(UnmanagedType.LPUTF8Str)] string root,
                                                   [MarshalAs(UnmanagedType.LPUTF8Str)] string sha,
                                                   [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
                                                   int wsMode);

        [DllImport(Dll, EntryPoint = "MsGitFileAtCommit", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr MsGitFileAtCommit([MarshalAs(UnmanagedType.LPUTF8Str)] string root,
                                                       [MarshalAs(UnmanagedType.LPUTF8Str)] string sha,
                                                       [MarshalAs(UnmanagedType.LPUTF8Str)] string path);

        [DllImport(Dll, EntryPoint = "MsGitRangeFiles", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr MsGitRangeFiles([MarshalAs(UnmanagedType.LPUTF8Str)] string root,
                                                     [MarshalAs(UnmanagedType.LPUTF8Str)] string a,
                                                     [MarshalAs(UnmanagedType.LPUTF8Str)] string b);

        [DllImport(Dll, EntryPoint = "MsGitRangeShortStat", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr MsGitRangeShortStat([MarshalAs(UnmanagedType.LPUTF8Str)] string root,
                                                         [MarshalAs(UnmanagedType.LPUTF8Str)] string a,
                                                         [MarshalAs(UnmanagedType.LPUTF8Str)] string b);

        [DllImport(Dll, EntryPoint = "MsGitRangeFileDiff", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr MsGitRangeFileDiff([MarshalAs(UnmanagedType.LPUTF8Str)] string root,
                                                        [MarshalAs(UnmanagedType.LPUTF8Str)] string a,
                                                        [MarshalAs(UnmanagedType.LPUTF8Str)] string b,
                                                        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
                                                        int wsMode);

        [DllImport(Dll, EntryPoint = "MsGitStatus", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr MsGitStatus([MarshalAs(UnmanagedType.LPUTF8Str)] string root);

        [DllImport(Dll, EntryPoint = "MsGitWorkTreeFileDiff", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr MsGitWorkTreeFileDiff([MarshalAs(UnmanagedType.LPUTF8Str)] string root,
                                                           [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
                                                           int area,
                                                           int wsMode);

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

        public static string GitOpenRepository(string path) => TakeString(MsGitOpenRepository(path));
        public static string GitLog(string root, int order, int maxCount) => TakeString(MsGitLog(root, order, maxCount));
        public static string GitRefDetails(string root) => TakeString(MsGitRefDetails(root));
        public static string GitCommitFiles(string root, string sha) => TakeString(MsGitCommitFiles(root, sha));
        public static string GitCommitShortStat(string root, string sha) => TakeString(MsGitCommitShortStat(root, sha));
        public static string GitFileDiff(string root, string sha, string path, int wsMode) => TakeString(MsGitFileDiff(root, sha, path, wsMode));
        public static string GitFileAtCommit(string root, string sha, string path) => TakeString(MsGitFileAtCommit(root, sha, path));
        public static string GitRangeFiles(string root, string a, string b) => TakeString(MsGitRangeFiles(root, a, b));
        public static string GitRangeShortStat(string root, string a, string b) => TakeString(MsGitRangeShortStat(root, a, b));
        public static string GitRangeFileDiff(string root, string a, string b, string path, int wsMode) => TakeString(MsGitRangeFileDiff(root, a, b, path, wsMode));
        public static string GitStatus(string root) => TakeString(MsGitStatus(root));
        public static string GitWorkTreeFileDiff(string root, string path, int area, int wsMode) => TakeString(MsGitWorkTreeFileDiff(root, path, area, wsMode));
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

        /// <summary>Raw bytes of a file at a commit/ref (binary-safe; uses an explicit length, not strlen).</summary>
        public static byte[] GitFileBytesAtCommit(string root, string sha, string path)
        {
            IntPtr ptr = MsGitFileBytesAtCommit(root, sha, path, out int len);
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
    }
}
