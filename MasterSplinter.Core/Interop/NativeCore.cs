namespace MasterSplinter.Entrypoint.Interop
{
    /// <summary>
    /// The native core's lifecycle, as the app sees it.
    /// <para>
    /// <see cref="NativeLogic"/> — the raw P/Invoke surface — stays <c>internal</c> so the 52
    /// <c>MsGit*</c> entry points cannot be called from the UI layer by accident; everything
    /// repository-shaped goes through <c>GitRepository</c>. This is the small, deliberate exception:
    /// the four calls the app genuinely owns.
    /// </para>
    /// </summary>
    public static class NativeCore
    {
        /// <summary>Call once after the library loads, before any other call. Done here rather than
        /// in DllMain, which runs under the loader lock and is Windows-only.</summary>
        public static bool Initialize() => NativeLogic.Initialize();

        /// <summary>Call once at exit.</summary>
        public static void Shutdown() => NativeLogic.Shutdown();

        /// <summary>The core's version string, shown in the title bar.</summary>
        public static string Version() => NativeLogic.Version();

        /// <summary>Trivial round-trip so the interop boundary is verifiable from the app.</summary>
        public static int Add(int a, int b) => NativeLogic.Add(a, b);
    }
}
