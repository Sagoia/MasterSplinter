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
    // Stash commands.
    public sealed partial class RepositoryWorkspace
    {
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

            var d = new DialogBuilder(XamlRoot, "Stash changes", "Stash");
            Field<string> messageField = d.TextBox(
                "Message (optional)",
                placeholder: "Leave blank for git's own \"WIP on <branch>\" text");
            // Shown even at zero, greyed: the count itself tells the user there is nothing to include.
            Field<bool> untrackedField = d.Check($"Include untracked files ({untracked})",
                                                 enabled: untracked > 0);
            Field<bool> keepIndexField = d.Check("Keep staged changes in the working tree");
            d.Note("Stashing parks every change and leaves a clean working tree. Everything "
                   + "stashed can be brought back with Apply or Pop from the STASHES section.");

            if (!await d.ShowAsync())
                return;

            await Vm.SaveStashAsync(messageField.Value, untrackedField.Value, keepIndexField.Value);
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
            if (await ConfirmAsync(Confirmations.DeleteTag(name)))
                await Vm.DeleteTagAsync(name);
        }
    }
}
