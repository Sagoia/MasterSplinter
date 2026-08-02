using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Text;
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

        // Phase 4: double-click toggles stage/unstage (SourceTree behavior); opening the file in
        // the external editor stays available on the context menu.
        private async void WorkingFile_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ChangedFile file)
                await Vm.ToggleStageAsync(file);
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

        // ---- Staging & commit (COMMIT-001..007) -----------------------------------------------

        private async void StageFile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ChangedFile file)
                await Vm.StageFileAsync(file);
        }

        private async void UnstageFile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ChangedFile file)
                await Vm.UnstageFileAsync(file);
        }

        private async void StageAll_Click(object sender, RoutedEventArgs e) => await Vm.StageAllAsync();

        private async void UnstageAll_Click(object sender, RoutedEventArgs e) => await Vm.UnstageAllAsync();

        // One shared context menu for every working-copy row; which of the stage/unstage/discard
        // items apply depends on the row's area, so visibility is set as the flyout opens.
        private void WorkingFileMenu_Opening(object sender, object e)
        {
            if (sender is not MenuFlyout menu || menu.Target is not FrameworkElement target
                || target.DataContext is not ChangedFile file)
                return;

            foreach (var item in menu.Items)
            {
                if (item is not MenuFlyoutItem mi || mi.Tag is not string tag)
                    continue;
                bool visible = tag switch
                {
                    "stage" => file.Area != WorkTreeArea.Staged,
                    "unstage" => file.Area == WorkTreeArea.Staged,
                    "discard" => file.Area != WorkTreeArea.Staged,
                    _ => true,
                };
                mi.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private async void DiscardFile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.DataContext is not ChangedFile file)
                return;

            // COMMIT-004: destructive, so always confirm — with distinct wording for untracked
            // files, where "discard" means deleting the file from disk.
            bool untracked = file.Area == WorkTreeArea.Untracked;
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = untracked ? "Delete untracked file" : "Discard changes",
                Content = new TextBlock
                {
                    Text = untracked
                        ? $"{file.Path} is not tracked by git. Discarding will permanently delete the file from disk."
                        : $"Discard changes to {file.Path}? This cannot be undone.",
                    TextWrapping = TextWrapping.Wrap,
                },
                PrimaryButtonText = untracked ? "Delete" : "Discard",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                await Vm.DiscardFileAsync(file);
        }

        private async void Commit_Click(object sender, RoutedEventArgs e)
        {
            // COMMIT-007: amending rewrites history, so confirm and name the commit being replaced.
            if (Vm.IsAmend)
            {
                var head = await Vm.GetHeadMessageAsync();
                string current = head?.Subject is { Length: > 0 } subject
                    ? $"\n\nCurrent commit: “{subject}”"
                    : "";
                var dialog = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = "Amend last commit",
                    Content = new TextBlock
                    {
                        Text = "Amend the last commit? This rewrites history — do not amend commits that are already pushed." + current,
                        TextWrapping = TextWrapping.Wrap,
                    },
                    PrimaryButtonText = "Amend",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Close,
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                    return;
            }
            await Vm.CommitAsync();
        }

        /// <summary>Toolbar Commit / Actions ▸ Commit…: open the working copy and focus the editor.</summary>
        public async Task FocusCommitEditorAsync()
        {
            await Vm.EnterWorkingCopyAsync();
            CommitSubjectBox.Focus(FocusState.Programmatic);
        }

        private async void CommitToolbar_Click(object sender, RoutedEventArgs e)
            => await FocusCommitEditorAsync();

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

        // ---- Branches & tags (Phase 5, BR-001..007 / TAG-001..003) -----------------------------

        /// <summary>Resolves the sidebar row a context-menu item or double-tap belongs to.</summary>
        private static SidebarItemVM? SidebarItemOf(object sender)
            => (sender as FrameworkElement)?.DataContext as SidebarItemVM
               ?? ((sender as MenuFlyout)?.Target as FrameworkElement)?.DataContext as SidebarItemVM;

        /// <summary>Show only the items that apply to this row's kind. Delete is hidden on the
        /// current branch — that is the one refusal `-D` cannot fix, and hiding it beats making
        /// the user discover it (BR-005).</summary>
        private void SidebarMenu_Opening(object sender, object e)
        {
            if (sender is not MenuFlyout menu)
                return;
            SidebarItemVM? item = SidebarItemOf(menu);
            if (item == null)
                return;

            bool isBranch = item.Kind == SidebarKind.Branch;
            bool isRemote = item.Kind == SidebarKind.RemoteBranch;
            bool isTag = item.Kind == SidebarKind.Tag;
            bool isRef = isBranch || isRemote || isTag;
            bool isRemoteNode = item.Kind == SidebarKind.Remote; // the "origin" parent, not a branch

            foreach (var entry in menu.Items)
            {
                if (entry is not MenuFlyoutItemBase mib || mib.Tag is not string tag)
                    continue;
                bool visible = tag switch
                {
                    "checkout" => isBranch && !item.IsCurrent,
                    "checkoutlocal" => isRemote,
                    "newfrom" => isRef,
                    "rename" => isBranch,
                    "sep" => isRef,
                    "deletebranch" => isBranch && !item.IsCurrent,
                    "deletetag" => isTag,
                    "editremote" => isRemoteNode, // REMOTE-008
                    "copyname" => isRef,
                    _ => true,
                };
                mib.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private async void SidebarLeaf_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            SidebarItemVM? item = SidebarItemOf(sender);
            if (item == null)
                return;
            if (item.Kind == SidebarKind.Branch && !item.IsCurrent)
                await SwitchToBranchAsync(item.ShortName);
            else if (item.Kind == SidebarKind.RemoteBranch)
                await ShowCreateBranchDialogAsync(item.ShortName, $"local branch from {item.ShortName}");
        }

        private async void SidebarCheckout_Click(object sender, RoutedEventArgs e)
        {
            SidebarItemVM? item = SidebarItemOf(sender);
            if (item != null)
                await SwitchToBranchAsync(item.ShortName);
        }

        private async void SidebarCheckoutLocal_Click(object sender, RoutedEventArgs e)
        {
            SidebarItemVM? item = SidebarItemOf(sender);
            if (item != null)
                await ShowCreateBranchDialogAsync(item.ShortName, $"local branch from {item.ShortName}");
        }

        private async void SidebarCreateBranch_Click(object sender, RoutedEventArgs e)
        {
            SidebarItemVM? item = SidebarItemOf(sender);
            if (item != null)
                await ShowCreateBranchDialogAsync(item.ShortName, item.ShortName);
        }

        private async void SidebarRenameBranch_Click(object sender, RoutedEventArgs e)
        {
            SidebarItemVM? item = SidebarItemOf(sender);
            if (item != null)
                await ShowRenameBranchDialogAsync(item.ShortName);
        }

        private async void SidebarDeleteBranch_Click(object sender, RoutedEventArgs e)
        {
            SidebarItemVM? item = SidebarItemOf(sender);
            if (item != null)
                await DeleteBranchWithConfirmAsync(item.ShortName);
        }

        private async void SidebarDeleteTag_Click(object sender, RoutedEventArgs e)
        {
            SidebarItemVM? item = SidebarItemOf(sender);
            if (item != null)
                await DeleteTagWithConfirmAsync(item.ShortName);
        }

        private void SidebarCopyRefName_Click(object sender, RoutedEventArgs e)
        {
            SidebarItemVM? item = SidebarItemOf(sender);
            if (item == null)
                return;
            var data = new DataPackage();
            data.SetText(item.ShortName);
            Clipboard.SetContent(data);
        }

        // ---- Toolbar / commit-row entry points -------------------------------------------------

        /// <summary>Toolbar Branch / Actions ▸ Branch…: create from the selected commit when there
        /// is one, else from HEAD.</summary>
        public Task ShowCreateBranchDialogFromSelectionAsync()
        {
            CommitRow? c = Vm.SelectedCommit;
            return c == null
                ? ShowCreateBranchDialogAsync("", "HEAD")
                : ShowCreateBranchDialogAsync(c.FullHash, $"{c.Hash} — {c.Message}");
        }

        private async void BranchToolbar_Click(object sender, RoutedEventArgs e)
            => await ShowCreateBranchDialogFromSelectionAsync();

        private async void TagToolbar_Click(object sender, RoutedEventArgs e)
        {
            CommitRow? c = Vm.SelectedCommit;
            if (c == null)
                await ShowCreateTagDialogAsync("", "HEAD");
            else
                await ShowCreateTagDialogAsync(c.FullHash, $"{c.Hash} — {c.Message}");
        }

        private async void CreateBranchHere_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is CommitRow c)
                await ShowCreateBranchDialogAsync(c.FullHash, $"{c.Hash} — {c.Message}");
        }

        private async void CreateTagHere_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is CommitRow c)
                await ShowCreateTagDialogAsync(c.FullHash, $"{c.Hash} — {c.Message}");
        }

        private async void CheckoutCommit_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.DataContext is not CommitRow c)
                return;

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Check out commit",
                Content = new TextBlock
                {
                    Text = $"Checking out {c.Hash} leaves HEAD detached — new commits will not "
                         + "belong to any branch. Create a branch first if you plan to commit.",
                    TextWrapping = TextWrapping.Wrap,
                },
                PrimaryButtonText = "Check Out",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                await Vm.CheckoutCommitAsync(c.FullHash);
        }

        // ---- The dialogs ----------------------------------------------------------------------

        /// <summary>BR-003. Warns when the working tree is dirty, then runs a plain switch: git
        /// carries the changes across when it safely can and refuses otherwise, and that refusal
        /// reaches the InfoBar. Nothing here forces or stashes.</summary>
        private async Task SwitchToBranchAsync(string name)
        {
            int changes = await Vm.CountLocalChangesAsync();
            if (changes > 0)
            {
                var warn = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = "Switch branch",
                    Content = new TextBlock
                    {
                        Text = $"You have {changes} uncommitted change{(changes == 1 ? "" : "s")}. "
                             + $"Git will carry {(changes == 1 ? "it" : "them")} across to {name} when it "
                             + "safely can, and refuse the switch otherwise.",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    PrimaryButtonText = "Switch",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Primary,
                };
                if (await warn.ShowAsync() != ContentDialogResult.Primary)
                    return;
            }
            await Vm.CheckoutBranchAsync(name);
        }

        /// <summary>BR-004. <paramref name="startPoint"/> is passed to git ("" = HEAD);
        /// <paramref name="startLabel"/> is what the picker shows for it.</summary>
        public async Task ShowCreateBranchDialogAsync(string startPoint, string startLabel)
        {
            if (!Vm.HasRepository)
            {
                await ShowMessageAsync("Create branch", "Open a repository first.");
                return;
            }

            var nameBox = new TextBox
            {
                Header = "Branch name",
                PlaceholderText = "feature/my-change",
                MinWidth = 360,
            };
            // The invoking ref/commit heads the list and is preselected; the rest are the same
            // HEAD + branches + remotes + tags the compare picker offers.
            var options = new List<string> { startLabel };
            foreach (string n in Vm.StartPointOptions)
                if (n != startLabel)
                    options.Add(n);
            var startBox = new ComboBox
            {
                Header = "Starting point",
                ItemsSource = options,
                SelectedIndex = 0,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            var checkoutBox = new CheckBox { Content = "Check out new branch", IsChecked = true };

            var panel = new StackPanel { Spacing = 12, MinWidth = 360 };
            panel.Children.Add(nameBox);
            panel.Children.Add(startBox);
            panel.Children.Add(checkoutBox);

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Create branch",
                Content = panel,
                PrimaryButtonText = "Create",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                IsPrimaryButtonEnabled = false,
            };
            nameBox.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = nameBox.Text.Trim().Length > 0;
            dialog.Opened += (_, _) => nameBox.Focus(FocusState.Programmatic);

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;

            // Index 0 is the invoking ref, whose label is cosmetic — send the real start point.
            string start = startBox.SelectedIndex == 0
                ? startPoint
                : startBox.SelectedItem as string ?? "";
            if (start == "HEAD")
                start = "";
            await Vm.CreateBranchAsync(nameBox.Text, start, checkoutBox.IsChecked == true);
        }

        /// <summary>BR-006.</summary>
        private async Task ShowRenameBranchDialogAsync(string oldName)
        {
            var nameBox = new TextBox { Header = "Branch name", Text = oldName, MinWidth = 360 };
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Rename branch",
                Content = nameBox,
                PrimaryButtonText = "Rename",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
            };
            nameBox.TextChanged += (_, _) =>
                dialog.IsPrimaryButtonEnabled = nameBox.Text.Trim().Length > 0;
            dialog.Opened += (_, _) =>
            {
                nameBox.Focus(FocusState.Programmatic);
                nameBox.SelectAll();
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary
                && nameBox.Text.Trim() is { Length: > 0 } newName && newName != oldName)
            {
                await Vm.RenameBranchAsync(oldName, newName);
            }
        }

        /// <summary>BR-005: always try the safe delete first, and only offer the forced form once
        /// git has refused — quoting git's own reason.</summary>
        private async Task DeleteBranchWithConfirmAsync(string name)
        {
            var confirm = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Delete branch",
                Content = new TextBlock
                {
                    Text = $"Delete branch {name}? This cannot be undone.",
                    TextWrapping = TextWrapping.Wrap,
                },
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary)
                return;

            string? error = await Vm.DeleteBranchAsync(name, force: false);
            if (error == null)
                return;

            var force = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Branch not deleted",
                Content = new TextBlock
                {
                    Text = $"{error}\n\nDeleting anyway discards commits that exist only on this branch.",
                    TextWrapping = TextWrapping.Wrap,
                },
                PrimaryButtonText = "Delete Anyway",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await force.ShowAsync() == ContentDialogResult.Primary)
                await Vm.DeleteBranchAsync(name, force: true);
        }

        /// <summary>TAG-002. An empty message means a lightweight tag.</summary>
        private async Task ShowCreateTagDialogAsync(string commitish, string atLabel)
        {
            if (!Vm.HasRepository)
            {
                await ShowMessageAsync("Create tag", "Open a repository first.");
                return;
            }

            var nameBox = new TextBox { Header = "Tag name", PlaceholderText = "v1.0.0", MinWidth = 360 };
            // Muted via opacity rather than a ThemeResource lookup, which does not resolve
            // reliably through Application.Current.Resources for theme-dictionary brushes.
            var atText = new TextBlock
            {
                Text = $"at {atLabel}",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.7,
            };
            var messageBox = new TextBox
            {
                Header = "Message (optional) — leave empty for a lightweight tag",
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 60,
                MaxHeight = 140,
                MinWidth = 360,
            };

            var panel = new StackPanel { Spacing = 12, MinWidth = 360 };
            panel.Children.Add(nameBox);
            panel.Children.Add(atText);
            panel.Children.Add(messageBox);

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Create tag",
                Content = panel,
                PrimaryButtonText = "Create",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                IsPrimaryButtonEnabled = false,
            };
            nameBox.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = nameBox.Text.Trim().Length > 0;
            dialog.Opened += (_, _) => nameBox.Focus(FocusState.Programmatic);

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                await Vm.CreateTagAsync(nameBox.Text, commitish, messageBox.Text);
        }

        // ---- Remotes (Phase 6, REMOTE-001..009) ------------------------------------------------

        /// <summary>REMOTE-002. Picks a remote (or all of them), then runs the fetch behind the
        /// progress dialog.</summary>
        public async Task ShowFetchDialogAsync()
        {
            IReadOnlyList<RemoteInfo> remotes = await RequireRemotesAsync("Fetch");
            if (remotes.Count == 0)
                return;

            var remoteBox = new ComboBox
            {
                Header = "Remote",
                ItemsSource = remotes.Select(r => r.Name).ToList(),
                SelectedIndex = 0,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            var allBox = new CheckBox { Content = "Fetch from all remotes" };
            // Both default off, matching git's own defaults — pruning silently deleting local
            // remote-tracking refs should be a decision, not a surprise.
            var pruneBox = new CheckBox { Content = "Prune deleted remote branches" };
            var tagsBox = new CheckBox { Content = "Fetch all tags" };
            allBox.Checked += (_, _) => remoteBox.IsEnabled = false;
            allBox.Unchecked += (_, _) => remoteBox.IsEnabled = true;

            var panel = new StackPanel { Spacing = 12, MinWidth = 360 };
            panel.Children.Add(remoteBox);
            panel.Children.Add(allBox);
            panel.Children.Add(pruneBox);
            panel.Children.Add(tagsBox);

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Fetch",
                Content = panel,
                PrimaryButtonText = "Fetch",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;

            bool all = allBox.IsChecked == true;
            string remote = remoteBox.SelectedItem as string ?? "";
            bool prune = pruneBox.IsChecked == true;
            bool tags = tagsBox.IsChecked == true;

            await RemoteProgressDialog.RunAsync(
                XamlRoot, "Fetch",
                "git fetch --progress " + (all ? "--all" : remote)
                    + (prune ? " --prune" : "") + (tags ? " --tags" : ""),
                (progress, token) => Vm.FetchAsync(remote, all, prune, tags, progress, token));
        }

        /// <summary>REMOTE-004. Warns about uncommitted changes first, then fast-forwards.</summary>
        public async Task PullAsync()
        {
            if (!Vm.CanPull)
            {
                await ShowMessageAsync("Pull", "Check out a branch first — a detached HEAD has no "
                                             + "upstream to pull from.");
                return;
            }
            if (!Vm.HasUpstream)
            {
                await ShowMessageAsync("Pull",
                    $"{Vm.CurrentBranchName} has no upstream branch yet. Push it with "
                    + "“Set upstream” checked to publish it, then pull.");
                return;
            }

            // REMOTE-004: a dirty tree is where a pull goes wrong, so say so before running it.
            // Untracked files count here even though they are excluded from the branch-switch
            // warning: a switch is never blocked by them, but a fast-forward is, whenever an
            // incoming commit adds a path one of them already occupies. Ignored files are not
            // untracked as far as `git status` is concerned, so this does not fire on build output.
            var (tracked, untracked) = await Vm.CountWorkingTreeAsync();
            if (tracked > 0 || untracked > 0)
            {
                var warn = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = "Pull",
                    Content = new TextBlock
                    {
                        Text = DirtyTreeWarning(tracked, untracked, Vm.CurrentBranchName),
                        TextWrapping = TextWrapping.Wrap,
                    },
                    PrimaryButtonText = "Pull Anyway",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Close,
                };
                if (await warn.ShowAsync() != ContentDialogResult.Primary)
                    return;
            }

            await RemoteProgressDialog.RunAsync(
                XamlRoot, "Pull",
                $"git pull --ff-only --progress {Vm.UpstreamRemote} {Vm.UpstreamBranch}".TrimEnd(),
                (progress, token) => Vm.PullAsync(progress, token));
        }

        /// <summary>Names what is actually in the way, and gives each kind the remedy git will ask
        /// for: tracked edits get committed or stashed, untracked files get moved out of the way.
        /// Saying "uncommitted changes" for an untracked file sends the user looking for something
        /// to commit that does not exist.</summary>
        private static string DirtyTreeWarning(int tracked, int untracked, string branch)
        {
            string what = (tracked, untracked) switch
            {
                ( > 0, > 0) => $"You have {Count(tracked, "uncommitted change")} and "
                             + $"{Count(untracked, "untracked file")}.",
                ( > 0, _) => $"You have {Count(tracked, "uncommitted change")}.",
                _ => $"You have {Count(untracked, "untracked file")}.",
            };

            string risk = tracked > 0
                ? $"Git will refuse to fast-forward {branch} if the incoming commits change the "
                  + "same files"
                : $"Git will refuse to fast-forward {branch} if the incoming commits add a file "
                  + "where one of yours already sits";
            if (tracked > 0 && untracked > 0)
                risk += ", or add a file where one of your untracked files already sits";

            string fix = (tracked, untracked) switch
            {
                ( > 0, > 0) => "Commit or stash your changes, and move the untracked files aside, "
                             + "if you would rather be safe.",
                ( > 0, _) => "Commit or stash first if you would rather be safe.",
                _ => "Move or delete them first if you would rather be safe.",
            };

            return $"{what} {risk}. {fix}";
        }

        private static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";

        /// <summary>REMOTE-005, and REMOTE-006 when the branch has no upstream yet.</summary>
        public async Task ShowPushDialogAsync()
        {
            if (!Vm.CanPush)
            {
                await ShowMessageAsync("Push", "Check out a branch first — a detached HEAD has "
                                             + "nothing to push.");
                return;
            }
            IReadOnlyList<RemoteInfo> remotes = await RequireRemotesAsync("Push");
            if (remotes.Count == 0)
                return;

            string branch = Vm.CurrentBranchName;
            bool publishing = !Vm.HasUpstream;

            var names = remotes.Select(r => r.Name).ToList();
            int preselected = publishing ? 0 : Math.Max(0, names.IndexOf(Vm.UpstreamRemote));
            var remoteBox = new ComboBox
            {
                Header = "Remote",
                ItemsSource = names,
                SelectedIndex = preselected,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            var branchText = new TextBlock
            {
                Text = publishing
                    ? $"Publishing {branch} — it has no upstream yet."
                    : $"Pushing {branch} to {Vm.UpstreamName}.",
                TextWrapping = TextWrapping.Wrap,
                // Muted via opacity rather than a ThemeResource lookup, which does not resolve
                // reliably through Application.Current.Resources for theme-dictionary brushes.
                Opacity = 0.7,
                FontSize = 12,
            };
            // REMOTE-006: tracking is the point of publishing, so it is pre-checked exactly then.
            var upstreamBox = new CheckBox
            {
                Content = "Set upstream (track this remote branch)",
                IsChecked = publishing,
            };
            var tagsBox = new CheckBox { Content = "Push tags" };

            var panel = new StackPanel { Spacing = 12, MinWidth = 380 };
            panel.Children.Add(remoteBox);
            panel.Children.Add(branchText);
            panel.Children.Add(upstreamBox);
            panel.Children.Add(tagsBox);

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = publishing ? "Publish branch" : "Push",
                Content = panel,
                PrimaryButtonText = publishing ? "Publish" : "Push",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;

            string remote = remoteBox.SelectedItem as string ?? "";
            bool setUpstream = upstreamBox.IsChecked == true;
            bool pushTags = tagsBox.IsChecked == true;

            await RemoteProgressDialog.RunAsync(
                XamlRoot, publishing ? "Publish branch" : "Push",
                "git push --progress" + (setUpstream ? " --set-upstream" : "")
                    + (pushTags ? " --tags" : "") + $" {remote} {branch}",
                (progress, token) => Vm.PushAsync(remote, branch, setUpstream, pushTags, progress, token));
        }

        /// <summary>REMOTE-001 / REMOTE-008: list every remote's URLs, and edit one in place.</summary>
        public async Task ShowRemotesDialogAsync()
        {
            if (!Vm.HasRepository)
            {
                await ShowMessageAsync("Remotes", "Open a repository first.");
                return;
            }

            IReadOnlyList<RemoteInfo> remotes = await Vm.ListRemotesAsync();
            var panel = new StackPanel { Spacing = 12, MinWidth = 460 };
            if (remotes.Count == 0)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "This repository has no remotes configured.",
                    TextWrapping = TextWrapping.Wrap,
                });
            }
            foreach (RemoteInfo remote in remotes)
            {
                var block = new StackPanel { Spacing = 2 };
                block.Children.Add(new TextBlock { Text = remote.Name, FontWeight = FontWeights.SemiBold });
                block.Children.Add(new TextBlock
                {
                    Text = remote.FetchUrl,
                    TextWrapping = TextWrapping.Wrap,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 12,
                    Opacity = 0.8,
                });
                if (remote.HasSeparatePushUrl)
                {
                    block.Children.Add(new TextBlock
                    {
                        Text = "push: " + remote.PushUrl,
                        TextWrapping = TextWrapping.Wrap,
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 12,
                        Opacity = 0.8,
                    });
                }
                var edit = new HyperlinkButton { Content = "Edit URL…", Padding = new Thickness(0, 2, 0, 0) };
                RemoteInfo captured = remote;
                edit.Click += async (_, _) =>
                {
                    // One editor at a time: close the list, edit, then reopen it so the change is
                    // visible without rebuilding the panel in place.
                    listDialog?.Hide();
                    if (await ShowEditRemoteUrlDialogAsync(captured))
                        await ShowRemotesDialogAsync();
                };
                block.Children.Add(edit);
                panel.Children.Add(block);
            }

            listDialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Remotes",
                Content = new ScrollViewer { Content = panel, MaxHeight = 440 },
                CloseButtonText = "Close",
                DefaultButton = ContentDialogButton.Close,
            };
            await listDialog.ShowAsync();
            listDialog = null;
        }

        // Held so the "Edit URL…" link inside the list can dismiss the list it lives in.
        private ContentDialog? listDialog;

        /// <summary>REMOTE-008. Validates before saving, so an obviously-broken URL never reaches
        /// git config.</summary>
        private async Task<bool> ShowEditRemoteUrlDialogAsync(RemoteInfo remote)
        {
            var urlBox = new TextBox
            {
                Header = $"Fetch URL for {remote.Name}",
                Text = remote.FetchUrl,
                MinWidth = 440,
            };
            var error = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Visibility = Visibility.Collapsed,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed),
            };
            var alsoPushBox = new CheckBox
            {
                Content = "Also update the push URL",
                IsChecked = remote.HasSeparatePushUrl,
                // Only meaningful when a distinct push URL exists; otherwise push follows fetch.
                Visibility = remote.HasSeparatePushUrl ? Visibility.Visible : Visibility.Collapsed,
            };

            var panel = new StackPanel { Spacing = 8, MinWidth = 440 };
            panel.Children.Add(urlBox);
            panel.Children.Add(error);
            panel.Children.Add(alsoPushBox);

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Edit remote URL",
                Content = panel,
                PrimaryButtonText = "Save",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
            };
            dialog.Opened += (_, _) => { urlBox.Focus(FocusState.Programmatic); urlBox.SelectAll(); };

            // Validate on the way out rather than disabling Save: the user gets told what is
            // wrong instead of being left guessing why the button does nothing.
            dialog.PrimaryButtonClick += (_, args) =>
            {
                string? problem = ValidateRemoteUrl(urlBox.Text);
                if (problem == null)
                    return;
                args.Cancel = true;
                error.Text = problem;
                error.Visibility = Visibility.Visible;
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return false;

            string url = urlBox.Text.Trim();
            string? failure = await Vm.SetRemoteUrlAsync(remote.Name, url, pushUrl: false);
            if (failure == null && alsoPushBox.IsChecked == true)
                failure = await Vm.SetRemoteUrlAsync(remote.Name, url, pushUrl: true);
            if (failure != null)
            {
                await ShowMessageAsync("Edit remote URL", failure);
                return false;
            }
            return true;
        }

        /// <summary>Null when <paramref name="url"/> is a shape git can use, otherwise the reason.
        /// Deliberately permissive: git accepts scp-style, several schemes, and bare paths, and
        /// rejecting something git would have accepted is worse than letting git say no.</summary>
        private static string? ValidateRemoteUrl(string url)
        {
            string u = (url ?? "").Trim();
            if (u.Length == 0)
                return "Enter a URL.";
            if (u.Any(char.IsWhiteSpace))
                return "A remote URL cannot contain spaces.";

            // scheme://host/path — https, ssh, git, file, ftp(s)…
            if (u.Contains("://", StringComparison.Ordinal))
            {
                int scheme = u.IndexOf("://", StringComparison.Ordinal);
                if (scheme == 0)
                    return "The URL is missing its scheme (for example https:// or ssh://).";
                if (u.Length <= scheme + 3)
                    return "The URL has a scheme but no host.";
                return null;
            }
            // scp-style "user@host:path" — what GitHub hands out for SSH.
            if (u.Contains(':') && u.Contains('@'))
                return null;
            // A local path (another clone on disk, or a bare repository) must actually exist.
            if (Directory.Exists(u))
                return null;

            return "Enter a URL like https://host/repo.git, git@host:owner/repo.git, "
                 + "or the path to a local repository.";
        }

        /// <summary>The repository's remotes, or an empty list after explaining why an operation
        /// cannot run. Read fresh each time — a remote added outside the app has no refs yet, so
        /// the sidebar is not a reliable source.</summary>
        private async Task<IReadOnlyList<RemoteInfo>> RequireRemotesAsync(string title)
        {
            if (!Vm.HasRepository)
            {
                await ShowMessageAsync(title, "Open a repository first.");
                return Array.Empty<RemoteInfo>();
            }
            IReadOnlyList<RemoteInfo> remotes = await Vm.ListRemotesAsync();
            if (remotes.Count == 0)
            {
                await ShowMessageAsync(title,
                    "This repository has no remotes configured, so there is nowhere to "
                    + $"{title.ToLowerInvariant()}. Add one with “git remote add” and try again.");
            }
            return remotes;
        }

        private async void FetchToolbar_Click(object sender, RoutedEventArgs e)
            => await ShowFetchDialogAsync();

        private async void PullToolbar_Click(object sender, RoutedEventArgs e) => await PullAsync();

        private async void PushToolbar_Click(object sender, RoutedEventArgs e)
            => await ShowPushDialogAsync();

        private async void SidebarEditRemote_Click(object sender, RoutedEventArgs e)
        {
            SidebarItemVM? item = SidebarItemOf(sender);
            if (item == null)
                return;
            IReadOnlyList<RemoteInfo> remotes = await Vm.ListRemotesAsync();
            RemoteInfo? match = remotes.FirstOrDefault(r => r.Name == item.Text);
            if (match == null)
            {
                await ShowMessageAsync("Edit remote URL", $"No remote named {item.Text} was found.");
                return;
            }
            await ShowEditRemoteUrlDialogAsync(match);
        }

        /// <summary>TAG-003.</summary>
        private async Task DeleteTagWithConfirmAsync(string name)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Delete tag",
                Content = new TextBlock
                {
                    Text = $"Delete tag {name}? This removes it from this repository only; a copy "
                         + "already pushed to a remote is unaffected.",
                    TextWrapping = TextWrapping.Wrap,
                },
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                await Vm.DeleteTagAsync(name);
        }
    }
}
