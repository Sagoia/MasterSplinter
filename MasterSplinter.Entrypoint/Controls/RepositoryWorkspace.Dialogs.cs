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
    // Toolbar entry points and the input dialogs they open.
    // Construction goes through DialogBuilder; confirmation wording lives in Core.
    public sealed partial class RepositoryWorkspace
    {
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
                if (!await ConfirmAsync(Confirmations.SwitchBranch(changes, name)))
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

            // The invoking ref/commit heads the list and is preselected; the rest are the same
            // HEAD + branches + remotes + tags the compare picker offers.
            var options = new List<string> { startLabel };
            foreach (string n in Vm.StartPointOptions)
                if (n != startLabel)
                    options.Add(n);

            var d = new DialogBuilder(XamlRoot, "Create branch", "Create");
            Field<string> nameField = d.TextBox("Branch name", placeholder: "feature/my-change");
            Field<string> startField = d.Combo("Starting point", options);
            Field<bool> checkoutField = d.Check("Check out new branch", isChecked: true);
            d.EnablePrimaryWhen(nameField).FocusOnOpen(nameField);

            if (!await d.ShowAsync())
                return;

            // The first entry's LABEL is cosmetic ("this commit"), so it maps back to the real
            // start point rather than being sent to git as written.
            string start = startField.Value == startLabel ? startPoint : startField.Value;
            if (start == "HEAD")
                start = "";
            await Vm.CreateBranchAsync(nameField.Value, start, checkoutField.Value);
        }

        /// <summary>BR-006.</summary>
        private async Task ShowRenameBranchDialogAsync(string oldName)
        {
            var d = new DialogBuilder(XamlRoot, "Rename branch", "Rename");
            Field<string> nameField = d.TextBox("Branch name", initial: oldName);
            // SelectAll so typing replaces the current name rather than appending to it.
            d.EnablePrimaryWhen(nameField).FocusOnOpen(nameField, selectAll: true);

            if (await d.ShowAsync()
                && nameField.Value.Trim() is { Length: > 0 } newName && newName != oldName)
            {
                await Vm.RenameBranchAsync(oldName, newName);
            }
        }

        /// <summary>BR-005: always try the safe delete first, and only offer the forced form once
        /// git has refused — quoting git's own reason.</summary>
        private async Task DeleteBranchWithConfirmAsync(string name)
        {
            if (!await ConfirmAsync(Confirmations.DeleteBranch(name)))
                return;

            string? error = await Vm.DeleteBranchAsync(name, force: false);
            if (error == null)
                return;

            if (await ConfirmAsync(Confirmations.DeleteBranchAnyway(error)))
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

            var d = new DialogBuilder(XamlRoot, "Create tag", "Create");
            Field<string> nameField = d.TextBox("Tag name", placeholder: "v1.0.0");
            d.Note($"at {atLabel}");
            Field<string> messageField = d.MultilineTextBox(
                "Message (optional) — leave empty for a lightweight tag");
            // Only the NAME gates the button; the message is genuinely optional.
            d.EnablePrimaryWhen(nameField).FocusOnOpen(nameField);

            if (await d.ShowAsync())
                await Vm.CreateTagAsync(nameField.Value, commitish, messageField.Value);
        }
    }
}
