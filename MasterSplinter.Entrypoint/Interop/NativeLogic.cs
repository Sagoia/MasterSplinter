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

        [DllImport(Dll, EntryPoint = "MsGitRefs", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr MsGitRefs([MarshalAs(UnmanagedType.LPUTF8Str)] string root);

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

        [DllImport(Dll, EntryPoint = "MsGitFileBytesAtCommit", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr MsGitFileBytesAtCommit([MarshalAs(UnmanagedType.LPUTF8Str)] string root,
                                                            [MarshalAs(UnmanagedType.LPUTF8Str)] string sha,
                                                            [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
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

        public static string GitOpenRepository(string path) => TakeString(MsGitOpenRepository(path));
        public static string GitLog(string root, int order, int maxCount) => TakeString(MsGitLog(root, order, maxCount));
        public static string GitRefs(string root) => TakeString(MsGitRefs(root));
        public static string GitCommitFiles(string root, string sha) => TakeString(MsGitCommitFiles(root, sha));
        public static string GitCommitShortStat(string root, string sha) => TakeString(MsGitCommitShortStat(root, sha));
        public static string GitFileDiff(string root, string sha, string path, int wsMode) => TakeString(MsGitFileDiff(root, sha, path, wsMode));
        public static string GitFileAtCommit(string root, string sha, string path) => TakeString(MsGitFileAtCommit(root, sha, path));
        public static string GitRangeFiles(string root, string a, string b) => TakeString(MsGitRangeFiles(root, a, b));
        public static string GitRangeShortStat(string root, string a, string b) => TakeString(MsGitRangeShortStat(root, a, b));
        public static string GitRangeFileDiff(string root, string a, string b, string path, int wsMode) => TakeString(MsGitRangeFileDiff(root, a, b, path, wsMode));

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
