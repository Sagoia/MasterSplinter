using System;
using System.Threading.Tasks;

namespace MasterSplinter.Entrypoint.Git
{
    /// <summary>
    /// What a git write reloads once it finishes.
    /// <para>
    /// The test for which scope an operation belongs in is <b>does a failure leave state behind?</b>
    /// Checkout, branch, tag, stash-save and stash-drop all fail without changing anything. Stash
    /// apply/pop do not — a conflicting pop writes the markers, leaves the file unmerged, keeps the
    /// entry, and still exits non-zero — so they belong with the sequencer commands.
    /// </para>
    /// </summary>
    public enum RefreshScope
    {
        /// <summary>Index-only writes (stage / unstage / discard). Reload the status list, and arm
        /// the watcher-suppression window so the echo does not also reload the log.</summary>
        StatusOnly,

        /// <summary>Ref-moving writes (checkout / branch / tag, stash save / drop). Full refresh,
        /// because the log, decoration badges and sidebar all have to rebuild — which is exactly why
        /// these must NOT arm the suppression window. Skipped when the operation failed.</summary>
        FullOnSuccess,

        /// <summary>Network, sequencer and stash apply/pop. Full refresh <b>even on failure</b>: a
        /// fetch can update some refs and still error, and a conflict leaves the repository
        /// mid-operation, so skipping the refresh would show a repository that no longer exists.
        /// </summary>
        FullAlways,
    }

    /// <summary>What <see cref="GitOperationRunner"/> needs from its host, so the policy can be
    /// exercised without a view model, a dispatcher, or a real repository.</summary>
    public interface IGitOperationHost
    {
        /// <summary>The open repository, or null when none is.</summary>
        GitRepository? Repository { get; }

        /// <summary>Surface a failure to the user (the InfoBar).</summary>
        void ReportError(string message);

        /// <summary>Start the window during which a watcher `repoDirty` event is downgraded to a
        /// status-only reload.</summary>
        void ArmWatcherSuppression();

        /// <summary>Gate the toolbar commands for the duration of a long-running operation.</summary>
        void SetBusy(bool busy);

        /// <summary>Reload everything: log, refs, sidebar, status.</summary>
        Task RefreshAllAsync();

        /// <summary>Reload just the working-tree status list.</summary>
        Task RefreshStatusAsync();
    }

    /// <summary>
    /// Runs a git write off the UI thread and reloads according to its <see cref="RefreshScope"/>.
    /// <para>
    /// This replaced four hand-written runners that differed only in refresh policy — two of which
    /// were byte-for-byte identical. Collapsing them into one named policy is what made the stash
    /// apply/pop miscategorisation visible; it had been invisible while spread across near-copies.
    /// </para>
    /// </summary>
    public sealed class GitOperationRunner
    {
        private readonly IGitOperationHost _host;

        public GitOperationRunner(IGitOperationHost host)
            => _host = host ?? throw new ArgumentNullException(nameof(host));

        /// <summary>
        /// Returns git's error text, or null on success.
        /// </summary>
        /// <param name="reportError">Surface failures to the InfoBar. False for operations fronted
        /// by the progress dialog — it is already showing git's output, and a CONFLICT is not an
        /// error bar's business.</param>
        /// <param name="raiseBusy">Hold the busy flag for the duration.</param>
        public async Task<string?> RunAsync(Func<GitRepository, string?> operation,
                                            RefreshScope scope,
                                            bool reportError = true,
                                            bool raiseBusy = false)
        {
            GitRepository? repo = _host.Repository;
            if (repo == null)
                return "No repository is open.";

            if (scope == RefreshScope.StatusOnly)
                _host.ArmWatcherSuppression();
            if (raiseBusy)
                _host.SetBusy(true);
            try
            {
                string? error = await Task.Run(() => operation(repo)).ConfigureAwait(true);
                if (error != null && reportError)
                    _host.ReportError(error);

                if (scope == RefreshScope.StatusOnly)
                    await _host.RefreshStatusAsync().ConfigureAwait(true);
                else if (scope == RefreshScope.FullAlways || error == null)
                    await _host.RefreshAllAsync().ConfigureAwait(true);

                return error;
            }
            catch (Exception ex)
            {
                if (reportError)
                    _host.ReportError(ex.Message);
                return ex.Message;
            }
            finally
            {
                if (raiseBusy)
                    _host.SetBusy(false);
            }
        }
    }
}
