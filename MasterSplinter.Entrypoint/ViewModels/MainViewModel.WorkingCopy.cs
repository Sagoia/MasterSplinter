using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MasterSplinter.Entrypoint.Git;
using MasterSplinter.Entrypoint.Models;

namespace MasterSplinter.Entrypoint.ViewModels
{
    // The working copy: status groups, staging, discard, and the commit editor.
    public sealed partial class MainViewModel
    {
        // ---- Working copy / file status (STATUS-001/002/005) ------------------------------------

        /// <summary>Grouped status sections (staged / unstaged / untracked) for the working-copy list.</summary>
        public ObservableCollection<ChangedFileGroup> StatusGroups { get; } = new();

        private bool _isWorkingCopyMode;
        public bool IsWorkingCopyMode
        {
            get => _isWorkingCopyMode;
            private set
            {
                if (SetProperty(ref _isWorkingCopyMode, value))
                    OnPropertyChanged(nameof(ShowCommitPane));
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
                OnPropertyChanged(nameof(CanCommit));

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
            set { if (SetProperty(ref _commitSubject, value)) OnPropertyChanged(nameof(CanCommit)); }
        }

        private string _commitBody = "";
        public string CommitBody { get => _commitBody; set => SetProperty(ref _commitBody, value); }

        private bool _isAmend;
        public bool IsAmend
        {
            get => _isAmend;
            set
            {
                if (!SetProperty(ref _isAmend, value)) return;
                OnPropertyChanged(nameof(CanCommit));
                if (value)
                    _ = PrefillAmendMessageAsync();
            }
        }

        private bool _isCommitting;
        public bool IsCommitting
        {
            get => _isCommitting;
            private set { if (SetProperty(ref _isCommitting, value)) OnPropertyChanged(nameof(CanCommit)); }
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


        /// <summary>Stage / unstage / discard.</summary>
        private Task RunStatusMutationAsync(Func<GitRepository, string?> operation)
            => RunGitOperationAsync(operation, RefreshScope.StatusOnly);

        private static IEnumerable<string> PathsOf(ChangedFile file)
        {
            yield return file.Path;
            // A staged rename occupies two index entries; unstaging needs both pathspecs.
            if (!string.IsNullOrEmpty(file.OldPath))
                yield return file.OldPath;
        }
    }
}
