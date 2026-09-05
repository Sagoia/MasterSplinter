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
    // Working-copy rows, staging, the commit editor and viewing a file at a commit.
    public sealed partial class RepositoryWorkspace
    {
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
            // Off the UI thread: a small JSON read, but this runs on a user gesture and the
            // launch itself follows immediately.
            AppSettings settings = await Task.Run(AppStores.Settings.Load);
            string? error = EditorLauncher.OpenInEditor(settings.EditorCommand, abs);
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
            if (await ConfirmAsync(Confirmations.DiscardFile(file.Path, untracked)))
                await Vm.DiscardFileAsync(file);
        }

        private async void Commit_Click(object sender, RoutedEventArgs e)
        {
            // COMMIT-007: amending rewrites history, so confirm and name the commit being replaced.
            if (Vm.IsAmend)
            {
                var head = await Vm.GetHeadMessageAsync();
                if (!await ConfirmAsync(Confirmations.AmendCommit(head?.Subject)))
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
    }
}
