using System;

namespace MasterSplinter.Entrypoint.Interop
{
    /// <summary>
    /// The native commit-graph renderer, as the app sees it.
    /// <para>
    /// This is the second deliberate exception to <see cref="NativeLogic"/> being internal (the
    /// first is <see cref="NativeCore"/>). It has to be public because the thing it binds to — a
    /// <c>SwapChainPanel</c> — only exists in the WinUI layer, and every alternative would mean
    /// passing a XAML type down into a project that has no WinUI reference.
    /// </para>
    /// <para>
    /// The handle owns a GPU device and a swap chain, so it is disposable and single-threaded:
    /// create it, feed it, draw it, and destroy it on the UI thread. Every call tolerates a failed
    /// create, so a machine with no usable device shows a blank graph column rather than crashing.
    /// </para>
    /// </summary>
    public sealed class GraphRenderer : IDisposable
    {
        private IntPtr _handle;

        private GraphRenderer(IntPtr handle) => _handle = handle;

        /// <summary>
        /// Attaches a renderer to a SwapChainPanel, given its <c>IUnknown*</c>. Returns null when
        /// no device could be created — the caller should then simply not draw.
        /// </summary>
        public static GraphRenderer? Attach(IntPtr panelUnknown)
        {
            if (panelUnknown == IntPtr.Zero)
                return null;
            IntPtr handle = NativeLogic.GraphCreate(panelUnknown);
            return handle == IntPtr.Zero ? null : new GraphRenderer(handle);
        }

        /// <summary>The display list from the loaded log; the native side copies it.</summary>
        public void SetModel(byte[]? displayList)
        {
            if (_handle != IntPtr.Zero)
                NativeLogic.GraphSetModel(_handle, displayList);
        }

        /// <summary>
        /// Where the graph is drawn and how far the history list has scrolled, in
        /// device-independent pixels. Row height is fixed by the list, which is what makes the
        /// offset-to-row arithmetic exact.
        /// </summary>
        public void SetViewport(float widthDip, float heightDip, double scrollPx,
                                float rowHeightPx, float scale)
        {
            if (_handle != IntPtr.Zero)
                NativeLogic.GraphSetViewport(_handle, widthDip, heightDip, scrollPx, rowHeightPx, scale);
        }

        /// <summary>
        /// The list colours to paint rows with, as 0xAARRGGBB.
        /// <para>
        /// The renderer paints them because a WinUI 3 SwapChainPanel does not blend with the XAML
        /// behind it -- a transparent surface shows black, not the rows. Measured, not assumed.
        /// </para>
        /// </summary>
        public void SetTheme(uint backgroundArgb, uint selectedArgb)
        {
            if (_handle != IntPtr.Zero)
                NativeLogic.GraphSetTheme(_handle, backgroundArgb, selectedArgb);
        }

        /// <summary>Which rows are selected, by index; the list allows extended selection.</summary>
        public void SetSelection(int[]? rows)
        {
            if (_handle != IntPtr.Zero)
                NativeLogic.GraphSetSelection(_handle, rows);
        }

        public void Render()
        {
            if (_handle != IntPtr.Zero)
                NativeLogic.GraphRender(_handle);
        }

        public void Dispose()
        {
            if (_handle == IntPtr.Zero)
                return;
            IntPtr handle = _handle;
            _handle = IntPtr.Zero;
            NativeLogic.GraphDestroy(handle);
        }
    }
}
