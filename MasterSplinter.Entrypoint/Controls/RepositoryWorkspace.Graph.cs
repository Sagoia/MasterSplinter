using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MasterSplinter.Entrypoint.Interop;
using MasterSplinter.Entrypoint.ViewModels;

namespace MasterSplinter.Entrypoint.Controls
{
    // The commit graph's host side: bind the SwapChainPanel to the native Direct2D renderer, keep
    // it in step with the history list's scroll offset, and tear it down again.
    //
    // Everything below is plumbing. The drawing is in Render/Windows/D2DGraphRenderer.cpp and the
    // layout in Graph/GraphLayout.cpp; this file only says WHERE and WHEN.
    public sealed partial class RepositoryWorkspace : UserControl
    {
        private GraphRenderer? _graph;
        private ScrollViewer? _commitScroller;

        /// <summary>
        /// Row height, pinned to CommitItemStyle in the XAML. The graph's offset-to-row arithmetic
        /// is exact only because every row is the same height; if that style changes, this must.
        /// </summary>
        private const float GraphRowHeight = 26f;

        private void GraphSurface_Loaded(object sender, RoutedEventArgs e)
        {
            AttachGraphRenderer();
            AttachCommitScroller();
            PushGraphTheme();
            PushGraphModel();
            PushGraphSelection();
            PushGraphViewport();
        }

        private void GraphSurface_Unloaded(object sender, RoutedEventArgs e)
        {
            Vm.PropertyChanged -= Graph_ViewModelChanged;
            if (_commitScroller != null)
            {
                _commitScroller.ViewChanged -= CommitScroller_ViewChanged;
                _commitScroller = null;
            }
            _graph?.Dispose();
            _graph = null;
        }

        private void AttachGraphRenderer()
        {
            if (_graph != null)
                return;

            // The native side wants the panel's IUnknown so it can QI ISwapChainPanelNative and do
            // every piece of COM and D3D work itself. FromManaged hands back a +1 reference; the
            // renderer takes its own, so this one is released either way.
            IntPtr unknown = IntPtr.Zero;
            try
            {
                unknown = WinRT.MarshalInspectable<SwapChainPanel>.FromManaged(GraphSurface);
                _graph = GraphRenderer.Attach(unknown);
            }
            catch (Exception)
            {
                // A machine with no usable graphics device shows no graph. It must not stop the
                // repository from opening.
                _graph = null;
            }
            finally
            {
                if (unknown != IntPtr.Zero)
                    Marshal.Release(unknown);
            }

            if (_graph != null)
                Vm.PropertyChanged += Graph_ViewModelChanged;
        }

        /// <summary>
        /// The history ListView has no ScrollViewer of its own in the XAML -- it is inside the
        /// control template -- so it has to be found once the template is applied.
        /// </summary>
        private void AttachCommitScroller()
        {
            if (_commitScroller != null)
                return;

            _commitScroller = FindDescendant<ScrollViewer>(CommitsList);
            if (_commitScroller != null)
                _commitScroller.ViewChanged += CommitScroller_ViewChanged;
        }

        private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                if (child is T match)
                    return match;
                T? deeper = FindDescendant<T>(child);
                if (deeper != null)
                    return deeper;
            }
            return null;
        }

        private void Graph_ViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.GraphDisplayList))
            {
                PushGraphModel();
                PushGraphViewport();
            }
        }

        private void CommitScroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
            => PushGraphViewport();

        private void GraphSurface_SizeChanged(object sender, SizeChangedEventArgs e)
            => PushGraphViewport();

        private void GraphSurface_CompositionScaleChanged(SwapChainPanel sender, object args)
            => PushGraphViewport();

        private void GraphSurface_ActualThemeChanged(FrameworkElement sender, object args)
        {
            // Let XAML finish updating ThemeResource values before reading the surface's brush.
            DispatcherQueue.TryEnqueue(() =>
            {
                PushGraphTheme();
                _graph?.Render();
            });
        }

        private void PushGraphModel() => _graph?.SetModel(Vm.GraphDisplayList);

        /// <summary>
        /// The colours the surface paints rows with, read from the same resources the ListView
        /// uses so the two cannot drift. A missing resource falls back to the values the XAML
        /// declares literally.
        /// </summary>
        private void PushGraphTheme()
        {
            CommitsList.Resources.TryGetValue("ListViewItemBackgroundSelected", out object? selected);
            // This ThemeResource belongs to the surface, so it follows its effective theme even
            // when the window overrides the application's requested theme.
            _graph?.SetTheme(BrushArgb(GraphSurface.Background, 0xFF1E1E1E),
                             BrushArgb(selected, 0xFF2563EB));
        }

        private static uint BrushArgb(object? value, uint fallback)
        {
            if (value is SolidColorBrush brush)
            {
                Windows.UI.Color c = brush.Color;
                return ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
            }
            return fallback;
        }

        /// <summary>Row indices of the current selection, in the list's own coordinates.</summary>
        private void PushGraphSelection()
        {
            if (_graph == null)
                return;

            var rows = new List<int>(CommitsList.SelectedItems.Count);
            foreach (object item in CommitsList.SelectedItems)
            {
                int index = Vm.Commits.IndexOf((Models.CommitRow)item);
                if (index >= 0)
                    rows.Add(index);
            }
            _graph.SetSelection(rows.ToArray());
            _graph.Render();
        }

        private void PushGraphViewport()
        {
            if (_graph == null)
                return;

            // The list scrolls; the panel does not. Feeding the offset in is what lets one surface
            // stand in for a Canvas on every row.
            double offset = _commitScroller?.VerticalOffset ?? 0.0;
            _graph.SetViewport((float)GraphSurface.ActualWidth, (float)GraphSurface.ActualHeight,
                               offset, GraphRowHeight, GraphSurface.CompositionScaleX);
            _graph.Render();
        }
    }
}
