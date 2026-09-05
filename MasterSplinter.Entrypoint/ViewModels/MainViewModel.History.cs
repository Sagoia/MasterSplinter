using System;
using System.Linq;
using System.Threading.Tasks;
using MasterSplinter.Entrypoint.Git;
using MasterSplinter.Entrypoint.Models;

namespace MasterSplinter.Entrypoint.ViewModels
{
    // What the commit list shows: the in-memory filter, git-backed search, and reflog mode.
    public sealed partial class MainViewModel
    {
        // ---- Reflog (Phase 8, REFLOG-001) ------------------------------------------------------

        private const int MaxReflogEntries = 500;

        private bool _isReflogMode;
        /// <summary>True while the history list shows the reflog instead of the commit log. The
        /// detail pane and diff viewer stay live: that is the whole point of a recovery tool.</summary>
        public bool IsReflogMode
        {
            get => _isReflogMode;
            private set => SetProperty(ref _isReflogMode, value);
        }

        private string _reflogRef = "HEAD";
        /// <summary>Which ref's reflog is shown. Only HEAD is offered today; the field exists
        /// because the same list renders any ref's history.</summary>
        public string ReflogRef
        {
            get => _reflogRef;
            private set => SetProperty(ref _reflogRef, value);
        }

        /// <summary>Switch the history list to the reflog and load it (REFLOG-001).</summary>
        public async Task EnterReflogAsync(string refName = "HEAD")
        {
            if (_repo == null) return;
            if (IsCompareMode) ClearCompareState();
            if (IsWorkingCopyMode) ExitWorkingCopy();

            ReflogRef = string.IsNullOrWhiteSpace(refName) ? "HEAD" : refName;
            IsReflogMode = true;

            var reflog = Sidebar.FirstOrDefault(i => i.Kind == SidebarKind.Reflog);
            if (reflog != null)
            {
                foreach (var i in Sidebar) i.IsSelected = false;
                reflog.IsSelected = true;
            }
            await LoadReflogAsync();
        }

        private async Task LoadReflogAsync()
        {
            if (_repo == null) return;
            GitRepository repo = _repo;
            string refName = ReflogRef;
            try
            {
                var entries = await Task.Run(() => repo.Reflog(refName, MaxReflogEntries));
                ReflogEntries.Clear();
                foreach (var e in entries)
                    ReflogEntries.Add(e);
            }
            catch (Exception ex) { ErrorMessage = ex.Message; }
        }

        /// <summary>Return the history list to the commit log.</summary>
        public void ExitReflog()
        {
            if (!IsReflogMode) return;
            IsReflogMode = false;
            ReflogEntries.Clear();
            SelectedCommit = Commits.FirstOrDefault();

            foreach (var i in Sidebar) i.IsSelected = false;
            var current = Sidebar.FirstOrDefault(i => i.Kind == SidebarKind.Branch && i.IsCurrent);
            if (current != null) current.IsSelected = true;
        }

        /// <summary>
        /// Show a reflog entry's commit in the detail pane. Looked up by sha rather than found in
        /// <see cref="Commits"/>, because the commits worth recovering are exactly the ones no
        /// branch reaches any more — and those never appear in the loaded log.
        /// </summary>
        public Task SelectReflogEntryAsync(ReflogEntry entry) => SelectCommitByHashAsync(entry.Sha);

        /// <summary>
        /// Select the commit with this sha, loading it on demand when it is not in the list. Shared
        /// by the reflog and by "Show in History" from a blame window — both routinely name commits
        /// older than the loaded <see cref="MaxCommits"/>, or unreachable altogether.
        /// </summary>
        public async Task SelectCommitByHashAsync(string sha)
        {
            if (_repo == null || string.IsNullOrWhiteSpace(sha)) return;
            GitRepository repo = _repo;
            try
            {
                CommitRow? row = _commitIndex.ByHash(sha)
                                 ?? await Task.Run(() => repo.CommitByHash(sha));
                if (row != null)
                    SelectedCommit = row;
                else
                    ErrorMessage = $"Commit {sha} is not in this repository any more.";
            }
            catch (Exception ex) { ErrorMessage = ex.Message; }
        }

        /// <summary>The open repository, for surfaces that read from it directly (the blame window,
        /// which runs on its own and must not go through this view model's single-selection state).</summary>
        public GitRepository? CurrentRepository => _repo;

        // ---- Filtering / ordering --------------------------------------------------------------

        private string _searchText = "";
        public string SearchText
        {
            get => _searchText;
            set
            {
                if (!SetProperty(ref _searchText, value))
                    return;
                // While results are showing, typing is composing the NEXT query (Enter runs it) —
                // filtering the results by it here would narrow them a second time, and in author
                // or content mode by text that is not in the message at all.
                if (!IsSearchResultMode)
                    ScheduleFilter();
            }
        }

        /// <summary>Debounce interval for the in-memory commit filter. The box binds with
        /// UpdateSourceTrigger=PropertyChanged, so without this every keystroke re-filters up to
        /// MaxCommits rows AND resets SelectedCommit — and that reset re-runs the selected commit's
        /// git work (measured at ~257 ms of git per character typed; the filter itself is under
        /// 1 ms).</summary>
        private const int FilterDebounceMs = 180;

        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _filterTimer;

        private void ScheduleFilter()
        {
            if (_dispatcherQueue == null)
            {
                ApplyFilter(); // no queue (tests / design time): stay synchronous
                return;
            }

            if (_filterTimer == null)
            {
                _filterTimer = _dispatcherQueue.CreateTimer();
                _filterTimer.Interval = TimeSpan.FromMilliseconds(FilterDebounceMs);
                _filterTimer.IsRepeating = false;
                _filterTimer.Tick += (_, _) => ApplyFilter();
            }

            // Trailing edge: each keystroke restarts the window, so a burst of typing filters once.
            _filterTimer.Stop();
            _filterTimer.Start();
        }

        private int _selectedOrderIndex;
        public int SelectedOrderIndex
        {
            get => _selectedOrderIndex;
            set
            {
                if (!SetProperty(ref _selectedOrderIndex, value))
                    return;
                // Order is part of the query, so results are re-run rather than re-sorted.
                _ = IsSearchResultMode ? LoadSearchResultsAsync() : ReloadLogAsync();
            }
        }

        // ---- Git-backed search (Phase 8, SEARCH-001/002) ---------------------------------------
        //
        // Two tiers over one search box. Typing keeps the instant in-memory filter above (free, no
        // process spawn, but blind to anything past MaxCommits); Enter escalates to a `git log`
        // query over the FULL history, whose results replace the commit list until cleared.

        public string[] SearchModeOptions { get; } =
            { "Message", "Author", "File content", "Path touched", "Commit hash" };

        private int _selectedSearchModeIndex;
        public int SelectedSearchModeIndex
        {
            get => _selectedSearchModeIndex;
            set => SetProperty(ref _selectedSearchModeIndex, value);
        }

        private SearchMode SelectedSearchMode => SelectedSearchModeIndex switch
        {
            1 => SearchMode.Author,
            2 => SearchMode.Content,
            3 => SearchMode.Path,
            4 => SearchMode.Hash,
            _ => SearchMode.Message,
        };

        private bool _searchMatchCase;
        public bool SearchMatchCase
        {
            get => _searchMatchCase;
            set => SetProperty(ref _searchMatchCase, value);
        }

        private bool _searchUseRegex;
        public bool SearchUseRegex
        {
            get => _searchUseRegex;
            set => SetProperty(ref _searchUseRegex, value);
        }

        private bool _searchAllBranches = true;
        /// <summary>On by default, matching the commit list itself (which loads with --all).</summary>
        public bool SearchAllBranches
        {
            get => _searchAllBranches;
            set => SetProperty(ref _searchAllBranches, value);
        }

        private string _searchPathFilter = "";
        public string SearchPathFilter
        {
            get => _searchPathFilter;
            set => SetProperty(ref _searchPathFilter, value ?? "");
        }

        private bool _isSearchResultMode;
        /// <summary>True while the commit list holds search results rather than the log.</summary>
        public bool IsSearchResultMode
        {
            get => _isSearchResultMode;
            private set => SetProperty(ref _isSearchResultMode, value);
        }

        private string _searchBannerText = "";
        public string SearchBannerText
        {
            get => _searchBannerText;
            private set => SetProperty(ref _searchBannerText, value);
        }

        // What produced the current result set, so RefreshAsync can re-run it rather than silently
        // replacing the results with the ordinary log.
        private SearchMode _activeSearchMode;
        private string _activeSearchQuery = "";
        private string _activeSearchPath = "";

        /// <summary>
        /// Run the git-backed search (SEARCH-001, SEARCH-002) and show its results in the commit
        /// list. A blank query with a blank path filter clears the results instead.
        /// </summary>
        public async Task RunSearchAsync()
        {
            if (_repo == null) return;
            string query = (SearchText ?? "").Trim();
            string path = SearchPathFilter.Trim();
            if (query.Length == 0 && path.Length == 0)
            {
                await ClearSearchResultsAsync();
                return;
            }

            _activeSearchMode = SelectedSearchMode;
            _activeSearchQuery = query;
            _activeSearchPath = path;
            if (IsReflogMode) ExitReflog();

            await LoadSearchResultsAsync();
        }

        private async Task LoadSearchResultsAsync()
        {
            if (_repo == null) return;
            GitRepository repo = _repo;
            SearchMode mode = _activeSearchMode;
            string query = _activeSearchQuery;
            string path = _activeSearchPath;
            int order = SelectedOrderIndex;
            bool matchCase = SearchMatchCase, useRegex = SearchUseRegex, all = SearchAllBranches;

            ErrorMessage = null;
            IsLoading = true;
            try
            {
                var results = await Task.Run(() => repo.SearchLog(mode, query, path, order,
                                                                  MaxCommits, matchCase, useRegex, all));
                SetLoadedCommits(results);
                IsSearchResultMode = true;
                SearchBannerText = SearchBanner.Describe(mode, query, path, results.Count);

                // The in-memory filter would narrow the results by the same text a second time —
                // and in author/content/path mode that text is not in the message at all, so it
                // would empty a list that has just been filled.
                Commits.Reset(results);
                SelectedCommit = Commits.FirstOrDefault();
            }
            catch (Exception ex) { ErrorMessage = ex.Message; }
            finally { IsLoading = false; }
        }


        /// <summary>Drop the result set and return to the ordinary log.</summary>
        public async Task ClearSearchResultsAsync()
        {
            if (!IsSearchResultMode) return;
            IsSearchResultMode = false;
            SearchBannerText = "";
            _activeSearchQuery = "";
            _activeSearchPath = "";
            await ReloadLogAsync();
        }
    }
}
