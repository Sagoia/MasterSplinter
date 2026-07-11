using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using MasterSplinter.Entrypoint.Git;
using MasterSplinter.Entrypoint.Infrastructure;
using MasterSplinter.Entrypoint.Models;
using MasterSplinter.Entrypoint.ViewModels;

namespace MasterSplinter.Entrypoint.Controls
{
    public sealed partial class RepositoryWorkspace : UserControl
    {
        public MainViewModel Vm { get; } = new();

        /// <summary>Raised by the home screen's "Open Repository…" button (the window owns the picker).</summary>
        public event EventHandler? OpenRepositoryRequested;

        /// <summary>Raised when a recent repository is chosen on the home screen (carries its path).</summary>
        public event EventHandler<string>? OpenRecentRequested;

        // True while this view is pushing the VM's mode into the SelectorBar, so the
        // SelectionChanged handler doesn't bounce the change back into the VM.
        private bool _syncingModeBar;

        public RepositoryWorkspace()
        {
            InitializeComponent();
            DataContext = Vm;

            // The grouped working-copy view: a CollectionViewSource resource can't bind to the
            // DataContext, so its Source is wired here.
            StatusGroupsView.Source = Vm.StatusGroups;

            // Keep the bottom mode tabs in sync when the mode changes from elsewhere
            // (sidebar "Working Copy" tap, selecting a commit, refresh).
            Vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.IsWorkingCopyMode))
                    SyncModeBar();
            };
        }

        private void SyncModeBar()
        {
            _syncingModeBar = true;
            try { ModeBar.SelectedItem = Vm.IsWorkingCopyMode ? ModeFileStatus : ModeLogHistory; }
            finally { _syncingModeBar = false; }
        }

        private async void ModeBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
        {
            if (_syncingModeBar) return;
            if (sender.SelectedItem == ModeFileStatus)
            {
                await Vm.EnterWorkingCopyAsync();
            }
            else if (sender.SelectedItem == ModeLogHistory)
            {
                Vm.ExitWorkingCopy();
            }
            else
            {
                // "Search" is not a view yet — snap the selection back to the current mode.
                SyncModeBar();
            }
        }

        private async void Refresh_Click(object sender, RoutedEventArgs e) => await Vm.RefreshAsync();

        private async void RefreshAccelerator_Invoked(KeyboardAccelerator sender,
            KeyboardAcceleratorInvokedEventArgs args)
        {
            args.Handled = true;
            await Vm.RefreshAsync(); // no-ops while a load is already running
        }

        private void Commits_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count > 0 && e.AddedItems[0] is CommitRow row)
                Vm.SelectedCommit = row;
        }

        private void Files_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count > 0 && e.AddedItems[0] is ChangedFile file)
                Vm.SelectedFile = file;
        }

        private void SidebarHost_Tapped(object sender, TappedRoutedEventArgs e)
        {
            DependencyObject? node = e.OriginalSource as DependencyObject;
            while (node != null)
            {
                if (node is FrameworkElement fe && fe.DataContext is SidebarItemVM item)
                {
                    Vm.HandleSidebarTap(item);
                    return;
                }
                node = VisualTreeHelper.GetParent(node);
            }
        }

        private void ThemeBtn_Click(object sender, RoutedEventArgs e)
        {
            if (XamlRoot?.Content is FrameworkElement root)
            {
                root.RequestedTheme = root.RequestedTheme == ElementTheme.Dark
                    ? ElementTheme.Light
                    : ElementTheme.Dark;
            }
        }

        // ---- Compare commits / refs (DIFF-006 / DIFF-007) -------------------------------------

        private void MarkForComparison_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is CommitRow c)
                Vm.MarkForComparison(c);
        }

        private async void CompareWithMarked_Click(object sender, RoutedEventArgs e)
        {
            if (!Vm.HasMarkedCommit)
            {
                await ShowMessageAsync("Compare commits", "Mark a commit first (right-click ▸ Mark for Comparison), then compare another with it.");
                return;
            }
            if (sender is FrameworkElement fe && fe.DataContext is CommitRow c)
                await Vm.CompareWithMarkedAsync(c);
        }

        private void ExitCompare_Click(object sender, RoutedEventArgs e) => Vm.ExitCompare();

        // Opening Button.Flyout via the built-in trigger proved unreliable for this styled icon
        // button, so the flyout is attached and shown explicitly here (DIFF-003/004 settings).
        private void DiffSettings_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe)
                Microsoft.UI.Xaml.Controls.Primitives.FlyoutBase.ShowAttachedFlyout(fe);
        }

        private async void CompareRefs_Click(object sender, RoutedEventArgs e)
        {
            var names = Vm.CompareRefNames;
            if (names.Count == 0)
            {
                await ShowMessageAsync("Compare refs", "Open a repository first.");
                return;
            }

            var sourceBox = new ComboBox
            {
                Header = "Source (A)", ItemsSource = names, SelectedIndex = 0,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            var targetBox = new ComboBox
            {
                Header = "Target (B)", ItemsSource = names, SelectedIndex = names.Count > 1 ? 1 : 0,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            var panel = new StackPanel { Spacing = 12, MinWidth = 320 };
            panel.Children.Add(sourceBox);
            panel.Children.Add(targetBox);

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Compare refs",
                Content = panel,
                PrimaryButtonText = "Compare",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary
                && sourceBox.SelectedItem is string a && targetBox.SelectedItem is string b)
            {
                await Vm.CompareRefsAsync(a, b);
            }
        }

        private async Task ShowMessageAsync(string title, string message)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = title,
                Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                CloseButtonText = "OK",
                DefaultButton = ContentDialogButton.Close,
            };
            await dialog.ShowAsync();
        }

        // ---- Home screen (CORE-002) -----------------------------------------------------------

        private void OpenRepo_Click(object sender, RoutedEventArgs e)
            => OpenRepositoryRequested?.Invoke(this, EventArgs.Empty);

        private void RecentItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is string path)
                OpenRecentRequested?.Invoke(this, path);
        }

        private void ErrorBar_CloseClick(InfoBar sender, object args) => Vm.DismissError();

        // ---- Copy commit hash (LOG-006) -------------------------------------------------------

        private void CopyShortHash_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is CommitRow c)
                CopyToClipboard(c.Hash);
        }

        private void CopyFullHash_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is CommitRow c)
                CopyToClipboard(c.FullHash);
        }

        private static void CopyToClipboard(string text)
        {
            var data = new DataPackage();
            data.SetText(text ?? string.Empty);
            Clipboard.SetContent(data);
        }

        // ---- Working-copy file actions (STATUS-006/007) ----------------------------------------

        /// <summary>Absolute on-disk path for a working-tree status entry, or null if unavailable.</summary>
        private string? AbsolutePathOf(ChangedFile file)
        {
            string? root = Vm.Repository?.RootPath;
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(file.Path))
                return null;
            // GetFullPath normalizes the forward slashes git prints (rev-parse --show-toplevel);
            // explorer.exe /select silently ignores paths containing them.
            return Path.GetFullPath(Path.Combine(root, file.Path.Replace('/', Path.DirectorySeparatorChar)));
        }

        private async void OpenInEditor_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.DataContext is not ChangedFile file)
                return;
            await OpenWorkingFileAsync(file);
        }

        private async void WorkingFile_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ChangedFile file)
                await OpenWorkingFileAsync(file);
        }

        private async Task OpenWorkingFileAsync(ChangedFile file)
        {
            string? abs = AbsolutePathOf(file);
            if (abs == null) return;
            if (!File.Exists(abs))
            {
                await ShowMessageAsync("Open in External Editor", $"The file no longer exists on disk:\n{abs}");
                return;
            }
            string? error = EditorLauncher.OpenInEditor(SettingsStore.Load().EditorCommand, abs);
            if (error != null)
                await ShowMessageAsync("Open in External Editor",
                    $"Could not launch the editor: {error}\n\nCheck the editor command under Tools ▸ Options…");
        }

        private async void RevealInExplorer_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.DataContext is not ChangedFile file)
                return;
            string? abs = AbsolutePathOf(file);
            if (abs == null) return;
            if (!File.Exists(abs))
            {
                await ShowMessageAsync("Reveal in Explorer", $"The file no longer exists on disk:\n{abs}");
                return;
            }
            string? error = EditorLauncher.RevealInExplorer(abs);
            if (error != null)
                await ShowMessageAsync("Reveal in Explorer", error);
        }

        private void CopyFilePath_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ChangedFile file)
                CopyToClipboard(AbsolutePathOf(file) ?? file.Path);
        }

        // ---- Open file at commit (LOG-007) ----------------------------------------------------

        private async void OpenFileAtCommit_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ChangedFile file)
                await ShowFileAtCommitAsync(file);
        }

        private async void File_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ChangedFile file)
                await ShowFileAtCommitAsync(file);
        }

        private async Task ShowFileAtCommitAsync(ChangedFile file)
        {
            CommitRow? commit = Vm.SelectedCommit;
            if (commit == null)
                return;

            string content = await Vm.ReadFileAtCommitAsync(commit.FullHash, file.Path);

            var text = new TextBlock
            {
                Text = string.IsNullOrEmpty(content) ? "(empty, binary, or missing file)" : content,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                IsTextSelectionEnabled = true,
                TextWrapping = TextWrapping.NoWrap,
            };
            var scroller = new ScrollViewer
            {
                Content = text,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = 540,
                MinWidth = 640,
                MaxWidth = 940,
            };
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = $"{file.Path}  @  {commit.Hash}",
                Content = scroller,
                CloseButtonText = "Close",
                DefaultButton = ContentDialogButton.Close,
            };
            await dialog.ShowAsync();
        }
    }
}
