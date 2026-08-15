using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;
using MasterSplinter.Entrypoint.Git;
using MasterSplinter.Entrypoint.Infrastructure;
using MasterSplinter.Entrypoint.Models;

namespace MasterSplinter.Entrypoint.ViewModels
{
    public sealed class MainViewModel : ObservableObject
    {
        // Phase 1 caps how much history we read at once so very large repos stay responsive.
        private const int MaxCommits = 2000;

        private GitRepository? _repo;
        private List<CommitRow> _allCommits = new();
        private RepositoryWatcher? _watcher;
        private readonly Microsoft.UI.Dispatching.DispatcherQueue? _dispatcherQueue;

        public ObservableCollection<CommitRow> Commits { get; } = new();
        public ObservableCollection<SidebarItemVM> Sidebar { get; } = new();
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
            foreach (var r in RecentRepositoriesStore.Load())
                Recent.Add(r);
        }

        // ---- Repository state ------------------------------------------------------------------

        private RepositoryInfo? _repository;
        public RepositoryInfo? Repository
        {
            get => _repository;
            private set
            {
                if (!Set(ref _repository, value)) return;
                Raise(nameof(HasRepository));
                RaiseCommandState();
            }
        }

        public bool HasRepository => Repository != null;

        private bool _isLoading;
        public bool IsLoading
        {
            get => _isLoading;
            private set
            {
                if (!Set(ref _isLoading, value)) return;
                Raise(nameof(CanCommit));
                RaiseCommandState();
            }
        }

        private string? _errorMessage;
        public string? ErrorMessage
        {
            get => _errorMessage;
            private set
            {
                if (Set(ref _errorMessage, value))
                    Raise(nameof(HasError));
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
                if (Set(ref _selectedCommit, value))
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
                if (Set(ref _selectedFile, value))
                {
                    UpdateDiffViewState();
                    if (value != null)
                        _ = LoadDiffForAsync(value);
                }
            }
        }

        // ---- Diff view options (DIFF-002/003/004) ----------------------------------------------

        private DiffViewMode _diffViewMode = DiffViewMode.Unified;

        /// <summary>Toggles between unified and side-by-side diff layout (DIFF-002).</summary>
        public bool IsSideBySide
        {
            get => _diffViewMode == DiffViewMode.SideBySide;
            set
            {
                DiffViewMode mode = value ? DiffViewMode.SideBySide : DiffViewMode.Unified;
                if (_diffViewMode == mode) return;
                _diffViewMode = mode;
                Raise(nameof(IsSideBySide));
                UpdateDiffViewState();
            }
        }

        private bool _syntaxHighlightOn = true;
        public bool SyntaxHighlightOn
        {
            get => _syntaxHighlightOn;
            set
            {
                if (!Set(ref _syntaxHighlightOn, value)) return;
                SyntaxState.Enabled = value;       // read by the Syntax attached property (DIFF-003)
                ReloadCurrentDiff();               // re-realize the diff so colors refresh
            }
        }

        private WhitespaceMode _whitespace = WhitespaceMode.None;
        private int _whitespaceIndex;
        public int WhitespaceIndex
        {
            get => _whitespaceIndex;
            set
            {
                if (!Set(ref _whitespaceIndex, value)) return;
                _whitespace = value switch
                {
                    1 => WhitespaceMode.IgnoreChange,
                    2 => WhitespaceMode.IgnoreAll,
                    _ => WhitespaceMode.None,
                };
                ReloadCurrentDiff();               // re-fetch the diff with the new whitespace flag
            }
        }

        private bool _showUnified, _showSideBySide, _showBinary;
        public bool ShowUnified { get => _showUnified; private set => Set(ref _showUnified, value); }
        public bool ShowSideBySide { get => _showSideBySide; private set => Set(ref _showSideBySide, value); }
        public bool ShowBinary { get => _showBinary; private set => Set(ref _showBinary, value); }

        private string _changedSummary = "";
        public string ChangedSummary { get => _changedSummary; private set => Set(ref _changedSummary, value); }

        private void UpdateDiffViewState()
        {
            ChangedFile? f = SelectedFile;
            bool hasFile = f != null;
            bool binary = hasFile && f!.IsBinary;
            ShowBinary = binary;
            ShowUnified = hasFile && !binary && _diffViewMode == DiffViewMode.Unified;
            ShowSideBySide = hasFile && !binary && _diffViewMode == DiffViewMode.SideBySide;
        }

        private void ReloadCurrentDiff()
        {
            ChangedFile? f = SelectedFile;
            if (f == null) return;
            f.DiffLoaded = false;
            _ = LoadDiffForAsync(f);
        }

        // ---- Compare two commits / refs (DIFF-006/007) -----------------------------------------

        private CommitRow? _markedCommit;
        private string? _compareBase;
        private string? _compareTarget;
        private List<string> _refNames = new();

        private bool _isCompareMode;
        public bool IsCompareMode
        {
            get => _isCompareMode;
            private set
            {
                if (Set(ref _isCompareMode, value))
                {
                    Raise(nameof(IsSingleCommitMode));
                    Raise(nameof(ShowCommitPane));
                }
            }
        }
        public bool IsSingleCommitMode => !_isCompareMode;

        private string _compareTitle = "";
        public string CompareTitle { get => _compareTitle; private set => Set(ref _compareTitle, value); }

        /// <summary>BR-007: "↑N ahead · ↓M behind" for the compared refs; empty when identical.</summary>
        private string _compareAheadBehind = "";
        public string CompareAheadBehind
        {
            get => _compareAheadBehind;
            private set { if (Set(ref _compareAheadBehind, value)) Raise(nameof(HasCompareAheadBehind)); }
        }
        public bool HasCompareAheadBehind => _compareAheadBehind.Length > 0;

        public bool HasMarkedCommit => _markedCommit != null;

        /// <summary>Names of branches/tags/remotes + HEAD, for the compare-refs picker (DIFF-007).</summary>
        public IReadOnlyList<string> CompareRefNames => _refNames;

        public void MarkForComparison(CommitRow commit)
        {
            _markedCommit = commit;
            Raise(nameof(HasMarkedCommit));
        }

        public Task CompareWithMarkedAsync(CommitRow target)
        {
            if (_markedCommit == null) return Task.CompletedTask;
            return EnterCompareAsync(_markedCommit.FullHash, target.FullHash,
                $"{Short(_markedCommit.FullHash)} → {Short(target.FullHash)}");
        }

        public Task CompareRefsAsync(string a, string b) => EnterCompareAsync(a, b, $"{a} → {b}");

        private async Task EnterCompareAsync(string a, string b, string title)
        {
            if (_repo == null) return;
            GitRepository repo = _repo;

            IsCompareMode = true;
            _compareBase = a;
            _compareTarget = b;
            CompareTitle = title;
            SelectedFile = null;

            try
            {
                var files = await Task.Run(() => repo.ChangedFilesRange(a, b));
                DiffStat stat = await Task.Run(() => repo.RangeStat(a, b));
                // BR-007: divergence between the two refs, alongside the diff. Computed here so
                // comparing marked commits gets it too, not just the ref picker.
                var ab = await Task.Run(() => repo.AheadBehind(a, b));
                if (!IsCompareMode) return; // exited while loading
                CompareAheadBehind = ab is { Ahead: 0, Behind: 0 }
                    ? ""
                    : $"↑{ab.Ahead} ahead · ↓{ab.Behind} behind";
                PanelFiles.Clear();
                foreach (var f in files) PanelFiles.Add(f);
                ChangedSummary = FormatStat(stat);
                SelectedFile = PanelFiles.FirstOrDefault();
                UpdateDiffViewState();
            }
            catch (Exception ex) { ErrorMessage = ex.Message; }
        }

        public void ExitCompare()
        {
            if (!IsCompareMode) return;
            ClearCompareState();
            CommitRow? commit = SelectedCommit;
            SelectedFile = null;
            PanelFiles.Clear();
            ChangedSummary = "";
            if (commit != null)
                _ = LoadFilesForAsync(commit);
            UpdateDiffViewState();
        }

        private void ClearCompareState()
        {
            IsCompareMode = false;
            _compareBase = null;
            _compareTarget = null;
            CompareTitle = "";
            CompareAheadBehind = "";
        }

        // ---- Working copy / file status (STATUS-001/002/005) ------------------------------------

        /// <summary>Grouped status sections (staged / unstaged / untracked) for the working-copy list.</summary>
        public ObservableCollection<ChangedFileGroup> StatusGroups { get; } = new();

        private bool _isWorkingCopyMode;
        public bool IsWorkingCopyMode
        {
            get => _isWorkingCopyMode;
            private set
            {
                if (Set(ref _isWorkingCopyMode, value))
                    Raise(nameof(ShowCommitPane));
            }
        }

        /// <summary>The commit metadata pane shows only in plain single-commit history view.</summary>
        public bool ShowCommitPane => !IsCompareMode && !IsWorkingCopyMode;

        /// <summary>Switch the bottom-left panel to the working-copy status view and (re)load it.</summary>
        public async Task EnterWorkingCopyAsync()
        {
            if (_repo == null) return;
            if (IsCompareMode) ClearCompareState();
            if (IsReflogMode) ExitReflog();

            var wc = Sidebar.FirstOrDefault(i => i.Kind == SidebarKind.WorkingCopy);
            if (wc != null)
            {
                foreach (var i in Sidebar) i.IsSelected = false;
                wc.IsSelected = true;
            }

            if (!IsWorkingCopyMode)
            {
                IsWorkingCopyMode = true;
                SelectedFile = null;
                PanelFiles.Clear();
                ChangedSummary = "";
                UpdateDiffViewState();
            }
            await LoadStatusAsync();
        }

        /// <summary>Return the bottom-left panel to the selected commit's history file list.</summary>
        public void ExitWorkingCopy()
        {
            if (!IsWorkingCopyMode) return;
            IsWorkingCopyMode = false;
            StatusGroups.Clear();
            SelectedFile = null;
            PanelFiles.Clear();
            ChangedSummary = "";

            // Move the sidebar highlight back from "Working Copy" to the current branch.
            foreach (var i in Sidebar) i.IsSelected = false;
            var current = Sidebar.FirstOrDefault(i => i.Kind == SidebarKind.Branch && i.IsCurrent);
            if (current != null) current.IsSelected = true;

            CommitRow? commit = SelectedCommit;
            if (commit != null)
                _ = LoadFilesForAsync(commit);
            UpdateDiffViewState();
        }

        private async Task LoadStatusAsync()
        {
            if (_repo == null) return;
            GitRepository repo = _repo;

            // Every load rebuilds the ChangedFile objects, so remember selection by (Area, Path).
            WorkTreeArea? prevArea = SelectedFile?.IsWorkingTree == true ? SelectedFile.Area : null;
            string? prevPath = SelectedFile?.Path;

            try
            {
                var st = await Task.Run(() => repo.Status());
                if (!IsWorkingCopyMode) return; // left the view while loading

                StatusGroups.Clear();
                // Conflicts first: they are the only rows that block everything else, so they lead
                // the list rather than being buried under the staged section.
                AddStatusGroup("Conflicted files", st.Conflicted);
                AddStatusGroup("Staged files", st.Staged);
                AddStatusGroup("Unstaged files", st.Unstaged);
                AddStatusGroup("Untracked files", st.Untracked);

                int total = st.Staged.Count + st.Unstaged.Count + st.Untracked.Count + st.Conflicted.Count;
                string conflicts = st.Conflicted.Count > 0
                    ? $"{st.Conflicted.Count} conflicted  ·  "
                    : "";
                ChangedSummary = total == 0
                    ? "Working tree clean"
                    : $"{conflicts}{st.Staged.Count} staged  ·  {st.Unstaged.Count} unstaged  ·  {st.Untracked.Count} untracked";

                _hasStaged = st.Staged.Count > 0;
                ConflictCount = st.Conflicted.Count;
                Raise(nameof(CanCommit));

                ChangedFile? restore = StatusGroups.SelectMany(g => g)
                        .FirstOrDefault(f => prevArea != null && f.Area == prevArea && f.Path == prevPath)
                    ?? StatusGroups.SelectMany(g => g).FirstOrDefault();
                SelectedFile = restore;
                UpdateDiffViewState();
            }
            catch (Exception ex) { ErrorMessage = ex.Message; }
        }

        private void AddStatusGroup(string title, List<ChangedFile> files)
        {
            if (files.Count == 0) return;
            var group = new ChangedFileGroup { Title = $"{title} ({files.Count})" };
            foreach (var f in files) group.Add(f);
            StatusGroups.Add(group);
        }

        // ---- Staging & commit (COMMIT-001..007) --------------------------------------------------

        // In-app mutations write .git\index, which the watcher reports as repoDirty ~500ms later;
        // inside this window that event is downgraded to a status reload (see OnRepositoryChanged).
        private long _suppressRepoRefreshUntil;
        private bool _hasStaged;

        private string _commitSubject = "";
        public string CommitSubject
        {
            get => _commitSubject;
            set { if (Set(ref _commitSubject, value)) Raise(nameof(CanCommit)); }
        }

        private string _commitBody = "";
        public string CommitBody { get => _commitBody; set => Set(ref _commitBody, value); }

        private bool _isAmend;
        public bool IsAmend
        {
            get => _isAmend;
            set
            {
                if (!Set(ref _isAmend, value)) return;
                Raise(nameof(CanCommit));
                if (value)
                    _ = PrefillAmendMessageAsync();
            }
        }

        private bool _isCommitting;
        public bool IsCommitting
        {
            get => _isCommitting;
            private set { if (Set(ref _isCommitting, value)) Raise(nameof(CanCommit)); }
        }

        /// <summary>COMMIT-005: committing is blocked while the subject is empty or there is
        /// nothing to commit (no staged files, unless amending) — and, from Phase 7, while any file
        /// is still conflicted. Git would refuse that commit anyway ("Committing is not possible
        /// because you have unmerged files"); disabling the button alongside the state banner says
        /// so before the user writes a message they cannot use.</summary>
        public bool CanCommit => !IsCommitting && !IsLoading && !HasConflicts
                                 && !string.IsNullOrWhiteSpace(CommitSubject)
                                 && (_hasStaged || IsAmend);

        public Task StageFileAsync(ChangedFile file)
            => RunStatusMutationAsync(r => r.StagePaths(PathsOf(file)));

        public Task UnstageFileAsync(ChangedFile file)
            => RunStatusMutationAsync(r => r.UnstagePaths(PathsOf(file)));

        public Task StageAllAsync()
            => RunStatusMutationAsync(r => r.StageAll());

        /// <summary>Unstages every staged row (git has no single verb for this in our restricted
        /// unborn-branch-safe form, so all staged paths are passed in one batch).</summary>
        public Task UnstageAllAsync()
        {
            var paths = StatusGroups.SelectMany(g => g)
                .Where(f => f.Area == WorkTreeArea.Staged)
                .SelectMany(PathsOf)
                .Distinct()
                .ToList();
            return paths.Count == 0
                ? Task.CompletedTask
                : RunStatusMutationAsync(r => r.UnstagePaths(paths));
        }

        /// <summary>Double-click on a working-copy row: staged rows unstage, all others stage.</summary>
        public Task ToggleStageAsync(ChangedFile file)
            => file.Area == WorkTreeArea.Staged ? UnstageFileAsync(file) : StageFileAsync(file);

        /// <summary>COMMIT-004. Tracked: restore from the index. Untracked: delete from disk —
        /// the caller must have shown the deletion-worded confirmation first.</summary>
        public Task DiscardFileAsync(ChangedFile file)
        {
            if (file.Area == WorkTreeArea.Untracked)
            {
                return RunStatusMutationAsync(r =>
                {
                    try
                    {
                        File.Delete(Path.Combine(r.RootPath, file.Path));
                        return null;
                    }
                    catch (Exception ex) { return ex.Message; }
                });
            }
            return RunStatusMutationAsync(r => r.DiscardPaths(new[] { file.Path }));
        }

        /// <summary>COMMIT-006/007. Returns true when the commit was created; on failure the
        /// editor text is preserved and the git output surfaces in the error bar.</summary>
        public async Task<bool> CommitAsync()
        {
            if (_repo == null || !CanCommit) return false;
            GitRepository repo = _repo;
            bool amend = IsAmend;

            string subject = CommitSubject.TrimEnd();
            string message = string.IsNullOrWhiteSpace(CommitBody)
                ? subject
                : subject + "\n\n" + CommitBody;

            IsCommitting = true;
            try
            {
                string? error = await Task.Run(() => repo.Commit(message, amend));
                if (error != null)
                {
                    ErrorMessage = error;
                    return false;
                }
                CommitSubject = "";
                CommitBody = "";
                IsAmend = false;
                await RefreshAsync(); // the new commit appears in the log; watcher echo is absorbed by IsLoading
                return true;
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
                return false;
            }
            finally { IsCommitting = false; }
        }

        /// <summary>HEAD's subject/body (amend pre-fill + confirmation), or null with no commits.</summary>
        public async Task<(string Subject, string Body)?> GetHeadMessageAsync()
        {
            if (_repo == null) return null;
            GitRepository repo = _repo;
            try { return await Task.Run(() => repo.HeadMessage()); }
            catch { return null; }
        }

        private async Task PrefillAmendMessageAsync()
        {
            // Pre-fill only into an empty editor, so checking the box never clobbers typed text.
            if (!string.IsNullOrWhiteSpace(CommitSubject) || !string.IsNullOrWhiteSpace(CommitBody))
                return;
            var head = await GetHeadMessageAsync();
            if (head == null)
            {
                ErrorMessage = "There is no commit to amend.";
                IsAmend = false;
                return;
            }
            if (!IsAmend) return; // unchecked again while the message loaded
            CommitSubject = head.Value.Subject;
            CommitBody = head.Value.Body;
        }

        /// <summary>Shared plumbing for stage/unstage/discard: arm the watcher-suppression window,
        /// run the mutation off-thread, surface any error, and reload the status list explicitly
        /// (never wait for the watcher's debounce).</summary>
        private async Task RunStatusMutationAsync(Func<GitRepository, string?> operation)
        {
            if (_repo == null) return;
            GitRepository repo = _repo;
            _suppressRepoRefreshUntil = Environment.TickCount64 + 2000;
            try
            {
                string? error = await Task.Run(() => operation(repo));
                if (error != null)
                    ErrorMessage = error;
                await LoadStatusAsync();
            }
            catch (Exception ex) { ErrorMessage = ex.Message; }
        }

        private static IEnumerable<string> PathsOf(ChangedFile file)
        {
            yield return file.Path;
            // A staged rename occupies two index entries; unstaging needs both pathspecs.
            if (!string.IsNullOrEmpty(file.OldPath))
                yield return file.OldPath;
        }

        // ---- Branches & tags (Phase 5, BR-001..007 / TAG-001..003) -----------------------------

        /// <summary>Shared plumbing for ref-changing operations (checkout / branch / tag).
        /// Unlike <see cref="RunStatusMutationAsync"/> this deliberately does NOT arm
        /// _suppressRepoRefreshUntil: that window exists to downgrade a repoDirty event to a
        /// status-only reload for writes that touch only .git\index. These commands move HEAD and
        /// refs, so the log, the decoration badges and the sidebar all have to be rebuilt — arming
        /// the window would swallow exactly the refresh we need. Like CommitAsync we refresh
        /// explicitly and let the IsLoading guard in OnRepositoryChanged absorb the watcher echo.
        /// Returns git's error text, or null on success.</summary>
        private async Task<string?> RunRefMutationAsync(Func<GitRepository, string?> operation,
                                                        bool reportError = true)
        {
            if (_repo == null) return "No repository is open.";
            GitRepository repo = _repo;
            try
            {
                string? error = await Task.Run(() => operation(repo));
                if (error != null)
                {
                    if (reportError) ErrorMessage = error;
                    return error; // nothing changed — skip the refresh
                }
                await RefreshAsync();
                return null;
            }
            catch (Exception ex)
            {
                if (reportError) ErrorMessage = ex.Message;
                return ex.Message;
            }
        }

        /// <summary>BR-003. Not forced — git refuses rather than clobbering local changes.</summary>
        public Task<string?> CheckoutBranchAsync(string name)
            => RunRefMutationAsync(r => r.Checkout(name, detach: false));

        /// <summary>BR-003 (detached): check out a commit directly.</summary>
        public Task<string?> CheckoutCommitAsync(string sha)
            => RunRefMutationAsync(r => r.Checkout(sha, detach: true));

        /// <summary>Tracked changes that a branch switch could collide with. Untracked files never
        /// block a switch, so they are excluded from the BR-003 warning count.</summary>
        public async Task<int> CountLocalChangesAsync() => (await CountWorkingTreeAsync()).Tracked;

        /// <summary>Both halves of a dirty working tree, counted separately. A pull needs the
        /// distinction that a branch switch does not: untracked files DO block a fast-forward when
        /// an incoming commit adds the same path, and git's refusal then tells the user to move or
        /// remove them — advice that makes no sense for a tracked edit.</summary>
        public async Task<(int Tracked, int Untracked)> CountWorkingTreeAsync()
        {
            if (_repo == null) return (0, 0);
            GitRepository repo = _repo;
            try
            {
                return await Task.Run(() =>
                {
                    var st = repo.Status();
                    return (st.Staged.Count + st.Unstaged.Count, st.Untracked.Count);
                });
            }
            catch { return (0, 0); }
        }

        /// <summary>BR-004. An empty <paramref name="startPoint"/> means HEAD.</summary>
        public Task<string?> CreateBranchAsync(string name, string startPoint, bool checkout)
            => RunRefMutationAsync(r => r.CreateBranch(name.Trim(), startPoint.Trim(), checkout));

        /// <summary>BR-005. The unforced attempt reports its error to the caller instead of the
        /// InfoBar: it feeds the "delete anyway?" follow-up, so the flow is still in progress.</summary>
        public Task<string?> DeleteBranchAsync(string name, bool force)
            => RunRefMutationAsync(r => r.DeleteBranch(name, force), reportError: force);

        /// <summary>BR-006.</summary>
        public Task<string?> RenameBranchAsync(string oldName, string newName)
            => RunRefMutationAsync(r => r.RenameBranch(oldName, newName.Trim()));

        /// <summary>TAG-002. Blank message = lightweight tag; empty commitish = HEAD.</summary>
        public Task<string?> CreateTagAsync(string name, string commitish, string message)
            => RunRefMutationAsync(r => r.CreateTag(name.Trim(), commitish, message));

        /// <summary>TAG-003.</summary>
        public Task<string?> DeleteTagAsync(string name)
            => RunRefMutationAsync(r => r.DeleteTag(name));

        /// <summary>Start points offered by the create-branch dialog (BR-004): the same
        /// HEAD + branches + remotes + tags list the compare picker uses.</summary>
        public IReadOnlyList<string> StartPointOptions => _refNames;

        // ---- Stash (Phase 8, STASH-001..004) ---------------------------------------------------
        //
        // All four go through RunRefMutationAsync, NOT RunStatusMutationAsync. A stash moves
        // refs/stash, the index AND the working tree, so the log, the sidebar and the status view
        // all have to be rebuilt — and RunStatusMutationAsync's suppression window exists precisely
        // to swallow that refresh for index-only writes.
        //
        // Its unconditional RefreshAsync() on success is also what keeps the selectors honest:
        // "stash@{2}" is positional, so every drop and pop renumbers the entries below it.

        public bool HasStashes => Stashes.Count > 0;

        /// <summary>STASH-001. Reports an error when there was nothing to stash (git exits 0).</summary>
        public Task<string?> SaveStashAsync(string message, bool includeUntracked, bool keepIndex)
            => RunRefMutationAsync(r => r.StashSave(message, includeUntracked, keepIndex));

        /// <summary>STASH-002. The entry survives; a conflict reaches the caller as git wrote it.</summary>
        public Task<string?> ApplyStashAsync(string selector, bool reportError = true)
            => RunRefMutationAsync(r => r.StashApply(selector), reportError);

        /// <summary>STASH-003. On conflict git keeps the entry, so nothing is lost either way.</summary>
        public Task<string?> PopStashAsync(string selector, bool reportError = true)
            => RunRefMutationAsync(r => r.StashPop(selector), reportError);

        /// <summary>STASH-004. Irreversible — callers must confirm first.</summary>
        public Task<string?> DropStashAsync(string selector)
            => RunRefMutationAsync(r => r.StashDrop(selector));

        // ---- Reflog (Phase 8, REFLOG-001) ------------------------------------------------------

        private const int MaxReflogEntries = 500;

        private bool _isReflogMode;
        /// <summary>True while the history list shows the reflog instead of the commit log. The
        /// detail pane and diff viewer stay live: that is the whole point of a recovery tool.</summary>
        public bool IsReflogMode
        {
            get => _isReflogMode;
            private set => Set(ref _isReflogMode, value);
        }

        private string _reflogRef = "HEAD";
        /// <summary>Which ref's reflog is shown. Only HEAD is offered today; the field exists
        /// because the same list renders any ref's history.</summary>
        public string ReflogRef
        {
            get => _reflogRef;
            private set => Set(ref _reflogRef, value);
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
                CommitRow? row = Commits.FirstOrDefault(c => c.FullHash == sha)
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

        // ---- Remotes (Phase 6, REMOTE-001..009) ------------------------------------------------

        // Every command gate depends on repository, loading and busy state, so all of them are
        // raised together whenever one of those inputs moves.
        private void RaiseCommandState()
        {
            Raise(nameof(CanFetch));
            Raise(nameof(CanPull));
            Raise(nameof(CanPush));
            Raise(nameof(CanStartOperation));
            Raise(nameof(CanResolveOperation));
            Raise(nameof(CanSkipOperation));
            Raise(nameof(CanStash));
            Raise(nameof(CanSearch));
        }

        private bool _isGitBusy;
        /// <summary>True while a command fronted by the progress dialog is running (fetch/pull/push,
        /// and the Phase 7 merge/rebase/cherry-pick/revert): the commands that would collide with it
        /// are disabled, so two of them can never overlap on one repository.</summary>
        public bool IsGitBusy
        {
            get => _isGitBusy;
            private set { if (Set(ref _isGitBusy, value)) RaiseCommandState(); }
        }

        private string _currentBranchName = "";
        /// <summary>The checked-out branch, or empty on a detached HEAD — which is exactly when
        /// pull and push have nothing to act on.</summary>
        public string CurrentBranchName
        {
            get => _currentBranchName;
            private set { if (Set(ref _currentBranchName, value)) RaiseCommandState(); }
        }

        /// <summary>REMOTE-003/006: the current branch's upstream ("origin/main"), split for the
        /// pull/push defaults. Empty when the branch has never been published.</summary>
        private string _upstreamName = "";
        public string UpstreamName
        {
            get => _upstreamName;
            private set
            {
                if (!Set(ref _upstreamName, value)) return;
                Raise(nameof(HasUpstream));
                RaiseCommandState();
            }
        }
        public bool HasUpstream => _upstreamName.Length > 0;

        public string UpstreamRemote { get; private set; } = "";
        public string UpstreamBranch { get; private set; } = "";

        private string _headerTrackText = "";
        /// <summary>REMOTE-003: "↑2 ↓1" / "gone" / "" for the repository header, formatted exactly
        /// like the sidebar's per-branch badge.</summary>
        public string HeaderTrackText
        {
            get => _headerTrackText;
            private set { if (Set(ref _headerTrackText, value)) Raise(nameof(HasHeaderTrack)); }
        }
        public bool HasHeaderTrack => _headerTrackText.Length > 0;

        public bool CanFetch => HasRepository && !IsGitBusy && !IsLoading;
        public bool CanPull => HasRepository && !IsGitBusy && !IsLoading && CurrentBranchName.Length > 0;
        public bool CanPush => HasRepository && !IsGitBusy && !IsLoading && CurrentBranchName.Length > 0;

        /// <summary>Phase 7: a new merge/rebase/cherry-pick/revert can only be started when nothing
        /// else is half-finished — git refuses otherwise, and offering it invites that refusal.</summary>
        public bool CanStartOperation => HasRepository && !IsGitBusy && !IsLoading && !HasActiveOperation;

        /// <summary>The continue/abort half: available exactly while something IS in progress.</summary>
        public bool CanResolveOperation => HasRepository && !IsGitBusy && !IsLoading && HasActiveOperation;

        /// <summary>STASH-001. Stashing mid-merge would park the conflict resolution itself, and
        /// git's own refusals there are cryptic — the same guard the Phase 7 commands use.</summary>
        public bool CanStash => HasRepository && !IsGitBusy && !IsLoading && !HasActiveOperation;

        /// <summary>SEARCH-001. A search is a plain read, so only a load can be in its way.</summary>
        public bool CanSearch => HasRepository && !IsLoading;

        /// <summary>REMOTE-001. Read on demand rather than on every refresh: a remote added
        /// outside the app has no refs yet, so the sidebar cannot be the source of truth.</summary>
        public async Task<IReadOnlyList<RemoteInfo>> ListRemotesAsync()
        {
            if (_repo == null) return Array.Empty<RemoteInfo>();
            GitRepository repo = _repo;
            try { return await Task.Run(() => repo.ListRemotes()); }
            catch (Exception ex) { ErrorMessage = ex.Message; return Array.Empty<RemoteInfo>(); }
        }

        /// <summary>REMOTE-008. Returns git's error text, or null on success.</summary>
        public async Task<string?> SetRemoteUrlAsync(string name, string url, bool pushUrl)
        {
            if (_repo == null) return "No repository is open.";
            GitRepository repo = _repo;
            try
            {
                string? error = await Task.Run(() => repo.SetRemoteUrl(name, url, pushUrl));
                if (error == null)
                    await RefreshAsync();
                return error;
            }
            catch (Exception ex) { return ex.Message; }
        }

        /// <summary>Shared plumbing for fetch/pull/push. Unlike <see cref="RunRefMutationAsync"/>
        /// this refreshes even when the command FAILED: a fetch can update some refs before
        /// erroring on another, and a partially-applied push still moved the remote-tracking ref.
        /// Errors are returned rather than pushed to the InfoBar — the progress dialog is already
        /// showing the output, and duplicating a multi-paragraph hint there would be noise.</summary>
        private async Task<string?> RunRemoteMutationAsync(Func<GitRepository, string?> operation)
        {
            if (_repo == null) return "No repository is open.";
            GitRepository repo = _repo;
            IsGitBusy = true;
            try
            {
                string? error = await Task.Run(() => operation(repo));
                await RefreshAsync();
                return error;
            }
            catch (Exception ex) { return ex.Message; }
            finally { IsGitBusy = false; }
        }

        /// <summary>REMOTE-002/007. <paramref name="progress"/> is reported from a background
        /// thread, so it must have been created on the UI thread.</summary>
        public Task<string?> FetchAsync(string remote, bool allRemotes, bool prune, bool tags,
                                        IProgress<string> progress, CancellationToken token)
            => RunRemoteMutationAsync(r => r.Fetch(remote, allRemotes, prune, tags,
                                                   Sink(progress, token)));

        /// <summary>REMOTE-004, fast-forward only, against the current branch's upstream.</summary>
        public Task<string?> PullAsync(IProgress<string> progress, CancellationToken token)
            => RunRemoteMutationAsync(r => r.Pull(UpstreamRemote, UpstreamBranch, Sink(progress, token)));

        /// <summary>REMOTE-005/006.</summary>
        public Task<string?> PushAsync(string remote, string branch, bool setUpstream, bool pushTags,
                                       IProgress<string> progress, CancellationToken token)
            => RunRemoteMutationAsync(r => r.Push(remote, branch, setUpstream, pushTags,
                                                  Sink(progress, token)));

        /// <summary>Bridges the native progress callback to an <see cref="IProgress{T}"/>. The
        /// empty chunk is the heartbeat — it carries no text, and exists only so a command that
        /// has gone quiet can still notice the cancellation.</summary>
        private static Func<string, bool> Sink(IProgress<string> progress, CancellationToken token)
            => chunk =>
            {
                if (chunk.Length > 0)
                    progress.Report(chunk);
                return !token.IsCancellationRequested;
            };

        // ---- Merge / rebase / cherry-pick / revert (Phase 7) -----------------------------------

        private RepositoryState _state = RepositoryState.None;
        /// <summary>What git is in the middle of. Drives the state banner (MERGE-002/003,
        /// REBASE-002) and is re-read after every operation and every repository refresh.</summary>
        public RepositoryState State
        {
            get => _state;
            private set
            {
                if (!Set(ref _state, value)) return;
                Raise(nameof(HasActiveOperation));
                Raise(nameof(StateTitle));
                Raise(nameof(StateDetail));
                Raise(nameof(CanSkipOperation));
                RaiseCommandState();
            }
        }

        public bool HasActiveOperation => State.IsActive;

        /// <summary>Skip is hidden for a merge — git has no `merge --skip`, and skipping the only
        /// commit a merge has would just be Abort under a friendlier name.</summary>
        public bool CanSkipOperation => CanResolveOperation && State.CanSkip;

        private int _conflictCount;
        /// <summary>How many working-tree files are still conflicted (MERGE-003).</summary>
        public int ConflictCount
        {
            get => _conflictCount;
            private set
            {
                if (!Set(ref _conflictCount, value)) return;
                Raise(nameof(HasConflicts));
                Raise(nameof(StateDetail));
                Raise(nameof(CanCommit));
            }
        }

        public bool HasConflicts => _conflictCount > 0;

        /// <summary>Banner headline: what is happening, and to what.</summary>
        public string StateTitle => State.Op switch
        {
            RepoOperation.Merging => State.Detail.Length > 0
                ? $"Merge in progress ({State.Detail})"
                : "Merge in progress",
            RepoOperation.Rebasing => State.Detail.Length > 0
                ? $"Rebasing {State.Detail}"
                : "Rebase in progress",
            RepoOperation.CherryPicking => "Cherry-pick in progress",
            RepoOperation.Reverting => "Revert in progress",
            _ => "",
        };

        /// <summary>Banner subtitle: how far along, and what is in the way.</summary>
        public string StateDetail
        {
            get
            {
                if (!State.IsActive)
                    return "";
                var parts = new List<string>();
                if (State.HasSteps)
                    parts.Add($"step {State.Step} of {State.Total}");
                parts.Add(HasConflicts
                    ? $"{_conflictCount} conflicted file{(_conflictCount == 1 ? "" : "s")} to resolve"
                    : "no conflicts left — continue to finish");
                return string.Join("  ·  ", parts);
            }
        }

        /// <summary>Re-reads the half-finished-operation state. One git spawn; the conflict count
        /// costs a second one and is only paid for while something is actually in progress.</summary>
        private async Task LoadStateAsync()
        {
            if (_repo == null)
            {
                State = RepositoryState.None;
                ConflictCount = 0;
                return;
            }
            GitRepository repo = _repo;
            try
            {
                RepositoryState state = await Task.Run(() => repo.State());
                int conflicts = state.IsActive
                    ? await Task.Run(() => repo.Status().Conflicted.Count)
                    : 0;
                State = state;
                ConflictCount = conflicts;
                if (state.IsActive)
                    PrefillOperationMessage(state);
                else
                    ClearPrefilledMessage();
            }
            catch (Exception ex) { ErrorMessage = ex.Message; }
        }

        /// <summary>
        /// Opens the commit editor with the message git already wrote for this operation (MERGE_MSG),
        /// so a resolved merge commits with "Merge branch 'x'" rather than whatever the user
        /// invents. Never overwrites typed text — same rule as the amend pre-fill.
        ///
        /// A rebase is deliberately excluded: its commits are finished with `rebase --continue`,
        /// which reuses the original message itself. Pre-filling the editor there would invite a
        /// commit that does not belong.
        /// </summary>
        private void PrefillOperationMessage(RepositoryState state)
        {
            if (state.Op == RepoOperation.Rebasing || state.Message.Length == 0)
                return;
            if (!string.IsNullOrWhiteSpace(CommitSubject) || !string.IsNullOrWhiteSpace(CommitBody))
                return;

            string message = state.Message.Replace("\r\n", "\n").Replace('\r', '\n');
            int blank = message.IndexOf("\n\n", StringComparison.Ordinal);
            CommitSubject = blank < 0 ? message.Trim() : message[..blank].Trim();
            CommitBody = blank < 0 ? "" : message[(blank + 2)..].Trim();
            _prefilledSubject = CommitSubject;
            _prefilledBody = CommitBody;
        }

        // What the pre-fill last wrote into the editor, so it can be taken back out again.
        private string _prefilledSubject = "";
        private string _prefilledBody = "";

        /// <summary>
        /// Takes the pre-filled message back out once the operation is over. Without this the
        /// editor keeps "Merge branch 'x'" after the merge has been committed, and the NEXT
        /// unrelated commit inherits it. Only clears text the pre-fill itself put there — anything
        /// the user typed over it is theirs and stays.
        /// </summary>
        private void ClearPrefilledMessage()
        {
            if (_prefilledSubject.Length == 0 && _prefilledBody.Length == 0)
                return;
            if (CommitSubject == _prefilledSubject && CommitBody == _prefilledBody)
            {
                CommitSubject = "";
                CommitBody = "";
            }
            _prefilledSubject = "";
            _prefilledBody = "";
        }

        /// <summary>
        /// Shared plumbing for the Phase 7 commands. Like <see cref="RunRemoteMutationAsync"/> it
        /// refreshes <b>even when the command failed</b> — and here that matters more than anywhere
        /// else: a merge that stops on a conflict exits non-zero having rewritten the index, moved
        /// nothing, and left the repository mid-operation. Skipping the refresh would leave the app
        /// showing a repository that no longer exists.
        ///
        /// Errors are returned rather than pushed to the InfoBar; the progress dialog is already
        /// showing git's output, and a CONFLICT is not an error bar's business.
        /// </summary>
        private async Task<string?> RunSequencerMutationAsync(Func<GitRepository, string?> operation)
        {
            if (_repo == null) return "No repository is open.";
            GitRepository repo = _repo;
            IsGitBusy = true;
            try
            {
                string? error = await Task.Run(() => operation(repo));
                await RefreshAsync();
                return error;
            }
            catch (Exception ex) { return ex.Message; }
            finally { IsGitBusy = false; }
        }

        /// <summary>MERGE-001.</summary>
        public Task<string?> MergeAsync(string refName, bool noFastForward, bool noCommit,
                                        IProgress<string> progress, CancellationToken token)
            => RunSequencerMutationAsync(r => r.Merge(refName, noFastForward, noCommit, Sink(progress, token)));

        /// <summary>REBASE-001.</summary>
        public Task<string?> RebaseAsync(string upstream, IProgress<string> progress,
                                         CancellationToken token)
            => RunSequencerMutationAsync(r => r.Rebase(upstream, Sink(progress, token)));

        /// <summary>CHERRY-001/002. <paramref name="commits"/> may arrive in any order; they are
        /// applied oldest-first.</summary>
        public Task<string?> CherryPickAsync(IReadOnlyList<CommitRow> commits, bool noCommit,
                                             IProgress<string> progress, CancellationToken token)
        {
            var shas = OldestFirst(commits).Select(c => c.FullHash).ToList();
            return shas.Count == 0
                ? Task.FromResult<string?>("No commits were selected.")
                : RunSequencerMutationAsync(r => r.CherryPick(shas, noCommit, Sink(progress, token)));
        }

        /// <summary>REVERT-001. <paramref name="mainline"/> is 1-based; 0 for a non-merge commit.</summary>
        public Task<string?> RevertAsync(string sha, int mainline, bool noCommit,
                                         IProgress<string> progress, CancellationToken token)
            => RunSequencerMutationAsync(r => r.Revert(sha, mainline, noCommit, Sink(progress, token)));

        /// <summary>MERGE-002 / REBASE-002: continue, skip or abort whatever is in progress.</summary>
        public Task<string?> ContinueOperationAsync(IProgress<string> progress, CancellationToken token)
            => SequencerActionAsync("continue", progress, token);

        public Task<string?> SkipOperationAsync(IProgress<string> progress, CancellationToken token)
            => SequencerActionAsync("skip", progress, token);

        public Task<string?> AbortOperationAsync(IProgress<string> progress, CancellationToken token)
            => SequencerActionAsync("abort", progress, token);

        private Task<string?> SequencerActionAsync(string action, IProgress<string> progress,
                                                   CancellationToken token)
        {
            string command = State.GitCommand;
            if (command.Length == 0)
                return Task.FromResult<string?>("Nothing is in progress.");
            return RunSequencerMutationAsync(r => r.SequencerAction(command, action, Sink(progress, token)));
        }

        /// <summary>MERGE-004. Git launches the tool and stages the file itself when it exits
        /// cleanly, so this refreshes like any other status mutation.</summary>
        public Task<string?> RunMergeToolAsync(ChangedFile file, string tool,
                                               IProgress<string> progress, CancellationToken token)
            => RunSequencerMutationAsync(r => r.MergeTool(file.Path, tool, Sink(progress, token)));

        /// <summary>Marks a conflicted file resolved: staging it is exactly what
        /// "git add &lt;file&gt;" means once the markers are gone, and it is what git's own advice
        /// tells the user to do.</summary>
        public Task MarkResolvedAsync(ChangedFile file)
            => RunStatusMutationAsync(r => r.StagePaths(new[] { file.Path }));

        /// <summary>
        /// The selected commits in the order git will apply them — public so the confirmation
        /// dialog can show exactly that order rather than the order they happen to appear in.
        /// </summary>
        public IReadOnlyList<CommitRow> OrderForCherryPick(IReadOnlyList<CommitRow> commits)
            => OldestFirst(commits);

        /// <summary>
        /// The selected commits in the order git must apply them. The log's own ordering is the
        /// source of truth — sorting by date would break a picked range whose commits share a
        /// timestamp or were rewritten out of chronological order. "Reverse Date Order" is the one
        /// mode that already lists oldest-first; every other mode lists newest-first.
        /// </summary>
        private List<CommitRow> OldestFirst(IReadOnlyList<CommitRow> commits)
        {
            bool logIsOldestFirst = SelectedOrderIndex == 2;
            var indexed = commits
                .Select(c => (Commit: c, Index: _allCommits.IndexOf(c)))
                .Where(t => t.Index >= 0)
                .ToList();
            if (indexed.Count != commits.Count)
                return commits.ToList(); // a row we cannot place: keep the caller's order rather than guess
            return (logIsOldestFirst
                    ? indexed.OrderBy(t => t.Index)
                    : indexed.OrderByDescending(t => t.Index))
                .Select(t => t.Commit)
                .ToList();
        }

        // ---- Binary / image diff (DIFF-005) ----------------------------------------------------

        private string _binaryInfoText = "";
        public string BinaryInfoText { get => _binaryInfoText; private set => Set(ref _binaryInfoText, value); }

        private ImageSource? _binaryOldImage;
        public ImageSource? BinaryOldImage { get => _binaryOldImage; private set => Set(ref _binaryOldImage, value); }

        private ImageSource? _binaryNewImage;
        public ImageSource? BinaryNewImage { get => _binaryNewImage; private set => Set(ref _binaryNewImage, value); }

        private bool _binaryHasImages;
        public bool BinaryHasImages { get => _binaryHasImages; private set => Set(ref _binaryHasImages, value); }

        private async Task LoadBinaryPreviewAsync(ChangedFile file, string baseRef, string targetRef, bool range)
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

            string newRef = targetRef;                          // new side = target commit/ref
            string oldRef = range ? baseRef : targetRef + "^";  // old side = base, or first parent

            try
            {
                byte[] newBytes = file.Status == FileChangeStatus.Deleted
                    ? Array.Empty<byte>()
                    : await Task.Run(() => repo.FileBytesAt(newRef, file.Path));
                byte[] oldBytes = file.Status == FileChangeStatus.Added
                    ? Array.Empty<byte>()
                    : await Task.Run(() => repo.FileBytesAt(oldRef, file.Path));

                if (!ReferenceEquals(file, SelectedFile)) return;

                BinaryNewImage = await BytesToImageAsync(newBytes);
                BinaryOldImage = await BytesToImageAsync(oldBytes);
                BinaryHasImages = BinaryNewImage != null || BinaryOldImage != null;
            }
            catch (Exception ex) { ErrorMessage = ex.Message; }
        }

        private static async Task<ImageSource?> BytesToImageAsync(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return null;
            try
            {
                var stream = new InMemoryRandomAccessStream();
                using (var writer = new DataWriter(stream))
                {
                    writer.WriteBytes(bytes);
                    await writer.StoreAsync();
                    await writer.FlushAsync();
                    writer.DetachStream();
                }
                stream.Seek(0);
                var image = new BitmapImage();
                await image.SetSourceAsync(stream);
                return image;
            }
            catch { return null; }
        }

        private static string FormatStat(DiffStat s)
        {
            if (s.IsEmpty) return "No changes";
            string files = $"{s.Files} file{(s.Files == 1 ? "" : "s")} changed";
            return $"{files},  +{s.Insertions}  −{s.Deletions}";
        }

        private static string Short(string hash) => hash.Length > 7 ? hash[..7] : hash;

        // ---- Filtering / ordering --------------------------------------------------------------

        private string _searchText = "";
        public string SearchText
        {
            get => _searchText;
            set
            {
                if (!Set(ref _searchText, value))
                    return;
                // While results are showing, typing is composing the NEXT query (Enter runs it) —
                // filtering the results by it here would narrow them a second time, and in author
                // or content mode by text that is not in the message at all.
                if (!IsSearchResultMode)
                    ApplyFilter();
            }
        }

        private int _selectedOrderIndex;
        public int SelectedOrderIndex
        {
            get => _selectedOrderIndex;
            set
            {
                if (!Set(ref _selectedOrderIndex, value))
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
            set => Set(ref _selectedSearchModeIndex, value);
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
            set => Set(ref _searchMatchCase, value);
        }

        private bool _searchUseRegex;
        public bool SearchUseRegex
        {
            get => _searchUseRegex;
            set => Set(ref _searchUseRegex, value);
        }

        private bool _searchAllBranches = true;
        /// <summary>On by default, matching the commit list itself (which loads with --all).</summary>
        public bool SearchAllBranches
        {
            get => _searchAllBranches;
            set => Set(ref _searchAllBranches, value);
        }

        private string _searchPathFilter = "";
        public string SearchPathFilter
        {
            get => _searchPathFilter;
            set => Set(ref _searchPathFilter, value ?? "");
        }

        private bool _isSearchResultMode;
        /// <summary>True while the commit list holds search results rather than the log.</summary>
        public bool IsSearchResultMode
        {
            get => _isSearchResultMode;
            private set => Set(ref _isSearchResultMode, value);
        }

        private string _searchBannerText = "";
        public string SearchBannerText
        {
            get => _searchBannerText;
            private set => Set(ref _searchBannerText, value);
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
                _allCommits = results.ToList();
                IsSearchResultMode = true;
                SearchBannerText = DescribeSearch(mode, query, path, results.Count);

                // The in-memory filter would narrow the results by the same text a second time —
                // and in author/content/path mode that text is not in the message at all, so it
                // would empty a list that has just been filled.
                Commits.Clear();
                foreach (var c in results)
                    Commits.Add(c);
                SelectedCommit = Commits.FirstOrDefault();
            }
            catch (Exception ex) { ErrorMessage = ex.Message; }
            finally { IsLoading = false; }
        }

        private static string DescribeSearch(SearchMode mode, string query, string path, int count)
        {
            string what = mode switch
            {
                SearchMode.Author => $"author “{query}”",
                SearchMode.Content => $"changes containing “{query}”",
                SearchMode.Path => $"commits touching “{query}”",
                SearchMode.Hash => $"commit {query}",
                _ => $"message “{query}”",
            };
            if (query.Length == 0)
                what = $"commits touching “{path}”";
            else if (path.Length > 0)
                what += $" under “{path}”";
            return $"{count} result{(count == 1 ? "" : "s")} for {what}";
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

                // Update recents (CORE-002).
                var recent = await Task.Run(() => RecentRepositoriesStore.Add(repo.ToInfo()));
                Recent.Clear();
                foreach (var r in recent) Recent.Add(r);

                // Sidebar + ref caches from real refs, plus the stash (one extra spawn, and the
                // sidebar section it feeds sits alongside the ref sections).
                var loaded = await Task.Run(() => (Refs: repo.ListRefs(), Stashes: repo.ListStashes()));
                ApplyRefs(loaded.Refs, loaded.Stashes);

                await ReloadLogAsync();
                await LoadStateAsync(); // a repository can be opened mid-merge (Phase 7)
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

                var loaded = await Task.Run(() => (Refs: repo.ListRefs(), Stashes: repo.ListStashes()));
                ApplyRefs(loaded.Refs, loaded.Stashes);

                if (!wasSearch)
                {
                    var commits = await Task.Run(() => repo.Log(SelectedOrderIndex, MaxCommits));
                    _allCommits = commits.ToList();
                    ApplyFilter();
                    // Rebuilt rows are new objects, so restore the selection by hash; the fresh row
                    // lazily reloads its files/diff, which is exactly what a refresh should do.
                    SelectedCommit = (prevSha != null ? Commits.FirstOrDefault(c => c.FullHash == prevSha) : null)
                                     ?? Commits.FirstOrDefault();
                }
            }
            catch (Exception ex) { ErrorMessage = ex.Message; }
            finally { IsLoading = false; }

            // Results are re-run rather than replaced by the plain log: a refresh triggered by an
            // unrelated commit must not silently throw away the search the user is looking at.
            if (wasSearch)
                await LoadSearchResultsAsync();

            // After IsLoading clears, so the banner's command gates settle on their final values.
            await LoadStateAsync();

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
            _allCommits = new List<CommitRow>();
            Commits.Clear();
            Sidebar.Clear();
            ClearCompareState();
            IsWorkingCopyMode = false;
            IsReflogMode = false;
            ReflogEntries.Clear();
            Stashes.Clear();
            Raise(nameof(HasStashes));
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
            Raise(nameof(HasMarkedCommit));
            PanelFiles.Clear();
            ChangedSummary = "";
            _refNames = new List<string>();
            Raise(nameof(CompareRefNames));
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
                _allCommits = commits.ToList();
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

            Commits.Clear();
            foreach (var c in source)
                Commits.Add(c);

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
                    List<DiffRow> rows = isBinary ? new List<DiffRow>() : SideBySideBuilder.Build(lines, langId);
                    return (lines, rows, isBinary);
                });

                if (file.DiffLoaded) return;

                file.Diff.Clear();
                foreach (DiffLine d in result.lines) file.Diff.Add(d);
                file.Rows.Clear();
                foreach (DiffRow r in result.rows) file.Rows.Add(r);
                file.IsBinary = result.isBinary;
                file.DiffLoaded = true;

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

        // ---- Sidebar ---------------------------------------------------------------------------

        /// <summary>Push a freshly-read ref set into the sidebar, the compare picker, and the
        /// caches the branch/tag dialogs read. Called from both load and refresh.</summary>
        private void ApplyRefs(GitRepository.RefList refs, IReadOnlyList<StashEntry> stashes)
        {
            Stashes.Clear();
            foreach (var s in stashes)
                Stashes.Add(s);
            Raise(nameof(HasStashes));

            BuildSidebar(refs, stashes);

            // Ref names for the compare-refs picker (DIFF-007) and the create-branch start point
            // (BR-004): HEAD + branches + remotes + tags.
            _refNames = new List<string> { "HEAD" };
            _refNames.AddRange(refs.Branches.Select(b => b.Name));
            _refNames.AddRange(refs.Remotes.Select(r => $"{r.Remote}/{r.Name}"));
            _refNames.AddRange(refs.Tags.Select(t => t.Name));
            Raise(nameof(CompareRefNames));

            // REMOTE-003: the header's divergence comes from the ref list we already have —
            // %(upstream:track) is parsed per branch in ListRefs, so this costs no extra git call.
            BranchInfo? current = refs.Branches.FirstOrDefault(b => b.IsCurrent);
            CurrentBranchName = current?.Name ?? "";
            UpstreamName = current?.Upstream ?? "";
            HeaderTrackText = current == null
                ? ""
                : SidebarItemVM.FormatTrack(current.Ahead, current.Behind, current.UpstreamGone);
            (UpstreamRemote, UpstreamBranch) = SplitUpstream(
                UpstreamName, refs.Remotes.Select(r => r.Remote));
        }

        /// <summary>Splits "origin/feature/x" into ("origin", "feature/x"). Matching against the
        /// known remote names first matters: a branch may itself contain slashes, so the first
        /// slash is not reliably the boundary.</summary>
        private static (string Remote, string Branch) SplitUpstream(string upstream,
                                                                    IEnumerable<string> remoteNames)
        {
            if (upstream.Length == 0)
                return ("", "");
            foreach (string name in remoteNames.Distinct().OrderByDescending(n => n.Length))
            {
                if (upstream.StartsWith(name + "/", StringComparison.Ordinal))
                    return (name, upstream[(name.Length + 1)..]);
            }
            int slash = upstream.IndexOf('/');
            return slash < 0 ? ("", upstream) : (upstream[..slash], upstream[(slash + 1)..]);
        }

        private void BuildSidebar(GitRepository.RefList refs, IReadOnlyList<StashEntry> stashes)
        {
            Sidebar.Clear();

            void Add(SidebarItemVM item) => Sidebar.Add(item);

            void AddPlain(string text, SidebarKind kind, int level, SidebarItemVM? parent)
                => Add(new SidebarItemVM { Text = text, Kind = kind, Level = level, ParentItem = parent });

            AddPlain("FILE STATUS", SidebarKind.SectionHeader, 0, null);
            var fileStatus = Sidebar[^1];
            AddPlain("Working Copy", SidebarKind.WorkingCopy, 1, fileStatus);
            // REFLOG-001. Lives beside Working Copy rather than in its own section: like the
            // working copy it is a VIEW of this repository, not a ref you can act on.
            AddPlain("Reflog", SidebarKind.Reflog, 1, fileStatus);

            AddPlain("BRANCHES", SidebarKind.SectionHeader, 0, null);
            var branches = Sidebar[^1];
            foreach (var b in refs.Branches)
                Add(new SidebarItemVM
                {
                    Text = b.Name,
                    ShortName = b.Name,
                    RefName = b.RefName,
                    Sha = b.Sha,
                    Upstream = b.Upstream,
                    Ahead = b.Ahead,
                    Behind = b.Behind,
                    UpstreamGone = b.UpstreamGone,
                    IsCurrent = b.IsCurrent,
                    Kind = SidebarKind.Branch,
                    Level = 1,
                    ParentItem = branches,
                });

            if (refs.Tags.Count > 0)
            {
                AddPlain("TAGS", SidebarKind.SectionHeader, 0, null);
                var tags = Sidebar[^1];
                foreach (var t in refs.Tags)
                    Add(new SidebarItemVM
                    {
                        Text = t.Name,
                        ShortName = t.Name,
                        RefName = t.RefName,
                        Sha = t.CommitSha,
                        Kind = SidebarKind.Tag,
                        Level = 1,
                        ParentItem = tags,
                    });
            }

            if (stashes.Count > 0)
            {
                AddPlain("STASHES", SidebarKind.SectionHeader, 0, null);
                var stashHeader = Sidebar[^1];
                foreach (var s in stashes)
                    Add(new SidebarItemVM
                    {
                        // The selector is what apply/pop/drop take, and it is POSITIONAL — these
                        // rows are rebuilt on every refresh precisely because a drop renumbers them.
                        // It is shown as well as stored: which entry a row IS matters when the
                        // messages are git's own "WIP on main" boilerplate and read alike.
                        Text = s.Message.Length > 0 ? $"{{{s.Index}}}  {s.Message}" : s.Selector,
                        ShortName = s.Selector,
                        RefName = s.Selector,
                        Sha = s.Sha,
                        Upstream = s.Branch,
                        Kind = SidebarKind.Stash,
                        Level = 1,
                        ParentItem = stashHeader,
                    });
            }

            if (refs.Remotes.Count > 0)
            {
                AddPlain("REMOTES", SidebarKind.SectionHeader, 0, null);
                var remotesHeader = Sidebar[^1];

                // Group "origin/main", "origin/dev" -> origin { main, dev }.
                foreach (var group in refs.Remotes
                             .GroupBy(r => r.Remote, StringComparer.Ordinal)
                             .OrderBy(g => g.Key, StringComparer.Ordinal))
                {
                    AddPlain(group.Key, SidebarKind.Remote, 1, remotesHeader);
                    var remoteNode = Sidebar[^1];
                    foreach (var r in group.Where(r => r.Name.Length > 0))
                        Add(new SidebarItemVM
                        {
                            // Displayed as "dev" under the "origin" node, but git wants "origin/dev".
                            Text = r.Name,
                            ShortName = $"{r.Remote}/{r.Name}",
                            RefName = r.RefName,
                            Sha = r.Sha,
                            Kind = SidebarKind.RemoteBranch,
                            Level = 2,
                            ParentItem = remoteNode,
                        });
                }
            }

            // Start with the checked-out branch selected. Nothing is current on a detached HEAD,
            // which is a correct no-op rather than an error.
            var current = Sidebar.FirstOrDefault(i => i.IsCurrent);
            if (current != null) current.IsSelected = true;

            RecomputeVisibility();
        }

        public void ToggleSidebar(SidebarItemVM item)
        {
            if (!item.IsExpandable) return;
            item.IsExpanded = !item.IsExpanded;
            RecomputeVisibility();
        }

        public void HandleSidebarTap(SidebarItemVM item)
        {
            if (item.IsHeader) { ToggleSidebar(item); return; }
            foreach (var i in Sidebar) i.IsSelected = false;
            item.IsSelected = true;
            if (item.Kind == SidebarKind.WorkingCopy)
            {
                _ = EnterWorkingCopyAsync(); // STATUS-001: switch to the working-copy view
                return;
            }
            if (item.Kind == SidebarKind.Reflog)
            {
                _ = EnterReflogAsync(); // REFLOG-001
                return;
            }
            if (item.IsExpandable) ToggleSidebar(item); // e.g. a remote node
        }

        private void RecomputeVisibility()
        {
            foreach (var item in Sidebar)
            {
                bool visible = true;
                var p = item.ParentItem;
                while (p != null)
                {
                    if (!p.IsExpanded) { visible = false; break; }
                    p = p.ParentItem;
                }
                item.IsVisible = visible;
            }
        }
    }
}
