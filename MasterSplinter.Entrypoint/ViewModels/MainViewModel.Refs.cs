using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MasterSplinter.Entrypoint.Git;
using MasterSplinter.Entrypoint.Models;

namespace MasterSplinter.Entrypoint.ViewModels
{
    // Branch, tag and stash mutations. All go through RefreshScope.FullOnSuccess.
    public sealed partial class MainViewModel
    {
        // ---- Branches & tags (Phase 5, BR-001..007 / TAG-001..003) -----------------------------

        /// <summary>Shared plumbing for ref-changing operations (checkout / branch / tag).
        /// Unlike <see cref="RunStatusMutationAsync"/> this deliberately does NOT arm
        /// _suppressRepoRefreshUntil: that window exists to downgrade a repoDirty event to a
        /// status-only reload for writes that touch only .git\index. These commands move HEAD and
        /// refs, so the log, the decoration badges and the sidebar all have to be rebuilt — arming
        /// the window would swallow exactly the refresh we need. Like CommitAsync we refresh
        /// explicitly and let the IsLoading guard in OnRepositoryChanged absorb the watcher echo.
        /// Returns git's error text, or null on success.</summary>
        private Task<string?> RunRefMutationAsync(Func<GitRepository, string?> operation,
                                                  bool reportError = true)
            => RunGitOperationAsync(operation, RefreshScope.FullOnSuccess, reportError);

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

        /// <summary>
        /// STASH-002. The entry survives; a conflict reaches the caller as git wrote it.
        /// <para>
        /// FullAlways, not FullOnSuccess: a conflicting apply/pop <b>changes the working tree and
        /// still exits non-zero</b> — verified on git 2.54, which writes the conflict markers,
        /// leaves the file unmerged (<c>UU</c>), keeps the stash entry, and returns 1. Treating
        /// that as "nothing changed" left the app showing a working tree that no longer existed
        /// until the file watcher happened to catch up. Same reasoning as the Phase 7 sequencer
        /// commands: a conflict is a normal outcome that leaves real state behind.
        /// </para>
        /// </summary>
        public Task<string?> ApplyStashAsync(string selector, bool reportError = true)
            => RunGitOperationAsync(r => r.StashApply(selector), RefreshScope.FullAlways, reportError);

        /// <summary>STASH-003. On conflict git keeps the entry, so nothing is lost either way.
        /// FullAlways for the reason spelled out on <see cref="ApplyStashAsync"/>.</summary>
        public Task<string?> PopStashAsync(string selector, bool reportError = true)
            => RunGitOperationAsync(r => r.StashPop(selector), RefreshScope.FullAlways, reportError);

        /// <summary>STASH-004. Irreversible — callers must confirm first.</summary>
        public Task<string?> DropStashAsync(string selector)
            => RunRefMutationAsync(r => r.StashDrop(selector));
    }
}
