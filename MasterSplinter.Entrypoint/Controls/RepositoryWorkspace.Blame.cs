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
    // Blame windows, which outlive the click that opened them.
    public sealed partial class RepositoryWorkspace
    {
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
    }
}
