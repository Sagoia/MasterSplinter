using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MasterSplinter.Entrypoint.Git;
using MasterSplinter.Entrypoint.Models;

namespace MasterSplinter.Entrypoint.Controls
{
    /// <summary>
    /// Per-line authorship for one file (BLAME-001), in its own top-level window so several files
    /// can be blamed side by side and next to the main window rather than instead of it.
    /// </summary>
    /// <remarks>
    /// This is the app's only secondary window. Two consequences worth knowing:
    /// <list type="bullet">
    /// <item>The theme has to be handed in and applied to <c>RootGrid</c>. MainWindow toggles the
    /// theme by setting its own root's RequestedTheme, which does not reach another window.</item>
    /// <item>It keeps its own <see cref="GitRepository"/> rather than sharing the view model's, so
    /// it is unaffected by the main window switching repositories — the host closes it instead
    /// (see <c>RepositoryWorkspace.CloseBlameWindows</c>).</item>
    /// </list>
    /// </remarks>
    public sealed partial class BlameWindow : Window
    {
        private readonly GitRepository _repo;
        private readonly string _path;
        private string _rev;

        /// <summary>Raised when the user asks to see a blamed line's commit in the main history.</summary>
        public event EventHandler<string>? ShowCommitRequested;

        // Private on purpose: the XAML type generator emits a setter-based provider for every type
        // reachable from an x:Class type's PUBLIC members, and BlameLine is a positional record
        // whose accessors are init-only. Keeping the collection private sidesteps that entirely.
        private readonly ObservableCollection<BlameLine> _lines = new();

        /// <param name="rev">Empty means HEAD (the working-copy view's natural starting point).</param>
        public BlameWindow(GitRepository repo, string path, string rev, ElementTheme theme)
        {
            InitializeComponent();
            _repo = repo;
            _path = path;
            _rev = rev ?? "";

            RootGrid.RequestedTheme = theme;
            Title = $"Blame — {path}";
            PathText.Text = path;
            MovesBox.SelectedIndex = 0;
            LinesList.ItemsSource = _lines;

            // 1000x700 is enough for a typical source file's longest line plus the gutter without
            // horizontal scrolling, which is what makes blame readable at all.
            AppWindow.Resize(new Windows.Graphics.SizeInt32(1000, 700));

            Activated += OnFirstActivated;
        }

        private bool _loadedOnce;

        private async void OnFirstActivated(object sender, WindowActivatedEventArgs args)
        {
            if (_loadedOnce)
                return;
            _loadedOnce = true;
            Activated -= OnFirstActivated;
            await LoadAsync();
        }

        private BlameMoveDetection SelectedMoves => MovesBox.SelectedIndex switch
        {
            1 => BlameMoveDetection.WithinFile,
            2 => BlameMoveDetection.AcrossFiles,
            3 => BlameMoveDetection.Aggressive,
            _ => BlameMoveDetection.None,
        };

        private async Task LoadAsync()
        {
            GitRepository repo = _repo;
            string path = _path;
            string rev = _rev;
            bool ignoreWs = IgnoreWsBox.IsChecked == true;
            BlameMoveDetection moves = SelectedMoves;

            Busy.IsActive = true;
            StatusText.Text = "Reading blame…";
            RevText.Text = rev.Length == 0 ? "HEAD" : rev;
            SelectLine(null);
            try
            {
                string? error = null;
                IReadOnlyList<BlameLine> result = await Task.Run(() =>
                    repo.Blame(rev, path, ignoreWs, moves, out error));

                _lines.Clear();
                foreach (var line in result)
                    _lines.Add(line);

                StatusText.Text = error ?? $"{result.Count} line{(result.Count == 1 ? "" : "s")}";
            }
            catch (Exception ex)
            {
                _lines.Clear();
                StatusText.Text = ex.Message;
            }
            finally
            {
                Busy.IsActive = false;
            }
        }

        // Both option controls re-run the blame: they change git's answer, not just its presentation.
        private async void Options_Changed(object sender, RoutedEventArgs e)
        {
            if (!_loadedOnce)
                return; // the initial SelectedIndex assignment, before the first load
            await LoadAsync();
        }

        private async void Reload_Click(object sender, RoutedEventArgs e) => await LoadAsync();

        private void Lines_SelectionChanged(object sender, SelectionChangedEventArgs e)
            => SelectLine(e.AddedItems.Count > 0 ? e.AddedItems[0] as BlameLine : null);

        private BlameLine? _selected;

        private void SelectLine(BlameLine? line)
        {
            _selected = line;
            bool show = line != null && !line.IsUncommitted;
            DetailPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            DetailEmpty.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
            if (line != null && line.IsUncommitted)
                DetailEmpty.Text = "This line is not committed yet.";
            else if (line == null)
                DetailEmpty.Text = "Select a line to see the commit that wrote it.";
            if (!show || line == null)
                return;

            DetailSummary.Text = line.Summary;
            DetailSha.Text = line.Sha;
            DetailAuthor.Text = line.AuthorEmail.Length > 0
                ? $"{line.Author} <{line.AuthorEmail}>"
                : line.Author;
            DetailDate.Text = line.When == DateTimeOffset.MinValue
                ? ""
                : line.When.ToLocalTime().ToString("d MMM yyyy H:mm");

            // Only worth saying when -M/-C actually found the line somewhere else.
            bool moved = line.SourcePath.Length > 0 && line.SourcePath != _path;
            DetailOrigin.Visibility = moved ? Visibility.Visible : Visibility.Collapsed;
            DetailOrigin.Text = moved ? $"Line came from {line.SourcePath}:{line.OrigLine}" : "";
        }

        private void ShowInHistory_Click(object sender, RoutedEventArgs e)
        {
            if (_selected != null)
                ShowCommitRequested?.Invoke(this, _selected.Sha);
        }

        /// <summary>Walk back in time: re-blame the file as of the selected line's commit, which is
        /// how you find the change before the one you are looking at.</summary>
        private async void BlameAtCommit_Click(object sender, RoutedEventArgs e)
        {
            if (_selected == null || _selected.IsUncommitted)
                return;
            _rev = _selected.Sha;
            await LoadAsync();
        }
    }
}
