using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
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

        public ObservableCollection<CommitRow> Commits { get; } = new();
        public ObservableCollection<SidebarItemVM> Sidebar { get; } = new();
        public ObservableCollection<RecentRepository> Recent { get; } = new();

        public string[] BranchFilterOptions { get; } =
            { "All Branches", "Current Branch" };
        public string[] OrderOptions { get; } =
            { "Date Order", "Topological Order", "Reverse Date Order", "Author Date" };

        public MainViewModel()
        {
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
                    SelectedFile = null; // clear the detail/diff while the new commit's files load
                    if (value != null)
                        _ = LoadFilesForAsync(value);
                }
            }
        }

        private ChangedFile? _selectedFile;
        public ChangedFile? SelectedFile
        {
            get => _selectedFile;
            set
            {
                if (Set(ref _selectedFile, value) && value != null)
                    _ = LoadDiffForAsync(value);
            }
        }

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

                // Update recents (CORE-002).
                var recent = await Task.Run(() => RecentRepositoriesStore.Add(repo.ToInfo()));
                Recent.Clear();
                foreach (var r in recent) Recent.Add(r);

                // Sidebar from real refs.
                var refs = await Task.Run(() => repo.ListRefs());
                BuildSidebar(refs, repo.Branch);

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

        /// <summary>Return to the empty/home state (no repository open).</summary>
        public void CloseRepository()
        {
            _repo = null;
            Repository = null;
            _allCommits = new List<CommitRow>();
            Commits.Clear();
            Sidebar.Clear();
            SelectedCommit = null;
            ErrorMessage = null;
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

            // Auto-select the first changed file once, if this commit is still the selected one.
            if (ReferenceEquals(commit, SelectedCommit) && SelectedFile == null)
                SelectedFile = commit.Files.FirstOrDefault();
        }

        private async Task LoadDiffForAsync(ChangedFile file)
        {
            if (_repo == null) return;
            CommitRow? commit = SelectedCommit;
            if (commit == null || file.DiffLoaded) return;
            GitRepository repo = _repo;

            try
            {
                var diff = await Task.Run(() => repo.Diff(commit.FullHash, file.Path));
                if (file.DiffLoaded) return;
                file.Diff.Clear();
                foreach (var d in diff) file.Diff.Add(d);
                file.DiffLoaded = true;
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
