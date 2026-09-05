using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MasterSplinter.Entrypoint.Git;
using MasterSplinter.Entrypoint.Models;

namespace MasterSplinter.Entrypoint.ViewModels
{
    // Merge, rebase, cherry-pick, revert and the continue/skip/abort half of each.
    public sealed partial class MainViewModel
    {
        // ---- Merge / rebase / cherry-pick / revert (Phase 7) -----------------------------------

        private RepositoryState _state = RepositoryState.None;
        /// <summary>What git is in the middle of. Drives the state banner (MERGE-002/003,
        /// REBASE-002) and is re-read after every operation and every repository refresh.</summary>
        public RepositoryState State
        {
            get => _state;
            private set
            {
                if (!SetProperty(ref _state, value)) return;
                OnPropertyChanged(nameof(HasActiveOperation));
                OnPropertyChanged(nameof(StateTitle));
                OnPropertyChanged(nameof(StateDetail));
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
                if (!SetProperty(ref _conflictCount, value)) return;
                OnPropertyChanged(nameof(HasConflicts));
                OnPropertyChanged(nameof(StateDetail));
                OnPropertyChanged(nameof(CanCommit));
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
        /// <param name="prefetched">
        /// A state already read as part of a batch, so a refresh does not spend a second spawn on
        /// what it has just fetched. Null means read it now.
        /// </param>
        private async Task LoadStateAsync(RepositoryState? prefetched = null)
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
                RepositoryState state = prefetched ?? await Task.Run(() => repo.State());
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
        // Shares RunProgressOperationAsync with the network commands: the two runners this replaces
        // had byte-identical bodies, and the reasoning above is why — both refresh even on failure.

        /// <summary>MERGE-001.</summary>
        public Task<string?> MergeAsync(string refName, bool noFastForward, bool noCommit,
                                        IProgress<string> progress, CancellationToken token)
            => RunProgressOperationAsync(r => r.Merge(refName, noFastForward, noCommit, Sink(progress, token)));

        /// <summary>REBASE-001.</summary>
        public Task<string?> RebaseAsync(string upstream, IProgress<string> progress,
                                         CancellationToken token)
            => RunProgressOperationAsync(r => r.Rebase(upstream, Sink(progress, token)));

        /// <summary>CHERRY-001/002. <paramref name="commits"/> may arrive in any order; they are
        /// applied oldest-first.</summary>
        public Task<string?> CherryPickAsync(IReadOnlyList<CommitRow> commits, bool noCommit,
                                             IProgress<string> progress, CancellationToken token)
        {
            var shas = OrderForCherryPick(commits).Select(c => c.FullHash).ToList();
            return shas.Count == 0
                ? Task.FromResult<string?>("No commits were selected.")
                : RunProgressOperationAsync(r => r.CherryPick(shas, noCommit, Sink(progress, token)));
        }

        /// <summary>REVERT-001. <paramref name="mainline"/> is 1-based; 0 for a non-merge commit.</summary>
        public Task<string?> RevertAsync(string sha, int mainline, bool noCommit,
                                         IProgress<string> progress, CancellationToken token)
            => RunProgressOperationAsync(r => r.Revert(sha, mainline, noCommit, Sink(progress, token)));

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
            return RunProgressOperationAsync(r => r.SequencerAction(command, action, Sink(progress, token)));
        }

        /// <summary>MERGE-004. Git launches the tool and stages the file itself when it exits
        /// cleanly, so this refreshes like any other status mutation.</summary>
        public Task<string?> RunMergeToolAsync(ChangedFile file, string tool,
                                               IProgress<string> progress, CancellationToken token)
            => RunProgressOperationAsync(r => r.MergeTool(file.Path, tool, Sink(progress, token)));

        /// <summary>Marks a conflicted file resolved: staging it is exactly what
        /// "git add &lt;file&gt;" means once the markers are gone, and it is what git's own advice
        /// tells the user to do.</summary>
        public Task MarkResolvedAsync(ChangedFile file)
            => RunStatusMutationAsync(r => r.StagePaths(new[] { file.Path }));

        /// <summary>
        /// The selected commits in the order git will apply them — public so the confirmation
        /// dialog can show exactly that order rather than the order they happen to appear in.
        /// </summary>
        /// <summary>CHERRY-002: the selection in the order git must apply it. The ordering rule
        /// lives in Core so it can be tested without a view model; see CommitOrdering.</summary>
        public IReadOnlyList<CommitRow> OrderForCherryPick(IReadOnlyList<CommitRow> commits)
            => CommitOrdering.OldestFirst(_allCommits, commits, logIsOldestFirst: SelectedOrderIndex == 2);
    }
}
