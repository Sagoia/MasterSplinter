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
    // Branch and tag commands.
    public sealed partial class RepositoryWorkspace
    {
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
    }
}
