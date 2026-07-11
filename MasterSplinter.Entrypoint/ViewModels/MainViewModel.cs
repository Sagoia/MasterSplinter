using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
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
                if (Set(ref _repository, value))
                    Raise(nameof(HasRepository));
            }
        }

        public bool HasRepository => Repository != null;

        private bool _isLoading;
        public bool IsLoading { get => _isLoading; private set => Set(ref _isLoading, value); }

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
                if (!IsCompareMode) return; // exited while loading
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
            var current = Sidebar.FirstOrDefault(
                i => i.Kind == SidebarKind.Branch && i.Text == Repository?.Branch);
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
                AddStatusGroup("Staged files", st.Staged);
                AddStatusGroup("Unstaged files", st.Unstaged);
                AddStatusGroup("Untracked files", st.Untracked);

                int total = st.Staged.Count + st.Unstaged.Count + st.Untracked.Count;
                ChangedSummary = total == 0
                    ? "Working tree clean"
                    : $"{st.Staged.Count} staged  ·  {st.Unstaged.Count} unstaged  ·  {st.Untracked.Count} untracked";

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
            set { if (Set(ref _searchText, value)) ApplyFilter(); }
        }

        private int _selectedOrderIndex;
        public int SelectedOrderIndex
        {
            get => _selectedOrderIndex;
            set { if (Set(ref _selectedOrderIndex, value)) _ = ReloadLogAsync(); }
        }

        // ---- Loading ---------------------------------------------------------------------------

        public async Task LoadRepositoryAsync(string path)
        {
            ErrorMessage = null;
            IsLoading = true;
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

                // Sidebar from real refs.
                var refs = await Task.Run(() => repo.ListRefs());
                BuildSidebar(refs, repo.Branch);

                // Ref names for the compare-refs picker (DIFF-007): HEAD + branches + remotes + tags.
                _refNames = new List<string> { "HEAD" };
                _refNames.AddRange(refs.Branches);
                _refNames.AddRange(refs.Remotes);
                _refNames.AddRange(refs.Tags);
                Raise(nameof(CompareRefNames));

                await ReloadLogAsync();
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

                var refs = await Task.Run(() => repo.ListRefs());
                BuildSidebar(refs, repo.Branch);
                _refNames = new List<string> { "HEAD" };
                _refNames.AddRange(refs.Branches);
                _refNames.AddRange(refs.Remotes);
                _refNames.AddRange(refs.Tags);
                Raise(nameof(CompareRefNames));

                var commits = await Task.Run(() => repo.Log(SelectedOrderIndex, MaxCommits));
                _allCommits = commits.ToList();
                ApplyFilter();
                // Rebuilt rows are new objects, so restore the selection by hash; the fresh row
                // lazily reloads its files/diff, which is exactly what a refresh should do.
                SelectedCommit = (prevSha != null ? Commits.FirstOrDefault(c => c.FullHash == prevSha) : null)
                                 ?? Commits.FirstOrDefault();
            }
            catch (Exception ex) { ErrorMessage = ex.Message; }
            finally { IsLoading = false; }

            if (wasWorkingCopy)
                await EnterWorkingCopyAsync(); // restore the working-copy view with fresh status
        }

        /// <summary>Debounced watcher callback (UI thread). repoDirty = .git state changed.</summary>
        private void OnRepositoryChanged(bool repoDirty)
        {
            if (_repo == null || IsLoading) return;
            if (repoDirty)
                _ = RefreshAsync();          // commit/checkout/stage happened outside the app
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
            StatusGroups.Clear();
            _markedCommit = null;
            Raise(nameof(HasMarkedCommit));
            PanelFiles.Clear();
            ChangedSummary = "";
            _refNames = new List<string>();
            Raise(nameof(CompareRefNames));
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

        private void BuildSidebar(GitRepository.Refs refs, string currentBranch)
        {
            Sidebar.Clear();

            void Add(string text, SidebarKind kind, int level, SidebarItemVM? parent)
                => Sidebar.Add(new SidebarItemVM { Text = text, Kind = kind, Level = level, ParentItem = parent });

            Add("FILE STATUS", SidebarKind.SectionHeader, 0, null);
            var fileStatus = Sidebar[^1];
            Add("Working Copy", SidebarKind.WorkingCopy, 1, fileStatus);

            Add("BRANCHES", SidebarKind.SectionHeader, 0, null);
            var branches = Sidebar[^1];
            foreach (var b in refs.Branches)
                Add(b, SidebarKind.Branch, 1, branches);

            if (refs.Tags.Count > 0)
            {
                Add("TAGS", SidebarKind.SectionHeader, 0, null);
                var tags = Sidebar[^1];
                foreach (var t in refs.Tags)
                    Add(t, SidebarKind.Tag, 1, tags);
            }

            if (refs.Remotes.Count > 0)
            {
                Add("REMOTES", SidebarKind.SectionHeader, 0, null);
                var remotesHeader = Sidebar[^1];

                // Group "origin/main", "origin/dev" -> origin { main, dev }.
                foreach (var group in refs.Remotes
                             .Select(SplitRemote)
                             .GroupBy(x => x.remote, StringComparer.Ordinal)
                             .OrderBy(g => g.Key, StringComparer.Ordinal))
                {
                    Add(group.Key, SidebarKind.Remote, 1, remotesHeader);
                    var remoteNode = Sidebar[^1];
                    foreach (var branch in group.Select(x => x.branch).Where(b => b.Length > 0))
                        Add(branch, SidebarKind.RemoteBranch, 2, remoteNode);
                }
            }

            // Highlight the current branch, if present.
            var current = Sidebar.FirstOrDefault(i => i.Kind == SidebarKind.Branch && i.Text == currentBranch);
            if (current != null) current.IsSelected = true;

            RecomputeVisibility();
        }

        private static (string remote, string branch) SplitRemote(string remoteRef)
        {
            int slash = remoteRef.IndexOf('/');
            return slash < 0
                ? (remoteRef, "")
                : (remoteRef[..slash], remoteRef[(slash + 1)..]);
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
