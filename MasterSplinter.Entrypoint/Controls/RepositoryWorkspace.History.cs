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
    // The commit list surface: search, reflog, compare, the home screen and row actions.
    public sealed partial class RepositoryWorkspace
    {
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

            var d = new DialogBuilder(XamlRoot, "Compare refs", "Compare");
            Field<string> sourceField = d.Combo("Source (A)", names);
            // Preselect a DIFFERENT ref for B, so the dialog does not open asking to compare
            // something with itself.
            Field<string> targetField = d.Combo("Target (B)", names, names.Count > 1 ? 1 : 0);

            if (await d.ShowAsync()
                && sourceField.Value.Length > 0 && targetField.Value.Length > 0)
            {
                await Vm.CompareRefsAsync(sourceField.Value, targetField.Value);
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
        private Task<bool> ConfirmAsync(string title, string message, string primaryText)
            => ConfirmAsync(new Confirmation(title, message, primaryText));

        /// <summary>
        /// Shows a yes/no confirmation. The wording and which button is default come from
        /// <see cref="Confirmations"/> in Core, so what we promise the user is pinned by tests;
        /// this method only renders it.
        /// </summary>
        private async Task<bool> ConfirmAsync(Confirmation c)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = c.Title,
                Content = new TextBlock
                {
                    Text = c.Message,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 400,
                },
                PrimaryButtonText = c.PrimaryText,
                CloseButtonText = "Cancel",
                DefaultButton = c.DefaultIsPrimary
                    ? ContentDialogButton.Primary
                    : ContentDialogButton.Close,
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
    }
}
