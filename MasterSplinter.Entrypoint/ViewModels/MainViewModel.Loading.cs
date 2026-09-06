using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MasterSplinter.Entrypoint.Git;
using MasterSplinter.Entrypoint.Infrastructure;
using MasterSplinter.Entrypoint.Models;

namespace MasterSplinter.Entrypoint.ViewModels
{
    // Opening, refreshing and the lazy per-commit loads.
    // This is where the commit graph will plug in - see CommitGraph and docs/graph.md.
    public sealed partial class MainViewModel
    {
        // ---- Loading ---------------------------------------------------------------------------

        public async Task LoadRepositoryAsync(string path)
        {
            ErrorMessage = null;
            IsLoading = true;
            // A search or a reflog belongs to the repository that produced it; the tab strip loads
            // straight into here without closing the old one first.
            IsSearchResultMode = false;
            SearchBannerText = "";
            _activeSearchQuery = "";
            _activeSearchPath = "";
            IsReflogMode = false;
            ReflogEntries.Clear();
            try
            {
                string? error = null;
                GitRepository? repo = await Task.Run(() => GitRepository.Open(path, out error));
                if (repo == null)
                {
                    ErrorMessage = error ?? "Could not open the selected folder.";
                    return;
                }

                _repo = repo;
                Repository = repo.ToInfo();

                // Watch for external changes (STATUS-004); null when the folder can't be watched.
                _watcher?.Dispose();
                _watcher = _dispatcherQueue == null
                    ? null
                    : RepositoryWatcher.TryCreate(repo.RootPath, _dispatcherQueue, OnRepositoryChanged);

                // Everything the first paint needs, in one round rather than four sequential ones.
                // All the git calls are pure reads (for-each-ref, stash list, log, rev-parse), so
                // overlapping them is safe; the recents write is a small JSON file and unrelated.
                // WhenAll before reading any result, so one failure cannot leave the rest
                // unobserved. See RefreshAsync for the measured numbers.
                Task<List<RecentRepository>> recentTask = Task.Run(() => AppStores.Recent.Add(repo.ToInfo()));
                Task<GitRepository.RefList> refsTask = Task.Run(() => repo.ListRefs());
                Task<IReadOnlyList<StashEntry>> stashesTask = Task.Run(() => repo.ListStashes());
                Task<IReadOnlyList<CommitRow>> logTask = Task.Run(() => repo.Log(SelectedOrderIndex, MaxCommits));
                Task<Models.RepositoryState> stateTask = Task.Run(() => repo.State());

                await Task.WhenAll(recentTask, refsTask, stashesTask, logTask, stateTask);

                // Update recents (CORE-002).
                Recent.Clear();
                foreach (RecentRepository r in recentTask.Result) Recent.Add(r);

                // Sidebar + ref caches from real refs, plus the stash section beside them.
                ApplyRefs(refsTask.Result, stashesTask.Result);

                SetLoadedCommits(logTask.Result);
                ApplyFilter();   // also publishes the graph for the rows it just set
                SelectedCommit = Commits.FirstOrDefault();

                await LoadStateAsync(stateTask.Result); // a repository can be opened mid-merge (Phase 7)
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
            }
            finally
            {
                IsLoading = false;
            }
        }

        /// <summary>Reload branch, refs, log, and (when shown) working-tree status (STATUS-003).</summary>
        public async Task RefreshAsync()
        {
            if (_repo == null || IsLoading) return;
            GitRepository repo = _repo;
            string? prevSha = SelectedCommit?.FullHash;
            bool wasWorkingCopy = IsWorkingCopyMode;
            bool wasReflog = IsReflogMode;
            bool wasSearch = IsSearchResultMode;

            ErrorMessage = null;
            IsLoading = true;
            // Fetched with the rest of the batch, but APPLIED further down: the state banner's
            // command gates must settle after IsLoading clears, so only the git call moves.
            Models.RepositoryState? prefetchedState = null;
            try
            {
                // Re-open to pick up branch/HEAD changes made outside the app.
                string? error = null;
                GitRepository? reopened = await Task.Run(() => GitRepository.Open(repo.RootPath, out error));
                if (reopened != null)
                {
                    _repo = reopened;
                    repo = reopened;
                    Repository = reopened.ToInfo();
                }

                // One round of git rather than three sequential ones. All four are pure reads
                // (for-each-ref, stash list, log, rev-parse) — none writes .git/index, which is what
                // makes overlapping them safe. Measured on the 12k-commit TortoiseGit clone:
                // 406 ms sequential vs 250 ms in parallel.
                //
                // WhenAll before reading any result, so a failure in one does not leave the others
                // unobserved.
                Task<GitRepository.RefList> refsTask = Task.Run(() => repo.ListRefs());
                Task<IReadOnlyList<StashEntry>> stashesTask = Task.Run(() => repo.ListStashes());
                Task<Models.RepositoryState> stateTask = Task.Run(() => repo.State());
                Task<IReadOnlyList<CommitRow>> logTask = wasSearch
                    ? Task.FromResult<IReadOnlyList<CommitRow>>(Array.Empty<CommitRow>())
                    : Task.Run(() => repo.Log(SelectedOrderIndex, MaxCommits));

                await Task.WhenAll(refsTask, stashesTask, stateTask, logTask);
                prefetchedState = stateTask.Result;

                ApplyRefs(refsTask.Result, stashesTask.Result);

                if (!wasSearch)
                {
                    SetLoadedCommits(logTask.Result);
                    ApplyFilter();
                    // Rebuilt rows are new objects, so restore the selection by hash; the fresh row
                    // lazily reloads its files/diff, which is exactly what a refresh should do.
                    SelectedCommit = _commitIndex.ByHash(prevSha) ?? Commits.FirstOrDefault();
                }
            }
            catch (Exception ex) { ErrorMessage = ex.Message; }
            finally { IsLoading = false; }

            // Results are re-run rather than replaced by the plain log: a refresh triggered by an
            // unrelated commit must not silently throw away the search the user is looking at.
            if (wasSearch)
                await LoadSearchResultsAsync();

            // After IsLoading clears, so the banner's command gates settle on their final values.
            await LoadStateAsync(prefetchedState);

            if (wasReflog)
                await LoadReflogAsync();      // the whole point is that it moved
            if (wasWorkingCopy)
                await EnterWorkingCopyAsync(); // restore the working-copy view with fresh status
        }

        /// <summary>Debounced watcher callback (UI thread). repoDirty = .git state changed.</summary>
        private void OnRepositoryChanged(bool repoDirty)
        {
            if (_repo == null || IsLoading) return;
            if (repoDirty)
            {
                // Inside the suppression window the .git\index write was our own stage/unstage/
                // discard: the log did not change, so a status reload is enough (a full refresh
                // here would flicker the log after every stage). External changes — the window
                // is only armed by in-app mutations — still trigger the full refresh.
                if (Environment.TickCount64 < _suppressRepoRefreshUntil)
                {
                    if (IsWorkingCopyMode)
                        _ = LoadStatusAsync();
                    return;
                }
                _ = RefreshAsync();          // commit/checkout/stage happened outside the app
            }
            else if (IsWorkingCopyMode)
                _ = LoadStatusAsync();       // plain file edits only affect the status view
            // Otherwise nothing: entering the working-copy view always loads a fresh status.
        }

        /// <summary>Return to the empty/home state (no repository open).</summary>
        public void CloseRepository()
        {
            _watcher?.Dispose();
            _watcher = null;
            _repo = null;
            Repository = null;
            SetLoadedCommits(Array.Empty<CommitRow>());
            Commits.Clear();
            Sidebar.Clear();
            ClearCompareState();
            IsWorkingCopyMode = false;
            IsReflogMode = false;
            ReflogEntries.Clear();
            Stashes.Clear();
            OnPropertyChanged(nameof(HasStashes));
            IsSearchResultMode = false;
            SearchBannerText = "";
            _activeSearchQuery = "";
            _activeSearchPath = "";
            SearchPathFilter = "";
            StatusGroups.Clear();
            CommitSubject = "";
            CommitBody = "";
            IsAmend = false;
            _hasStaged = false;
            State = RepositoryState.None;
            ConflictCount = 0;
            _prefilledSubject = "";
            _prefilledBody = "";
            _markedCommit = null;
            OnPropertyChanged(nameof(HasMarkedCommit));
            PanelFiles.Clear();
            ChangedSummary = "";
            _refNames = new List<string>();
            OnPropertyChanged(nameof(CompareRefNames));
            CurrentBranchName = "";
            UpstreamName = "";
            UpstreamRemote = "";
            UpstreamBranch = "";
            HeaderTrackText = "";
            SelectedCommit = null;
            ErrorMessage = null;
            UpdateDiffViewState();
        }

        private async Task ReloadLogAsync()
        {
            if (_repo == null) return;
            GitRepository repo = _repo;
            int order = SelectedOrderIndex;

            IsLoading = true;
            try
            {
                var commits = await Task.Run(() => repo.Log(order, MaxCommits));
                SetLoadedCommits(commits);
                ApplyFilter();
                SelectedCommit = Commits.FirstOrDefault();
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
            }
            finally
            {
                IsLoading = false;
            }
        }

        private void ApplyFilter()
        {
            IEnumerable<CommitRow> source = _allCommits;
            string q = _searchText?.Trim() ?? "";
            if (q.Length > 0)
            {
                source = _allCommits.Where(c =>
                    c.Message.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    c.Author.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    c.AuthorEmail.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    c.Hash.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    c.FullHash.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    c.Body.Contains(q, StringComparison.OrdinalIgnoreCase));
            }

            // One Reset notification, not one per row: the unfiltered list is up to MaxCommits long
            // and this runs on the UI thread.
            Commits.Reset(source);

            // The graph is laid out over the LOADED rows, so it only describes what is on screen
            // while nothing is filtered out. A narrowed list gets no graph rather than one drawn
            // against rows that are no longer there.
            SetGraphDisplayList(Commits.Count == _allCommits.Count && _repo != null
                                    ? _repo.GraphDisplayList
                                    : Array.Empty<byte>());

            if (SelectedCommit == null || !Commits.Contains(SelectedCommit))
                SelectedCommit = Commits.FirstOrDefault();
        }

        private async Task LoadFilesForAsync(CommitRow commit)
        {
            if (_repo == null) return;
            GitRepository repo = _repo;

            if (!commit.FilesLoaded)
            {
                try
                {
                    var files = await Task.Run(() => repo.ChangedFiles(commit.FullHash));
                    if (!commit.FilesLoaded) // guard against a racing load of the same commit
                    {
                        commit.Files.Clear();
                        foreach (var f in files) commit.Files.Add(f);
                        commit.FilesLoaded = true;
                    }
                }
                catch (Exception ex) { ErrorMessage = ex.Message; }
            }

            if (!ReferenceEquals(commit, SelectedCommit) || IsCompareMode || IsWorkingCopyMode)
                return;

            // Mirror this commit's files into the shared panel and load the change summary (DIFF-001).
            PanelFiles.Clear();
            foreach (var f in commit.Files) PanelFiles.Add(f);

            try
            {
                DiffStat stat = await Task.Run(() => repo.CommitStat(commit.FullHash));
                if (ReferenceEquals(commit, SelectedCommit) && !IsCompareMode)
                    ChangedSummary = FormatStat(stat);
            }
            catch (Exception ex) { ErrorMessage = ex.Message; }

            // Auto-select the first changed file once, if this commit is still the selected one.
            if (ReferenceEquals(commit, SelectedCommit) && SelectedFile == null)
                SelectedFile = PanelFiles.FirstOrDefault();
            UpdateDiffViewState();
        }

        private async Task LoadDiffForAsync(ChangedFile file)
        {
            if (_repo == null) return;
            GitRepository repo = _repo;

            bool worktree = file.IsWorkingTree;
            bool range = !worktree && IsCompareMode && _compareBase != null && _compareTarget != null;
            CommitRow? commit = SelectedCommit;
            if (!worktree && !range && commit == null) return;
            if (file.DiffLoaded) { UpdateDiffViewState(); return; }

            string baseRef = range ? _compareBase! : commit?.FullHash ?? "";
            string targetRef = range ? _compareTarget! : commit?.FullHash ?? "";
            string path = file.Path;
            WorkTreeArea area = file.Area;
            WhitespaceMode ws = _whitespace;
            string langId = DiffLanguages.IdForPath(path);

            try
            {
                var result = await Task.Run(() =>
                {
                    var (lines, isBinary) = worktree
                        ? repo.WorkTreeDiff(path, area, ws)
                        : range
                            ? repo.RangeDiff(baseRef, targetRef, path, ws)
                            : repo.FileDiff(targetRef, path, ws);
                    foreach (DiffLine d in lines) d.LanguageId = langId;
                    // Only for the view that is actually showing — EnsureSideBySideRows fills these
                    // in later if the user switches. See its remarks for why this matters.
                    List<DiffRow> rows = isBinary || _diffViewMode != DiffViewMode.SideBySide
                        ? new List<DiffRow>()
                        : SideBySideBuilder.Build(lines, langId);
                    return (lines, rows, isBinary);
                });

                if (file.DiffLoaded) return;

                file.Diff.Clear();
                foreach (DiffLine d in result.lines) file.Diff.Add(d);
                file.Rows.Clear();
                foreach (DiffRow r in result.rows) file.Rows.Add(r);
                file.IsBinary = result.isBinary;
                file.DiffLoaded = true;
                _diffCache.Retain(file);

                if (ReferenceEquals(file, SelectedFile))
                {
                    UpdateDiffViewState();
                    if (result.isBinary)
                    {
                        if (worktree)
                            await LoadWorkTreeBinaryPreviewAsync(file);
                        else
                            await LoadBinaryPreviewAsync(file, baseRef, targetRef, range);
                    }
                }
            }
            catch (Exception ex) { ErrorMessage = ex.Message; }
        }

        /// <summary>Binary/image preview for a working-tree file: new side from disk, old side from HEAD.</summary>
        private async Task LoadWorkTreeBinaryPreviewAsync(ChangedFile file)
        {
            BinaryOldImage = null;
            BinaryNewImage = null;
            BinaryHasImages = false;
            BinaryInfoText = $"Binary file — {file.Path}";

            if (_repo == null) return;
            GitRepository repo = _repo;

            string ext = Path.GetExtension(file.Path).ToLowerInvariant();
            bool isImage = ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".ico";
            if (!isImage) return;

            try
            {
                string abs = Path.Combine(repo.RootPath, file.Path.Replace('/', Path.DirectorySeparatorChar));
                byte[] newBytes = file.Status == FileChangeStatus.Deleted || !File.Exists(abs)
                    ? Array.Empty<byte>()
                    : await Task.Run(() => File.ReadAllBytes(abs));
                string oldSidePath = file.OldPath.Length > 0 ? file.OldPath : file.Path;
                byte[] oldBytes = file.Status is FileChangeStatus.Added or FileChangeStatus.Untracked
                    ? Array.Empty<byte>()
                    : await Task.Run(() => repo.FileBytesAt("HEAD", oldSidePath));

                if (!ReferenceEquals(file, SelectedFile)) return;

                BinaryNewImage = await BytesToImageAsync(newBytes);
                BinaryOldImage = await BytesToImageAsync(oldBytes);
                BinaryHasImages = BinaryNewImage != null || BinaryOldImage != null;
            }
            catch (Exception ex) { ErrorMessage = ex.Message; }
        }

        /// <summary>Reads a file's full content as of a commit (LOG-007).</summary>
        public Task<string> ReadFileAtCommitAsync(string sha, string path)
        {
            GitRepository? repo = _repo;
            if (repo == null) return Task.FromResult(string.Empty);
            return Task.Run(() => repo.FileAt(sha, path));
        }
    }
}
