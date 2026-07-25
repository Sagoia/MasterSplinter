using System;
using System.Collections.Generic;
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
