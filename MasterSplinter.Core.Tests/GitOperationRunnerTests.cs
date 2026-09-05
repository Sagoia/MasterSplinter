using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MasterSplinter.Entrypoint.Git;

namespace MasterSplinter.Core.Tests;

/// <summary>
/// The refresh policy. This is the logic that used to live in four near-identical runners inside
/// MainViewModel, where a miscategorisation stayed invisible for two phases.
/// </summary>
public class GitOperationRunnerTests
{
    /// <summary>Records what the runner asked its host to do.</summary>
    private sealed class FakeHost : IGitOperationHost
    {
        public GitRepository? Repository { get; set; }
        public List<string> Calls { get; } = new();
        public List<string> Errors { get; } = new();

        public void ReportError(string message) { Errors.Add(message); Calls.Add("error"); }
        public void ArmWatcherSuppression() => Calls.Add("suppress");
        public void SetBusy(bool busy) => Calls.Add(busy ? "busy:on" : "busy:off");
        public Task RefreshAllAsync() { Calls.Add("refresh:all"); return Task.CompletedTask; }
        public Task RefreshStatusAsync() { Calls.Add("refresh:status"); return Task.CompletedTask; }
    }

    /// <summary>
    /// GitRepository has a private constructor (only Open creates one), and the runner never calls
    /// into it — it just hands the instance to the operation delegate. An uninitialized instance is
    /// therefore enough to exercise the policy with no git, no filesystem and no native DLL.
    /// </summary>
    private static GitRepository FakeRepo()
        => (GitRepository)System.Runtime.CompilerServices.RuntimeHelpers
               .GetUninitializedObject(typeof(GitRepository));

    private static (GitOperationRunner Runner, FakeHost Host) Make()
    {
        var host = new FakeHost { Repository = FakeRepo() };
        return (new GitOperationRunner(host), host);
    }

    // ---- No repository -------------------------------------------------------------------------

    [Fact]
    public async Task WithNoRepositoryNothingRunsAndNothingRefreshes()
    {
        var host = new FakeHost { Repository = null };
        var runner = new GitOperationRunner(host);
        bool ran = false;

        string? error = await runner.RunAsync(_ => { ran = true; return null; }, RefreshScope.FullAlways);

        Assert.Equal("No repository is open.", error);
        Assert.False(ran);
        Assert.Empty(host.Calls);
    }

    // ---- StatusOnly ----------------------------------------------------------------------------

    [Fact]
    public async Task StatusOnlyArmsSuppressionAndReloadsOnlyTheStatusList()
    {
        var (runner, host) = Make();

        await runner.RunAsync(_ => null, RefreshScope.StatusOnly);

        Assert.Equal(new[] { "suppress", "refresh:status" }, host.Calls);
    }

    [Fact]
    public async Task StatusOnlyStillReloadsStatusWhenTheOperationFailed()
    {
        var (runner, host) = Make();

        string? error = await runner.RunAsync(_ => "git add failed", RefreshScope.StatusOnly);

        Assert.Equal("git add failed", error);
        Assert.Contains("refresh:status", host.Calls);
        Assert.DoesNotContain("refresh:all", host.Calls);
    }

    // ---- FullOnSuccess -------------------------------------------------------------------------

    [Fact]
    public async Task FullOnSuccessRefreshesEverythingAndNeverArmsSuppression()
    {
        var (runner, host) = Make();

        await runner.RunAsync(_ => null, RefreshScope.FullOnSuccess);

        // Arming the window here would swallow exactly the log/sidebar refresh a ref write needs.
        Assert.DoesNotContain("suppress", host.Calls);
        Assert.Equal(new[] { "refresh:all" }, host.Calls);
    }

    [Fact]
    public async Task FullOnSuccessSkipsTheRefreshWhenNothingChanged()
    {
        var (runner, host) = Make();

        string? error = await runner.RunAsync(_ => "error: branch not found", RefreshScope.FullOnSuccess);

        Assert.Equal("error: branch not found", error);
        Assert.DoesNotContain("refresh:all", host.Calls);
        Assert.DoesNotContain("refresh:status", host.Calls);
    }

    // ---- FullAlways ----------------------------------------------------------------------------

    [Fact]
    public async Task FullAlwaysRefreshesEvenWhenTheOperationFailed()
    {
        // REGRESSION: a conflicting stash pop writes the markers, leaves the file unmerged, keeps
        // the entry, and exits non-zero. Treating that as "nothing changed" left the UI showing a
        // working tree that no longer existed. Same for a fetch that updates some refs then errors,
        // and for a merge that stops on a conflict.
        var (runner, host) = Make();

        string? error = await runner.RunAsync(_ => "CONFLICT (content): Merge conflict in f.txt",
                                              RefreshScope.FullAlways);

        Assert.Equal("CONFLICT (content): Merge conflict in f.txt", error);
        Assert.Contains("refresh:all", host.Calls);
    }

    // ---- Error reporting -----------------------------------------------------------------------

    [Fact]
    public async Task ErrorsReachTheInfoBarByDefault()
    {
        var (runner, host) = Make();

        await runner.RunAsync(_ => "boom", RefreshScope.FullOnSuccess);

        Assert.Equal(new[] { "boom" }, host.Errors);
    }

    [Fact]
    public async Task ProgressFrontedOperationsKeepErrorsOutOfTheInfoBar()
    {
        // The progress dialog is already showing git's output; duplicating it there is noise.
        var (runner, host) = Make();

        string? error = await runner.RunAsync(_ => "boom", RefreshScope.FullAlways, reportError: false);

        Assert.Equal("boom", error);
        Assert.Empty(host.Errors);
    }

    // ---- Busy flag -----------------------------------------------------------------------------

    [Fact]
    public async Task BusyIsRaisedAndClearedAroundTheOperation()
    {
        var (runner, host) = Make();

        await runner.RunAsync(_ => null, RefreshScope.FullAlways, raiseBusy: true);

        Assert.Equal("busy:on", host.Calls[0]);
        Assert.Equal("busy:off", host.Calls[^1]);
    }

    [Fact]
    public async Task BusyIsClearedEvenWhenTheOperationThrows()
    {
        var (runner, host) = Make();

        string? error = await runner.RunAsync(
            GitOperationRunnerTests.Throw, RefreshScope.FullAlways, raiseBusy: true);

        Assert.Equal("kaboom", error);
        Assert.Equal("busy:off", host.Calls[^1]);
    }

    [Fact]
    public async Task AThrownExceptionIsReportedRatherThanEscaping()
    {
        var (runner, host) = Make();

        string? error = await runner.RunAsync(GitOperationRunnerTests.Throw, RefreshScope.FullOnSuccess);

        Assert.Equal("kaboom", error);
        Assert.Equal(new[] { "kaboom" }, host.Errors);
        Assert.DoesNotContain("refresh:all", host.Calls);   // a throw changed nothing
    }

    [Fact]
    public async Task BusyIsNotTouchedWhenNotRequested()
    {
        var (runner, host) = Make();

        await runner.RunAsync(_ => null, RefreshScope.FullOnSuccess);

        Assert.DoesNotContain("busy:on", host.Calls);
        Assert.DoesNotContain("busy:off", host.Calls);
    }

    private static string? Throw(GitRepository repo) => throw new InvalidOperationException("kaboom");
}
