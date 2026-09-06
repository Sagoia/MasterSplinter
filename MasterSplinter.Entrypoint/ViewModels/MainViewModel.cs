using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using MasterSplinter.Entrypoint.Git;
using CommunityToolkit.Mvvm.ComponentModel;
using MasterSplinter.Entrypoint.Infrastructure;
using MasterSplinter.Entrypoint.Models;

namespace MasterSplinter.Entrypoint.ViewModels
{
    public sealed partial class MainViewModel : ObservableObject, IGitOperationHost
    {
        // Phase 1 caps how much history we read at once so very large repos stay responsive.
        private const int MaxCommits = 2000;

        private GitRepository? _repo;
        private List<CommitRow> _allCommits = new();

        /// <summary>Lookups over <see cref="_allCommits"/>, rebuilt with it. Assign through
        /// <see cref="SetLoadedCommits"/> so the two can never disagree.</summary>
        private CommitIndex _commitIndex = CommitIndex.Empty;

        /// <summary>Bounds retained diffs — they are loaded lazily and were never released, so
        /// browsing grew memory monotonically. See <see cref="DiffCache"/>.</summary>
        private readonly DiffCache _diffCache = new();
        private RepositoryWatcher? _watcher;
        private readonly Microsoft.UI.Dispatching.DispatcherQueue? _dispatcherQueue;

        /// <summary>The filtered commit list bound to the history table. Bulk-capable because a
        /// filter keystroke replaces up to <see cref="MaxCommits"/> rows at once.</summary>
        public BulkObservableCollection<CommitRow> Commits { get; } = new();

        /// <summary>
        /// The commit-graph display list for whatever <see cref="Commits"/> is currently showing,
        /// as the native renderer consumes it. Empty for search results, which deliberately have
        /// no graph.
        /// <para>
        /// Raised as a property change so the view can hand it to the renderer without having to
        /// know when a load finished.
        /// </para>
        /// </summary>
        public byte[] GraphDisplayList { get; private set; } = Array.Empty<byte>();

        /// <summary>Publishes the graph for the rows just loaded; empty clears it.</summary>
        private void SetGraphDisplayList(byte[] graph)
        {
            GraphDisplayList = graph;
            OnPropertyChanged(nameof(GraphDisplayList));
        }
        public BulkObservableCollection<SidebarItemVM> Sidebar { get; } = new();
        public ObservableCollection<RecentRepository> Recent { get; } = new();

        /// <summary>Changed files shown in the bottom panel — the selected commit's, or a compare's.</summary>
        public ObservableCollection<ChangedFile> PanelFiles { get; } = new();

        /// <summary>The stash, newest first (STASH-001). Backs the sidebar's STASHES section.</summary>
        public ObservableCollection<StashEntry> Stashes { get; } = new();

        /// <summary>HEAD's (or another ref's) movement history, shown in place of the commit list
        /// while <see cref="IsReflogMode"/> is on (REFLOG-001).</summary>
        public ObservableCollection<ReflogEntry> ReflogEntries { get; } = new();

        public string[] BranchFilterOptions { get; } =
            { "All Branches", "Current Branch" };
        public string[] OrderOptions { get; } =
            { "Date Order", "Topological Order", "Reverse Date Order", "Author Date" };

        public MainViewModel()
        {
            // The VM is created by the workspace control on the UI thread; the watcher needs this
            // queue to debounce and call back on that thread (STATUS-004).
            _dispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            // Off the UI thread: this reads a JSON file, and the constructor runs during control
            // construction on the startup path. The await resumes here, so Recent is only ever
            // touched on the UI thread.
            _ = LoadRecentAsync();
        }

        /// <summary>The one place the loaded log is replaced, so its index is never stale.</summary>
        private void SetLoadedCommits(IEnumerable<CommitRow> commits)
        {
            _allCommits = commits.ToList();
            _commitIndex = new CommitIndex(_allCommits);
            // The rows these diffs hung off are gone, so the cache's bookkeeping would be tracking
            // objects nothing else references.
            _diffCache.Clear();
        }

        private async Task LoadRecentAsync()
        {
            try
            {
                List<RecentRepository> recent = await Task.Run(AppStores.Recent.Load);
                // LoadRepositoryAsync rewrites Recent when a repo is opened. If the user got there
                // first, that list is newer than this one — discard ours rather than clobbering it.
                if (Recent.Count > 0)
                    return;
                foreach (RecentRepository r in recent)
                    Recent.Add(r);
            }
            catch { /* the recent list is a convenience; a missing or corrupt file is not an error */ }
        }

        // ---- Repository state ------------------------------------------------------------------

        // The dependent properties are declared here rather than re-raised by hand in the setter:
        // the whole point of the command gates is that they can never go stale, and a list the
        // compiler maintains cannot drift from the properties it depends on.
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasRepository))]
        [NotifyPropertyChangedFor(nameof(CanFetch), nameof(CanPull), nameof(CanPush))]
        [NotifyPropertyChangedFor(nameof(CanStartOperation), nameof(CanResolveOperation))]
        [NotifyPropertyChangedFor(nameof(CanSkipOperation), nameof(CanStash), nameof(CanSearch))]
        private RepositoryInfo? _repository;

        public bool HasRepository => Repository != null;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanCommit))]
        [NotifyPropertyChangedFor(nameof(CanFetch), nameof(CanPull), nameof(CanPush))]
        [NotifyPropertyChangedFor(nameof(CanStartOperation), nameof(CanResolveOperation))]
        [NotifyPropertyChangedFor(nameof(CanSkipOperation), nameof(CanStash), nameof(CanSearch))]
        private bool _isLoading;

        private string? _errorMessage;
        public string? ErrorMessage
        {
            get => _errorMessage;
            private set
            {
                if (SetProperty(ref _errorMessage, value))
                    OnPropertyChanged(nameof(HasError));
            }
        }

        public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
        public void DismissError() => ErrorMessage = null;

        // ---- Selection -------------------------------------------------------------------------

        private CommitRow? _selectedCommit;
        public CommitRow? SelectedCommit
        {
            get => _selectedCommit;
            set
            {
                if (SetProperty(ref _selectedCommit, value))
                {
                    if (IsCompareMode) ClearCompareState(); // picking a commit leaves compare mode
                    if (IsWorkingCopyMode && !IsLoading && value != null)
                    {
                        // Picking a commit also leaves the working-copy view (but not when a
                        // refresh/reload is programmatically restoring the selection).
                        IsWorkingCopyMode = false;
                        StatusGroups.Clear();
                    }
                    // Clear the detail/diff while the new commit's files load — but not in
                    // working-copy mode (a background refresh must keep the status selection,
                    // which LoadStatusAsync then restores by area + path).
                    if (!IsWorkingCopyMode)
                        SelectedFile = null;
                    if (value != null)
                    {
                        _ = LoadFilesForAsync(value);
                    }
                    else
                    {
                        PanelFiles.Clear();
                        ChangedSummary = "";
                        UpdateDiffViewState();
                    }
                }
            }
        }

        private ChangedFile? _selectedFile;
        public ChangedFile? SelectedFile
        {
            get => _selectedFile;
            set
            {
                if (SetProperty(ref _selectedFile, value))
                {
                    UpdateDiffViewState();
                    if (value != null)
                        _ = LoadDiffForAsync(value);
                }
            }
        }


        // ---- Shared git-operation plumbing ------------------------------------------------
        // Cross-cutting, so it lives here rather than in any one area file: every mutation in
        // MainViewModel.Refs / .WorkingCopy / .Remotes / .Sequencer routes through it.

        /// <summary>The watcher-suppression window armed by <see cref="RefreshScope.StatusOnly"/>.</summary>
        private const int SuppressRepoRefreshMs = 2000;

        // The refresh policy itself lives in MasterSplinter.Core (GitOperationRunner) so it can be
        // unit-tested without a dispatcher or a real repository; this VM is just its host.
        private GitOperationRunner? _operations;
        private GitOperationRunner Operations => _operations ??= new GitOperationRunner(this);

        GitRepository? IGitOperationHost.Repository => _repo;
        void IGitOperationHost.ReportError(string message) => ErrorMessage = message;
        void IGitOperationHost.ArmWatcherSuppression()
            => _suppressRepoRefreshUntil = Environment.TickCount64 + SuppressRepoRefreshMs;
        void IGitOperationHost.SetBusy(bool busy) => IsGitBusy = busy;
        Task IGitOperationHost.RefreshAllAsync() => RefreshAsync();
        Task IGitOperationHost.RefreshStatusAsync() => LoadStatusAsync();

        private Task<string?> RunGitOperationAsync(Func<GitRepository, string?> operation,
                                                   RefreshScope scope,
                                                   bool reportError = true,
                                                   bool raiseBusy = false)
            => Operations.RunAsync(operation, scope, reportError, raiseBusy);

        /// <summary>Shared plumbing for fetch/pull/push. Unlike <see cref="RunRefMutationAsync"/>
        /// this refreshes even when the command FAILED: a fetch can update some refs before
        /// erroring on another, and a partially-applied push still moved the remote-tracking ref.
        /// Errors are returned rather than pushed to the InfoBar — the progress dialog is already
        /// showing the output, and duplicating a multi-paragraph hint there would be noise.</summary>
        private Task<string?> RunProgressOperationAsync(Func<GitRepository, string?> operation)
            => RunGitOperationAsync(operation, RefreshScope.FullAlways,
                                    reportError: false, raiseBusy: true);

        /// <summary>
        /// The one place still raising these by hand. Every other source declares its dependents
        /// with [NotifyPropertyChangedFor]; <see cref="State"/> cannot, because it is a plain
        /// property (its setter does more than store a value) rather than a generated one.
        /// </summary>
        private void RaiseCommandState()
        {
            OnPropertyChanged(nameof(CanFetch));
            OnPropertyChanged(nameof(CanPull));
            OnPropertyChanged(nameof(CanPush));
            OnPropertyChanged(nameof(CanStartOperation));
            OnPropertyChanged(nameof(CanResolveOperation));
            OnPropertyChanged(nameof(CanSkipOperation));
            OnPropertyChanged(nameof(CanStash));
            OnPropertyChanged(nameof(CanSearch));
        }
    }
}
