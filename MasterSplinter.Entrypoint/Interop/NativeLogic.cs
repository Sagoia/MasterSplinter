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

        [DllImport(Dll, EntryPoint = "MsGitFileDiff", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr MsGitFileDiff([MarshalAs(UnmanagedType.LPUTF8Str)] string root,
                                                   [MarshalAs(UnmanagedType.LPUTF8Str)] string sha,
                                                   [MarshalAs(UnmanagedType.LPUTF8Str)] string path);

        [DllImport(Dll, EntryPoint = "MsGitFileAtCommit", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr MsGitFileAtCommit([MarshalAs(UnmanagedType.LPUTF8Str)] string root,
                                                       [MarshalAs(UnmanagedType.LPUTF8Str)] string sha,
                                                       [MarshalAs(UnmanagedType.LPUTF8Str)] string path);

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
        public static string GitFileDiff(string root, string sha, string path) => TakeString(MsGitFileDiff(root, sha, path));
        public static string GitFileAtCommit(string root, string sha, string path) => TakeString(MsGitFileAtCommit(root, sha, path));
    }
}
