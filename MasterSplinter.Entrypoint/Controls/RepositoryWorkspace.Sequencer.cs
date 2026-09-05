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
    // Merge, rebase, cherry-pick, revert, the state banner and conflict resolution.
    public sealed partial class RepositoryWorkspace
    {
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
            var d = new DialogBuilder(XamlRoot, "Merge", "Merge");
            Field<string> sourceField = d.Combo($"Merge into {Vm.CurrentBranchName}", options, preselected);
            Field<bool> noFfField = d.Check("Always create a merge commit (--no-ff)");
            Field<bool> noCommitField = d.Check("Do not commit automatically (--no-commit)");

            if (!await d.ShowAsync())
                return;

            string source = sourceField.Value;
            if (source.Length == 0)
                return;
            bool noFf = noFfField.Value;
            bool noCommit = noCommitField.Value;

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
            var d = new DialogBuilder(XamlRoot, "Rebase", "Rebase");
            Field<string> upstreamField = d.Combo($"Rebase {branch} onto", options, preselected);
            d.Paragraph($"Rebasing replaces every commit on {branch} that is not already on the "
                        + "chosen branch with a new commit that has a different SHA. Anyone who has "
                        + $"already pulled {branch} will be left on the old commits — do not rebase "
                        + "a branch you have shared.");
            // Cancel is the default here, unlike merge: this one rewrites history.
            d.DefaultToCancel();

            if (!await d.ShowAsync())
                return;

            string upstream = upstreamField.Value;
            if (upstream.Length == 0)
                return;

            // git refuses a rebase with uncommitted changes outright (no --autostash here by
            // choice), so saying so first is cheaper than showing the user a failure.
            var (tracked, _) = await Vm.CountWorkingTreeAsync();
            if (tracked > 0)
            {
                await ShowMessageAsync("Rebase",
                    $"You have {WorkingTreeWarning.Count(tracked, "uncommitted change")}. "
                    + "Git will refuse to rebase with a dirty working tree — commit or "
                    + "stash first.");
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

            var d = new DialogBuilder(
                XamlRoot,
                ordered.Count == 1 ? "Cherry-pick commit" : $"Cherry-pick {ordered.Count} commits",
                "Cherry-pick");
            d.Paragraph(ordered.Count == 1
                ? "This commit will be applied on top of the current branch:"
                : $"These {ordered.Count} commits will be applied on top of the current branch, "
                  + "in this order:");
            // The ordered list itself has no builder equivalent — it is bespoke content, which is
            // what Add() is for.
            d.Add(new ScrollViewer { Content = list, MaxHeight = 220 });
            Field<bool> noCommitField = d.Check("Stage the changes without committing (-n)");

            if (!await d.ShowAsync())
                return;

            bool noCommit = noCommitField.Value;
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

            var d = new DialogBuilder(XamlRoot, "Revert commit", "Revert");
            d.Paragraph($"Revert {commit.Hash} — “{commit.Message}”?\n\nThis does not remove "
                        + "the commit: it creates a new commit that undoes its changes, so the "
                        + "history everyone else has already pulled stays intact.");

            Field<int>? mainlineField = null;
            if (isMerge)
            {
                // git cannot guess which side of a merge "undoing it" should keep, so -m is
                // mandatory here; parent 1 is the branch the merge was made ON, which is what
                // reverting a merge almost always means.
                d.Note("This is a merge commit. Git needs to know which parent is the mainline "
                       + "— the history to keep — before it can undo the other side.");
                mainlineField = d.ComboIndex(
                    "Mainline parent (the side to keep)",
                    commit.ParentHashes.Select((p, i) => $"{i + 1}: {(p.Length > 7 ? p[..7] : p)}").ToList());
            }

            Field<bool> noCommitField = d.Check("Stage the reversal without committing (-n)");
            d.DefaultToCancel();

            if (!await d.ShowAsync())
                return;

            // git's -m is 1-based; 0 means "omit it", which is required for a non-merge commit.
            int mainline = isMerge ? mainlineField!.Value + 1 : 0;
            bool noCommit = noCommitField.Value;

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

            if (!await ConfirmAsync(Confirmations.SkipCommit()))
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
            if (!await ConfirmAsync(Confirmations.AbortOperation(command)))
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

            string tool = (await Task.Run(AppStores.Settings.Load)).MergeTool;
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
                if (!await ConfirmAsync(Confirmations.ConflictMarkers(file.Path)))
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
    }
}
