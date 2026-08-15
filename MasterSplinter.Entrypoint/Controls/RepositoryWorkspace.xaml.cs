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
                if (e.PropertyName == nameof(MainViewModel.IsWorkingCopyMode) ||
                    e.PropertyName == nameof(MainViewModel.IsReflogMode))
                {
                    SyncModeBar();
                }
                // A blame window holds its own GitRepository, so it has to go when the workspace
                // moves to a different one (or to none) — otherwise it keeps showing a file from a
                // repository the user has closed.
                else if (e.PropertyName == nameof(MainViewModel.Repository))
                {
                    CloseBlameWindows();
                }
            };
        }

        private void SyncModeBar()
        {
            _syncingModeBar = true;
            try
            {
                ModeBar.SelectedItem = Vm.IsWorkingCopyMode ? ModeFileStatus
                                     : Vm.IsReflogMode ? ModeReflog
                                     : ModeLogHistory;
            }
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
                Vm.ExitReflog();
            }
            else if (sender.SelectedItem == ModeReflog)
            {
                await Vm.EnterReflogAsync();
            }
            else
            {
                // Search is an action on the current list, not a mode of its own: put the caret in
                // the box and snap the tab back to whatever is actually showing.
                SyncModeBar();
                SearchBox.Focus(FocusState.Programmatic);
                SearchBox.SelectAll();
            }
        }

        // ---- Search (Phase 8, SEARCH-001/002) --------------------------------------------------

        private async void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != Windows.System.VirtualKey.Enter)
                return;
            e.Handled = true;
            await Vm.RunSearchAsync();
        }

        // Button.Flyout has proven unreliable on these styled icon buttons (see the diff-settings
        // flyout), so it is opened explicitly here too.
        private void SearchOptions_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe)
                Microsoft.UI.Xaml.Controls.Primitives.FlyoutBase.ShowAttachedFlyout(fe);
        }

        private async void RunSearch_Click(object sender, RoutedEventArgs e)
        {
            // The flyout hosts this button, so it has to close before the results it produced can
            // be seen behind it.
            SearchOptionsFlyout.Hide();
            await Vm.RunSearchAsync();
        }

        private async void SearchBanner_Close(InfoBar sender, object args)
            => await Vm.ClearSearchResultsAsync();

        // ---- Reflog (Phase 8, REFLOG-001) ------------------------------------------------------

        private async void Reflog_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count > 0 && e.AddedItems[0] is ReflogEntry entry)
                await Vm.SelectReflogEntryAsync(entry);
        }

        private static ReflogEntry? ReflogEntryOf(object sender)
            => (sender as FrameworkElement)?.DataContext as ReflogEntry;

        private async void ReflogCreateBranch_Click(object sender, RoutedEventArgs e)
        {
            ReflogEntry? entry = ReflogEntryOf(sender);
            if (entry == null) return;
            // The sha, not the selector: HEAD@{5} is relative to the reflog and would drift the
            // moment anything else moves HEAD.
            await ShowCreateBranchDialogAsync(entry.Sha, $"commit {entry.ShortSha}");
        }

        private async void ReflogCheckout_Click(object sender, RoutedEventArgs e)
        {
            ReflogEntry? entry = ReflogEntryOf(sender);
            if (entry == null) return;
            await CheckoutCommitWithConfirmAsync(entry.Sha, entry.ShortSha, entry.Description);
        }

        private void ReflogCopyHash_Click(object sender, RoutedEventArgs e)
        {
            ReflogEntry? entry = ReflogEntryOf(sender);
            if (entry != null)
                CopyToClipboard(entry.Sha);
        }

        private async void Refresh_Click(object sender, RoutedEventArgs e) => await Vm.RefreshAsync();

        private async void RefreshAccelerator_Invoked(KeyboardAccelerator sender,
            KeyboardAcceleratorInvokedEventArgs args)
        {
            args.Handled = true;
            await Vm.RefreshAsync(); // no-ops while a load is already running
        }

        // The list is Extended-select so several commits can be cherry-picked in one go (CHERRY-002).
        // The detail pane follows the LAST row added to the selection, which is the one the user
        // just clicked — taking AddedItems[0] would make a shift-select jump to the far end.
        private void Commits_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count > 0 && e.AddedItems[^1] is CommitRow row)
                Vm.SelectedCommit = row;
        }

        /// <summary>The commits the user has selected, in list order (top row first).</summary>
        private List<CommitRow> SelectedCommits()
        {
            var rows = CommitsList.SelectedItems.OfType<CommitRow>().ToList();
            if (rows.Count == 0 && Vm.SelectedCommit != null)
                rows.Add(Vm.SelectedCommit);
            return rows;
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

        /// <summary>
        /// Yes/no confirmation in the house style: the primary button is the VERB ("Delete",
        /// "Drop"), and the default button is Close so Enter cancels rather than commits.
        /// </summary>
        private async Task<bool> ConfirmAsync(string title, string message, string primaryText)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = title,
                Content = new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 400,
                },
                PrimaryButtonText = primaryText,
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
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

            // A conflicted row is a different kind of thing: it cannot be staged as-is or discarded
            // in any meaningful sense, and the only useful verbs are "resolve it" and "I have".
            bool conflicted = file.Area == WorkTreeArea.Conflicted;

            foreach (var item in menu.Items)
            {
                if (item is not MenuFlyoutItem mi || mi.Tag is not string tag)
                    continue;
                bool visible = tag switch
                {
                    "resolve" => conflicted,
                    "markresolved" => conflicted,
                    "stage" => !conflicted && file.Area != WorkTreeArea.Staged,
                    "unstage" => !conflicted && file.Area == WorkTreeArea.Staged,
                    "discard" => !conflicted && file.Area != WorkTreeArea.Staged,
                    // BLAME-001: an untracked file has no history to attribute, and a conflicted
                    // one blames the pre-merge state, which is never what the user is asking about.
                    "blame" => !conflicted && file.Area != WorkTreeArea.Untracked,
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
            bool isStash = item.Kind == SidebarKind.Stash;

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
                    // Phase 7: merging or rebasing onto the branch you are already on is a no-op,
                    // and a tag is not something you rebase onto in this UI.
                    "merge" => (isBranch || isRemote) && !item.IsCurrent,
                    "rebase" => (isBranch || isRemote) && !item.IsCurrent,
                    "sepmerge" => (isBranch || isRemote) && !item.IsCurrent,
                    "sep" => isRef || isStash,
                    "deletebranch" => isBranch && !item.IsCurrent,
                    "deletetag" => isTag,
                    // Phase 8 (STASH-002/003/004).
                    "stashapply" => isStash,
                    "stashpop" => isStash,
                    "stashdrop" => isStash,
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
            await CheckoutCommitWithConfirmAsync(c.FullHash, c.Hash);
        }

        /// <summary>BR-003 (detached). Shared with the reflog, where checking out an unreachable
        /// commit is the whole point — the detached-HEAD warning matters more there, not less.</summary>
        private async Task CheckoutCommitWithConfirmAsync(string fullHash, string shortHash,
                                                          string? description = null)
        {
            string what = string.IsNullOrWhiteSpace(description)
                ? shortHash
                : $"{shortHash} ({description})";
            if (await ConfirmAsync("Check out commit",
                    $"Checking out {what} leaves HEAD detached — new commits will not belong to "
                    + "any branch. Create a branch first if you plan to commit.",
                    "Check Out"))
            {
                await Vm.CheckoutCommitAsync(fullHash);
            }
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

            await GitProgressDialog.RunAsync(
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

            await GitProgressDialog.RunAsync(
                XamlRoot, "Pull",
                $"git pull --ff-only --progress {Vm.UpstreamRemote} {Vm.UpstreamBranch}".TrimEnd(),
                (progress, token) => Vm.PullAsync(progress, token));
        }

        /// <summary>Names what is actually in the way, and gives each kind the remedy git will ask
        /// for: tracked edits get committed or stashed, untracked files get moved out of the way.
        /// Saying "uncommitted changes" for an untracked file sends the user looking for something
        /// to commit that does not exist.</summary>
        private static string DirtyTreeWarning(int tracked, int untracked, string branch,
                                               string verb = "fast-forward")
        {
            string what = (tracked, untracked) switch
            {
                ( > 0, > 0) => $"You have {Count(tracked, "uncommitted change")} and "
                             + $"{Count(untracked, "untracked file")}.",
                ( > 0, _) => $"You have {Count(tracked, "uncommitted change")}.",
                _ => $"You have {Count(untracked, "untracked file")}.",
            };

            string risk = tracked > 0
                ? $"Git will refuse to {verb} {branch} if the incoming commits change the "
                  + "same files"
                : $"Git will refuse to {verb} {branch} if the incoming commits add a file "
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

            await GitProgressDialog.RunAsync(
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

        // ---- Merge / rebase / cherry-pick / revert (Phase 7) -----------------------------------

        /// <summary>Entry points. Everything here funnels into <see cref="GitProgressDialog"/>, so
        /// a conflict leaves git's own CONFLICT text on screen rather than a bare error bar.</summary>
        private async void MergeToolbar_Click(object sender, RoutedEventArgs e)
            => await ShowMergeDialogAsync("");

        private async void SidebarMerge_Click(object sender, RoutedEventArgs e)
        {
            SidebarItemVM? item = SidebarItemOf(sender);
            if (item != null)
                await ShowMergeDialogAsync(item.ShortName);
        }

        private async void SidebarRebase_Click(object sender, RoutedEventArgs e)
        {
            SidebarItemVM? item = SidebarItemOf(sender);
            if (item != null)
                await ShowRebaseDialogAsync(item.ShortName);
        }

        /// <summary>Rewrites the cherry-pick item to say how many commits it would apply, so a
        /// multi-row selection is obvious before the dialog opens. The item is found by tag rather
        /// than by name: it lives inside a DataTemplate, so x:Name is scoped to the template and
        /// invisible here.</summary>
        private void CommitMenu_Opening(object sender, object e)
        {
            if (sender is not MenuFlyout menu)
                return;
            int count = SelectedCommits().Count;
            foreach (var entry in menu.Items)
            {
                if (entry is MenuFlyoutItem mi && (mi.Tag as string) == "cherrypick")
                {
                    mi.Text = count > 1 ? $"Cherry-pick {count} Commits…" : "Cherry-pick Commit…";
                    return;
                }
            }
        }

        /// <summary>MERGE-001. <paramref name="preferred"/> preselects the invoking branch.</summary>
        public async Task ShowMergeDialogAsync(string preferred)
        {
            if (!await RequireIdleRepositoryAsync("Merge"))
                return;
            if (Vm.CurrentBranchName.Length == 0)
            {
                await ShowMessageAsync("Merge", "Check out a branch first — a detached HEAD has "
                                              + "nothing to merge into.");
                return;
            }

            // Everything except the current branch: merging a branch into itself does nothing.
            var options = Vm.CompareRefNames
                .Where(n => n != "HEAD" && n != Vm.CurrentBranchName)
                .ToList();
            if (options.Count == 0)
            {
                await ShowMessageAsync("Merge", "There is no other branch or tag to merge in.");
                return;
            }

            int preselected = Math.Max(0, options.IndexOf(preferred));
            var sourceBox = new ComboBox
            {
                Header = $"Merge into {Vm.CurrentBranchName}",
                ItemsSource = options,
                SelectedIndex = preselected,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            var noFfBox = new CheckBox
            {
                Content = "Always create a merge commit (--no-ff)",
                IsChecked = false,
            };
            var noCommitBox = new CheckBox
            {
                Content = "Do not commit automatically (--no-commit)",
                IsChecked = false,
            };

            var panel = new StackPanel { Spacing = 12, MinWidth = 380 };
            panel.Children.Add(sourceBox);
            panel.Children.Add(noFfBox);
            panel.Children.Add(noCommitBox);

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Merge",
                Content = panel,
                PrimaryButtonText = "Merge",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;

            string source = sourceBox.SelectedItem as string ?? "";
            if (source.Length == 0)
                return;
            bool noFf = noFfBox.IsChecked == true;
            bool noCommit = noCommitBox.IsChecked == true;

            // Same wording as the pull path: a dirty tree is where a merge goes wrong, and the two
            // halves of "dirty" need different advice.
            if (!await ConfirmDirtyTreeAsync("Merge", "merge into"))
                return;

            await GitProgressDialog.RunAsync(
                XamlRoot, "Merge",
                "git merge --no-edit" + (noFf ? " --no-ff" : "") + (noCommit ? " --no-commit" : "")
                    + $" -- {source}",
                (progress, token) => Vm.MergeAsync(source, noFf, noCommit, progress, token));
        }

        /// <summary>REBASE-001 — "warning before operation" is the point of this dialog, not the
        /// options on it.</summary>
        public async Task ShowRebaseDialogAsync(string preferred)
        {
            if (!await RequireIdleRepositoryAsync("Rebase"))
                return;
            string branch = Vm.CurrentBranchName;
            if (branch.Length == 0)
            {
                await ShowMessageAsync("Rebase", "Check out a branch first — a detached HEAD has "
                                               + "nothing to rebase.");
                return;
            }

            var options = Vm.CompareRefNames
                .Where(n => n != "HEAD" && n != branch)
                .ToList();
            if (options.Count == 0)
            {
                await ShowMessageAsync("Rebase", "There is no other branch to rebase onto.");
                return;
            }

            int preselected = Math.Max(0, options.IndexOf(preferred));
            var upstreamBox = new ComboBox
            {
                Header = $"Rebase {branch} onto",
                ItemsSource = options,
                SelectedIndex = preselected,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            var warning = new TextBlock
            {
                Text = $"Rebasing replaces every commit on {branch} that is not already on the "
                     + "chosen branch with a new commit that has a different SHA. Anyone who has "
                     + $"already pulled {branch} will be left on the old commits — do not rebase a "
                     + "branch you have shared.",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 400,
            };

            var panel = new StackPanel { Spacing = 12, MinWidth = 400 };
            panel.Children.Add(upstreamBox);
            panel.Children.Add(warning);

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Rebase",
                Content = panel,
                PrimaryButtonText = "Rebase",
                CloseButtonText = "Cancel",
                // Close is the default here, unlike merge: this one rewrites history.
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;

            string upstream = upstreamBox.SelectedItem as string ?? "";
            if (upstream.Length == 0)
                return;

            // git refuses a rebase with uncommitted changes outright (no --autostash here by
            // choice), so saying so first is cheaper than showing the user a failure.
            var (tracked, _) = await Vm.CountWorkingTreeAsync();
            if (tracked > 0)
            {
                await ShowMessageAsync("Rebase",
                    $"You have {Count(tracked, "uncommitted change")}. Git will refuse to rebase "
                    + "with a dirty working tree — commit or stash first.");
                return;
            }

            await GitProgressDialog.RunAsync(
                XamlRoot, "Rebase", $"git rebase {upstream}",
                (progress, token) => Vm.RebaseAsync(upstream, progress, token));
        }

        /// <summary>CHERRY-001/002.</summary>
        private async void CherryPickCommits_Click(object sender, RoutedEventArgs e)
        {
            if (!await RequireIdleRepositoryAsync("Cherry-pick"))
                return;

            List<CommitRow> commits = SelectedCommits();
            if (commits.Count == 0)
                return;

            // The order git will apply them in, which is not necessarily the order they appear on
            // screen — showing it is what makes a multi-commit pick reviewable before it runs.
            var ordered = Vm.OrderForCherryPick(commits);
            var list = new StackPanel { Spacing = 2 };
            foreach (CommitRow c in ordered)
            {
                list.Children.Add(new TextBlock
                {
                    Text = $"{c.Hash}  {c.Message}",
                    FontSize = 12,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 420,
                });
            }

            var intro = new TextBlock
            {
                Text = ordered.Count == 1
                    ? "This commit will be applied on top of the current branch:"
                    : $"These {ordered.Count} commits will be applied on top of the current branch, "
                      + "in this order:",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 420,
            };
            var noCommitBox = new CheckBox
            {
                Content = "Stage the changes without committing (-n)",
                IsChecked = false,
            };

            var panel = new StackPanel { Spacing = 10, MinWidth = 420 };
            panel.Children.Add(intro);
            panel.Children.Add(new ScrollViewer { Content = list, MaxHeight = 220 });
            panel.Children.Add(noCommitBox);

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = ordered.Count == 1 ? "Cherry-pick commit" : $"Cherry-pick {ordered.Count} commits",
                Content = panel,
                PrimaryButtonText = "Cherry-pick",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;

            bool noCommit = noCommitBox.IsChecked == true;
            await GitProgressDialog.RunAsync(
                XamlRoot, "Cherry-pick",
                "git cherry-pick --no-edit" + (noCommit ? " -n" : "")
                    + " " + string.Join(" ", ordered.Select(c => c.Hash)),
                (progress, token) => Vm.CherryPickAsync(commits, noCommit, progress, token));
        }

        /// <summary>REVERT-001 — confirmation required, and for a merge commit git also needs to be
        /// told which parent to treat as the mainline.</summary>
        private async void RevertCommit_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.DataContext is not CommitRow commit)
                return;
            if (!await RequireIdleRepositoryAsync("Revert"))
                return;

            bool isMerge = commit.ParentHashes.Length > 1;

            var panel = new StackPanel { Spacing = 12, MinWidth = 400 };
            panel.Children.Add(new TextBlock
            {
                Text = $"Revert {commit.Hash} — “{commit.Message}”?\n\nThis does not remove the "
                     + "commit: it creates a new commit that undoes its changes, so the history "
                     + "everyone else has already pulled stays intact.",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 400,
            });

            ComboBox? mainlineBox = null;
            if (isMerge)
            {
                // git cannot guess which side of a merge "undoing it" should keep, so -m is
                // mandatory here; parent 1 is the branch the merge was made ON, which is what
                // reverting a merge almost always means.
                var parents = commit.ParentHashes
                    .Select((p, i) => $"{i + 1}: {(p.Length > 7 ? p[..7] : p)}")
                    .ToList();
                mainlineBox = new ComboBox
                {
                    Header = "Mainline parent (the side to keep)",
                    ItemsSource = parents,
                    SelectedIndex = 0,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                };
                panel.Children.Add(new TextBlock
                {
                    Text = "This is a merge commit. Git needs to know which parent is the mainline "
                         + "— the history to keep — before it can undo the other side.",
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.7,
                    FontSize = 12,
                    MaxWidth = 400,
                });
                panel.Children.Add(mainlineBox);
            }

            var noCommitBox = new CheckBox
            {
                Content = "Stage the reversal without committing (-n)",
                IsChecked = false,
            };
            panel.Children.Add(noCommitBox);

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Revert commit",
                Content = panel,
                PrimaryButtonText = "Revert",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;

            int mainline = isMerge ? (mainlineBox!.SelectedIndex + 1) : 0;
            bool noCommit = noCommitBox.IsChecked == true;

            await GitProgressDialog.RunAsync(
                XamlRoot, "Revert",
                "git revert --no-edit" + (mainline > 0 ? $" -m {mainline}" : "")
                    + (noCommit ? " -n" : "") + $" {commit.Hash}",
                (progress, token) => Vm.RevertAsync(commit.FullHash, mainline, noCommit, progress, token));
        }

        // ---- The state banner's actions (MERGE-002, REBASE-002) --------------------------------

        private async void ResolveConflicts_Click(object sender, RoutedEventArgs e)
            => await Vm.EnterWorkingCopyAsync();

        private async void ContinueOperation_Click(object sender, RoutedEventArgs e)
        {
            string command = Vm.State.GitCommand;
            if (command.Length == 0)
                return;
            if (Vm.HasConflicts)
            {
                await ShowMessageAsync("Continue",
                    $"{Vm.ConflictCount} file{(Vm.ConflictCount == 1 ? " is" : "s are")} still "
                    + "conflicted. Resolve them and mark them resolved first — git will refuse to "
                    + "continue while any conflict remains.");
                return;
            }
            await GitProgressDialog.RunAsync(
                XamlRoot, "Continue", $"git {command} --continue",
                (progress, token) => Vm.ContinueOperationAsync(progress, token));
        }

        private async void SkipOperation_Click(object sender, RoutedEventArgs e)
        {
            string command = Vm.State.GitCommand;
            if (command.Length == 0)
                return;

            var confirm = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Skip this commit",
                Content = new TextBlock
                {
                    Text = "The commit currently being applied will be dropped and the operation "
                         + "will move on to the next one. Its changes will not appear anywhere.",
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 400,
                },
                PrimaryButtonText = "Skip",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary)
                return;

            await GitProgressDialog.RunAsync(
                XamlRoot, "Skip", $"git {command} --skip",
                (progress, token) => Vm.SkipOperationAsync(progress, token));
        }

        private async void AbortOperation_Click(object sender, RoutedEventArgs e)
        {
            string command = Vm.State.GitCommand;
            if (command.Length == 0)
                return;

            // MERGE-002: aborting restores the pre-operation state, which also means throwing away
            // any conflict resolution done so far. Saying so is the whole confirmation.
            var confirm = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = $"Abort {command}",
                Content = new TextBlock
                {
                    Text = $"Git will put the branch and the working tree back the way they were "
                         + $"before the {command} started. Any conflicts you have already resolved "
                         + "will be lost.",
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 400,
                },
                PrimaryButtonText = "Abort",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary)
                return;

            await GitProgressDialog.RunAsync(
                XamlRoot, "Abort", $"git {command} --abort",
                (progress, token) => Vm.AbortOperationAsync(progress, token));
        }

        // ---- Conflict resolution on a working-copy row (MERGE-003/004) -------------------------

        private async void ResolveWithMergeTool_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.DataContext is not ChangedFile file)
                return;

            string tool = SettingsStore.Load().MergeTool;
            // The call does not return until the tool's window is closed, so it runs behind the
            // progress dialog like everything else — Cancel there kills the whole tool process tree.
            string label = "git mergetool --no-prompt"
                         + (tool.Length > 0 ? $" --tool={tool}" : "") + $" -- {file.Path}";
            string? error = await GitProgressDialog.RunAsync(
                XamlRoot, "Merge tool", label,
                (progress, token) => Vm.RunMergeToolAsync(file, tool, progress, token));

            if (error != null && tool.Length == 0)
            {
                await ShowMessageAsync("Merge tool",
                    "Git could not start a merge tool. Set one under Tools ▸ Options…, or "
                    + "configure merge.tool in this repository. Run “git mergetool --tool-help” "
                    + "to see the names git recognizes.");
            }
        }

        private async void MarkResolved_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.DataContext is not ChangedFile file)
                return;

            // Staging a file with conflict markers still in it is a real and common mistake, and
            // git will happily commit them. Checking is cheap; the file is already on disk.
            string? abs = AbsolutePathOf(file);
            if (abs != null && await HasConflictMarkersAsync(abs))
            {
                var warn = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = "Conflict markers found",
                    Content = new TextBlock
                    {
                        Text = $"{file.Path} still contains conflict markers (<<<<<<<, =======, "
                             + ">>>>>>>). Marking it resolved now would commit them.",
                        TextWrapping = TextWrapping.Wrap,
                        MaxWidth = 400,
                    },
                    PrimaryButtonText = "Mark Resolved Anyway",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Close,
                };
                if (await warn.ShowAsync() != ContentDialogResult.Primary)
                    return;
            }

            await Vm.MarkResolvedAsync(file);
        }

        /// <summary>True when the file still holds git's conflict markers. Reads at most the first
        /// megabyte: a marker further in than that is not a case worth blocking the UI for.</summary>
        private static async Task<bool> HasConflictMarkersAsync(string absPath)
        {
            try
            {
                return await Task.Run(() =>
                {
                    if (!File.Exists(absPath))
                        return false;
                    using var reader = new StreamReader(absPath);
                    var buffer = new char[1024 * 1024];
                    int read = reader.Read(buffer, 0, buffer.Length);
                    string head = new(buffer, 0, read);
                    return head.Contains("\n<<<<<<< ", StringComparison.Ordinal)
                        || head.StartsWith("<<<<<<< ", StringComparison.Ordinal);
                });
            }
            catch { return false; } // unreadable/binary: let git be the judge
        }

        // ---- Shared guards ---------------------------------------------------------------------

        /// <summary>False (after explaining why) when there is no repository, or when one operation
        /// is already half-finished — git refuses to start a second one, so offering it would only
        /// produce that refusal.</summary>
        private async Task<bool> RequireIdleRepositoryAsync(string title)
        {
            if (!Vm.HasRepository)
            {
                await ShowMessageAsync(title, "Open a repository first.");
                return false;
            }
            if (Vm.HasActiveOperation)
            {
                await ShowMessageAsync(title,
                    $"{Vm.StateTitle}. Finish or abort it first — the banner at the top of the "
                    + "window has the actions.");
                return false;
            }
            return true;
        }

        /// <summary>The pull flow's dirty-tree warning, reused: same risk, same two remedies, with
        /// the verb swapped so it reads as what is actually about to happen. Returns false when the
        /// user backs out.</summary>
        private async Task<bool> ConfirmDirtyTreeAsync(string title, string verb)
        {
            var (tracked, untracked) = await Vm.CountWorkingTreeAsync();
            if (tracked == 0 && untracked == 0)
                return true;

            var warn = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = title,
                Content = new TextBlock
                {
                    Text = DirtyTreeWarning(tracked, untracked, Vm.CurrentBranchName, verb),
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 400,
                },
                PrimaryButtonText = "Continue Anyway",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
            return await warn.ShowAsync() == ContentDialogResult.Primary;
        }

        // ---- Blame (Phase 8, BLAME-001) --------------------------------------------------------

        // Blame windows outlive the click that opened them, so they are tracked: a repository the
        // main window has closed must not leave a window behind still reading from it.
        private readonly List<BlameWindow> _blameWindows = new();

        /// <summary>From a commit's file list: blame the file as it was AT that commit.</summary>
        private void BlameFile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.DataContext is not ChangedFile file)
                return;
            OpenBlameWindow(file.Path, Vm.SelectedCommit?.FullHash ?? "");
        }

        /// <summary>From the working copy: blame HEAD, which is what the file on disk came from.</summary>
        private void BlameWorkingFile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.DataContext is not ChangedFile file)
                return;
            OpenBlameWindow(file.Path, "");
        }

        private void OpenBlameWindow(string path, string rev)
        {
            GitRepository? repo = Vm.CurrentRepository;
            if (repo == null || string.IsNullOrWhiteSpace(path))
                return;

            var window = new BlameWindow(repo, path, rev, RootLayout.ActualTheme);
            window.ShowCommitRequested += async (_, sha) => await ShowCommitFromBlameAsync(sha);
            window.Closed += (_, _) => _blameWindows.Remove(window);
            _blameWindows.Add(window);
            window.Activate();
        }

        /// <summary>Select a blamed line's commit in the main history. Falls back to a hash search
        /// because a blame can easily name a commit older than the loaded 2000.</summary>
        private async Task ShowCommitFromBlameAsync(string sha)
        {
            await Vm.SelectCommitByHashAsync(sha);
            if (Vm.SelectedCommit != null)
                CommitsList.ScrollIntoView(Vm.SelectedCommit);
        }

        /// <summary>Called when the workspace lets go of a repository, so no blame window is left
        /// reading from one the user has closed.</summary>
        public void CloseBlameWindows()
        {
            foreach (var window in _blameWindows.ToList())
                window.Close();
            _blameWindows.Clear();
        }

        // ---- Stash (Phase 8, STASH-001..004) ---------------------------------------------------

        private async void StashToolbar_Click(object sender, RoutedEventArgs e)
            => await ShowStashDialogAsync();

        /// <summary>STASH-001. Also the target of the Actions ▸ Stash menu item.</summary>
        public async Task ShowStashDialogAsync()
        {
            if (!await RequireIdleRepositoryAsync("Stash changes"))
                return;

            var (tracked, untracked) = await Vm.CountWorkingTreeAsync();
            if (tracked == 0 && untracked == 0)
            {
                // git would exit 0 having stashed nothing; saying so up front beats an error dialog
                // for something the user did not really ask for.
                await ShowMessageAsync("Stash changes",
                    "There is nothing to stash — the working tree is clean.");
                return;
            }

            var messageBox = new TextBox
            {
                Header = "Message (optional)",
                PlaceholderText = "Leave blank for git's own \"WIP on <branch>\" text",
                MinWidth = 420,
            };
            var untrackedBox = new CheckBox
            {
                Content = $"Include untracked files ({untracked})",
                IsChecked = false,
                IsEnabled = untracked > 0,
            };
            var keepIndexBox = new CheckBox
            {
                Content = "Keep staged changes in the working tree",
                IsChecked = false,
            };
            var hint = new TextBlock
            {
                Text = "Stashing parks every change and leaves a clean working tree. Everything "
                     + "stashed can be brought back with Apply or Pop from the STASHES section.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.7,
                FontSize = 12,
                MaxWidth = 420,
            };

            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(messageBox);
            panel.Children.Add(untrackedBox);
            panel.Children.Add(keepIndexBox);
            panel.Children.Add(hint);

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Stash changes",
                Content = panel,
                PrimaryButtonText = "Stash",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;

            await Vm.SaveStashAsync(messageBox.Text ?? "",
                                    untrackedBox.IsChecked == true,
                                    keepIndexBox.IsChecked == true);
        }

        private async void SidebarStashApply_Click(object sender, RoutedEventArgs e)
        {
            SidebarItemVM? item = SidebarItemOf(sender);
            if (item == null) return;
            await ApplyOrPopStashAsync(item.ShortName, pop: false);
        }

        private async void SidebarStashPop_Click(object sender, RoutedEventArgs e)
        {
            SidebarItemVM? item = SidebarItemOf(sender);
            if (item == null) return;
            await ApplyOrPopStashAsync(item.ShortName, pop: true);
        }

        /// <summary>
        /// STASH-002 / STASH-003. Restoring onto a dirty tree is where stash conflicts come from,
        /// so that is the case that gets a confirmation. A conflict is then reported rather than
        /// swallowed: the files are half-merged and the user has to know.
        /// </summary>
        private async Task ApplyOrPopStashAsync(string selector, bool pop)
        {
            string verb = pop ? "Pop" : "Apply";
            var (tracked, untracked) = await Vm.CountWorkingTreeAsync();
            if (tracked + untracked > 0 &&
                !await ConfirmAsync($"{verb} stash",
                    $"The working tree already has {tracked + untracked} uncommitted change"
                    + $"{(tracked + untracked == 1 ? "" : "s")}. Restoring {selector} on top of "
                    + "them can conflict, leaving files with conflict markers to resolve."
                    + (pop ? "\n\nOn conflict git keeps the stash entry, so nothing is lost." : ""),
                    verb))
            {
                return;
            }

            string? error = pop
                ? await Vm.PopStashAsync(selector, reportError: false)
                : await Vm.ApplyStashAsync(selector, reportError: false);
            if (error == null)
                return;

            if (GitErrorHints.IsConflict(error))
            {
                await ShowMessageAsync($"{verb} stash — conflicts",
                    GitErrorHints.Decorate(error)
                    + (pop ? "\n\nThe stash entry was kept, so you can drop it once the conflicts "
                           + "are resolved." : ""));
            }
            else
            {
                await ShowMessageAsync($"{verb} stash failed", GitErrorHints.Decorate(error));
            }
        }

        /// <summary>STASH-004.</summary>
        private async void SidebarStashDrop_Click(object sender, RoutedEventArgs e)
        {
            SidebarItemVM? item = SidebarItemOf(sender);
            if (item == null) return;

            // The row's label carries an index prefix so identical "WIP on main" messages stay
            // distinguishable in the sidebar; the dialog already names the selector, so it reads
            // the entry's own message instead of repeating that.
            StashEntry? entry = Vm.Stashes.FirstOrDefault(s => s.Selector == item.ShortName);
            string message = entry?.Message ?? item.Text;

            if (!await ConfirmAsync("Drop stash",
                    $"Delete {item.ShortName} — “{message}”?\n\nThe changes it holds are not "
                    + "committed anywhere, so this cannot be undone.",
                    "Drop"))
            {
                return;
            }
            await Vm.DropStashAsync(item.ShortName);
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
