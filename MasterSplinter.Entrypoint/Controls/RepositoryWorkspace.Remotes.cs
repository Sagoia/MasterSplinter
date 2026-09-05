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
    // Remote management and the three network commands.
    public sealed partial class RepositoryWorkspace
    {
        // ---- Remotes (Phase 6, REMOTE-001..009) ------------------------------------------------

        /// <summary>REMOTE-002. Picks a remote (or all of them), then runs the fetch behind the
        /// progress dialog.</summary>
        public async Task ShowFetchDialogAsync()
        {
            IReadOnlyList<RemoteInfo> remotes = await RequireRemotesAsync("Fetch");
            if (remotes.Count == 0)
                return;

            var d = new DialogBuilder(XamlRoot, "Fetch", "Fetch");
            Field<string> remoteField = d.Combo("Remote", remotes.Select(r => r.Name).ToList());
            // Prune and tags default OFF, matching git's own defaults — pruning silently deleting
            // local remote-tracking refs should be a decision, not a surprise.
            Field<bool> allField = d.Check("Fetch from all remotes");
            Field<bool> pruneField = d.Check("Prune deleted remote branches");
            Field<bool> tagsField = d.Check("Fetch all tags");
            d.DisableWhile(remoteField, allField);   // --all and a remote name are exclusive

            if (!await d.ShowAsync())
                return;

            bool all = allField.Value;
            string remote = remoteField.Value;
            bool prune = pruneField.Value;
            bool tags = tagsField.Value;

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
                if (!await ConfirmAsync("Pull",
                        WorkingTreeWarning.Describe(tracked, untracked, Vm.CurrentBranchName),
                        "Pull Anyway"))
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
            var d = new DialogBuilder(XamlRoot,
                                      publishing ? "Publish branch" : "Push",
                                      publishing ? "Publish" : "Push");
            Field<string> remoteField = d.Combo("Remote", names, preselected);
            d.Note(publishing
                ? $"Publishing {branch} — it has no upstream yet."
                : $"Pushing {branch} to {Vm.UpstreamName}.");
            // REMOTE-006: tracking is the point of publishing, so it is pre-checked exactly then.
            Field<bool> upstreamField = d.Check("Set upstream (track this remote branch)",
                                                isChecked: publishing);
            Field<bool> tagsField = d.Check("Push tags");

            if (!await d.ShowAsync())
                return;

            string remote = remoteField.Value;
            bool setUpstream = upstreamField.Value;
            bool pushTags = tagsField.Value;

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
            var d = new DialogBuilder(XamlRoot, "Edit remote URL", "Save");
            Field<string> urlField = d.TextBox($"Fetch URL for {remote.Name}", initial: remote.FetchUrl);
            d.ValidateOnSubmit(urlField, u => RemoteUrl.Validate(u))
             .FocusOnOpen(urlField, selectAll: true);

            // Offered only when a distinct push URL exists; otherwise push already follows fetch and
            // the question is meaningless.
            Field<bool>? alsoPushField = remote.HasSeparatePushUrl
                ? d.Check("Also update the push URL", isChecked: true)
                : null;

            if (!await d.ShowAsync())
                return false;

            string url = urlField.Value.Trim();
            string? failure = await Vm.SetRemoteUrlAsync(remote.Name, url, pushUrl: false);
            if (failure == null && alsoPushField?.Value == true)
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
    }
}
