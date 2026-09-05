using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MasterSplinter.Entrypoint.Git;
using CommunityToolkit.Mvvm.ComponentModel;
using MasterSplinter.Entrypoint.Models;

namespace MasterSplinter.Entrypoint.ViewModels
{
    // Remotes and the three network commands, all fronted by the progress dialog.
    public sealed partial class MainViewModel
    {
        // ---- Remotes (Phase 6, REMOTE-001..009) ------------------------------------------------

        // Every command gate depends on repository, loading and busy state, so all of them are
        // raised together whenever one of those inputs moves.

        /// <summary>True while a command fronted by the progress dialog is running (fetch/pull/push,
        /// and the Phase 7 merge/rebase/cherry-pick/revert): the commands that would collide with it
        /// are disabled, so two of them can never overlap on one repository.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanFetch), nameof(CanPull), nameof(CanPush))]
        [NotifyPropertyChangedFor(nameof(CanStartOperation), nameof(CanResolveOperation))]
        [NotifyPropertyChangedFor(nameof(CanSkipOperation), nameof(CanStash), nameof(CanSearch))]
        private bool _isGitBusy;

        /// <summary>The checked-out branch, or empty on a detached HEAD — which is exactly when
        /// pull and push have nothing to act on.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanFetch), nameof(CanPull), nameof(CanPush))]
        [NotifyPropertyChangedFor(nameof(CanStartOperation), nameof(CanResolveOperation))]
        [NotifyPropertyChangedFor(nameof(CanSkipOperation), nameof(CanStash), nameof(CanSearch))]
        private string _currentBranchName = "";

        /// <summary>REMOTE-003/006: the current branch's upstream ("origin/main"), split for the
        /// pull/push defaults. Empty when the branch has never been published.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasUpstream))]
        [NotifyPropertyChangedFor(nameof(CanFetch), nameof(CanPull), nameof(CanPush))]
        [NotifyPropertyChangedFor(nameof(CanStartOperation), nameof(CanResolveOperation))]
        [NotifyPropertyChangedFor(nameof(CanSkipOperation), nameof(CanStash), nameof(CanSearch))]
        private string _upstreamName = "";

        public bool HasUpstream => UpstreamName.Length > 0;

        public string UpstreamRemote { get; private set; } = "";
        public string UpstreamBranch { get; private set; } = "";

        private string _headerTrackText = "";
        /// <summary>REMOTE-003: "↑2 ↓1" / "gone" / "" for the repository header, formatted exactly
        /// like the sidebar's per-branch badge.</summary>
        public string HeaderTrackText
        {
            get => _headerTrackText;
            private set { if (SetProperty(ref _headerTrackText, value)) OnPropertyChanged(nameof(HasHeaderTrack)); }
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


        /// <summary>REMOTE-002/007. <paramref name="progress"/> is reported from a background
        /// thread, so it must have been created on the UI thread.</summary>
        public Task<string?> FetchAsync(string remote, bool allRemotes, bool prune, bool tags,
                                        IProgress<string> progress, CancellationToken token)
            => RunProgressOperationAsync(r => r.Fetch(remote, allRemotes, prune, tags,
                                                   Sink(progress, token)));

        /// <summary>REMOTE-004, fast-forward only, against the current branch's upstream.</summary>
        public Task<string?> PullAsync(IProgress<string> progress, CancellationToken token)
            => RunProgressOperationAsync(r => r.Pull(UpstreamRemote, UpstreamBranch, Sink(progress, token)));

        /// <summary>REMOTE-005/006.</summary>
        public Task<string?> PushAsync(string remote, string branch, bool setUpstream, bool pushTags,
                                       IProgress<string> progress, CancellationToken token)
            => RunProgressOperationAsync(r => r.Push(remote, branch, setUpstream, pushTags,
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
    }
}
