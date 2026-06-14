using System;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using MasterSplinter.Entrypoint.Infrastructure;

namespace MasterSplinter.Entrypoint
{
    public sealed partial class MainWindow : Window
    {
        private int _newTabCounter;

        public MainWindow()
        {
            InitializeComponent();

            // ---- Modern WinUI 3 custom title bar -------------------------------------------
            ExtendsContentIntoTitleBar = true;   // hide the system title bar (must be set in code)
            SetTitleBar(TitleBarDrag);           // our draggable region

            if (AppWindowTitleBar.IsCustomizationSupported())
                AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;

            AppTitleBar.Loaded += (_, _) => UpdateTitleBarInsets();
            AppTitleBar.SizeChanged += (_, _) => UpdateTitleBarInsets();
            RootGrid.ActualThemeChanged += (_, _) => UpdateCaptionButtonColors();

            UpdateCaptionButtonColors();

            // ---- Confirm the native C++ core loaded (P/Invoke) ------------------------------
            try
            {
                CoreInfo.Text = $"· {Interop.NativeLogic.Version()}";
                System.Diagnostics.Debug.WriteLine($"[interop] MsLogicAdd(40,2) = {Interop.NativeLogic.Add(40, 2)}");
            }
            catch (Exception ex)
            {
                CoreInfo.Text = $"· C++ core unavailable: {ex.GetType().Name}";
            }
        }

        /// <summary>Reserve space for the system caption buttons so content never slides under them.</summary>
        private void UpdateTitleBarInsets()
        {
            if (!ExtendsContentIntoTitleBar || !AppWindowTitleBar.IsCustomizationSupported())
                return;

            double scale = AppTitleBar.XamlRoot?.RasterizationScale ?? 1.0;
            var bar = AppWindow.TitleBar;
            LeftPaddingColumn.Width = new GridLength(bar.LeftInset / scale);
            RightPaddingColumn.Width = new GridLength(bar.RightInset / scale);
        }

        /// <summary>Keep the min/max/close buttons transparent and tinted to match the active theme.</summary>
        private void UpdateCaptionButtonColors()
        {
            if (!AppWindowTitleBar.IsCustomizationSupported())
                return;

            var bar = AppWindow.TitleBar;
            bar.ButtonBackgroundColor = Colors.Transparent;
            bar.ButtonInactiveBackgroundColor = Colors.Transparent;

            bool dark = RootGrid.ActualTheme == ElementTheme.Dark;
            if (dark)
            {
                bar.ButtonForegroundColor = Color.FromArgb(0xFF, 0xE6, 0xE6, 0xE6);
                bar.ButtonHoverForegroundColor = Colors.White;
                bar.ButtonHoverBackgroundColor = Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF);
                bar.ButtonPressedForegroundColor = Colors.White;
                bar.ButtonPressedBackgroundColor = Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF);
                bar.ButtonInactiveForegroundColor = Color.FromArgb(0xFF, 0x9A, 0x9A, 0x9A);
            }
            else
            {
                bar.ButtonForegroundColor = Color.FromArgb(0xFF, 0x1B, 0x1B, 0x1B);
                bar.ButtonHoverForegroundColor = Colors.Black;
                bar.ButtonHoverBackgroundColor = Color.FromArgb(0x18, 0x00, 0x00, 0x00);
                bar.ButtonPressedForegroundColor = Colors.Black;
                bar.ButtonPressedBackgroundColor = Color.FromArgb(0x28, 0x00, 0x00, 0x00);
                bar.ButtonInactiveForegroundColor = Color.FromArgb(0xFF, 0x8A, 0x8A, 0x8A);
            }
        }

        // Keeps the scaled content filling the host: ScaledRoot is laid out at host/scale, then the
        // RenderTransform shrinks it back to exactly fill ZoomHost (so 0.85 = everything ~15% smaller).
        private void ZoomHost_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            double scale = UiScale.ScaleX;
            if (scale <= 0) return;
            ScaledRoot.Width = ZoomHost.ActualWidth / scale;
            ScaledRoot.Height = ZoomHost.ActualHeight / scale;
        }

        private void ToggleTheme_Click(object sender, RoutedEventArgs e)
        {
            RootGrid.RequestedTheme = RootGrid.RequestedTheme == ElementTheme.Dark
                ? ElementTheme.Light
                : ElementTheme.Dark;
        }

        private void Exit_Click(object sender, RoutedEventArgs e) => Close();

        private void NewTab_Click(object sender, RoutedEventArgs e) => AddRepositoryTab();

        private void RepoTabs_AddTabButtonClick(TabView sender, object args) => AddRepositoryTab();

        private void RepoTabs_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
        {
            sender.TabItems.Remove(args.Tab);
        }

        private void AddRepositoryTab()
        {
            // The TabView is a strip only; tabs carry no content (the shared workspace lives below it).
            var tab = new TabViewItem
            {
                Header = $"new-repo-{++_newTabCounter}",
                IconSource = new FontIconSource { Glyph = Glyphs.Of(Glyphs.Folder) },
            };

            RepoTabs.TabItems.Add(tab);
            RepoTabs.SelectedItem = tab;
        }
    }
}
