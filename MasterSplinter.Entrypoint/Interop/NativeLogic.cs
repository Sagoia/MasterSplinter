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
    }
}
