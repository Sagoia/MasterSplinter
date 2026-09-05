using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using MasterSplinter.Entrypoint.Git;
using MasterSplinter.Entrypoint.Interop;
using MasterSplinter.Entrypoint.Models;

namespace MasterSplinter.Core.Tests;

/// <summary>
/// The whole stack below the UI, for real: native DLL, git.exe, the C ABI, and the parsers, against
/// a scratch repository built by this fixture.
/// <para>
/// The unit tests feed the parsers hand-written samples, which proves they parse what we *think*
/// git emits. This proves git actually emits it — and that the P/Invoke still resolves now that
/// NativeLogic lives in its own assembly.
/// </para>
/// <para>
/// Skips itself (rather than failing) when git or the native DLL is unavailable, so a machine
/// without them still gets a green unit run.
/// </para>
/// </summary>
public sealed class ScratchRepo : IDisposable
{
    public string Path { get; }
    public bool Usable { get; }
    public string? SkipReason { get; }

    public ScratchRepo()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                                      "ms-e2e-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            // Touching the ABI is what proves the native DLL resolves from this assembly.
            if (!NativeCore.Initialize())
            {
                SkipReason = "native core failed to initialize";
                return;
            }
            Directory.CreateDirectory(Path);
            Build();
            Usable = true;
        }
        catch (Exception ex)
        {
            SkipReason = ex.GetType().Name + ": " + ex.Message;
        }
    }

    private void Git(string args)
    {
        var psi = new ProcessStartInfo("git", args)
        {
            WorkingDirectory = Path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using Process p = Process.Start(psi) ?? throw new InvalidOperationException("git did not start");
        p.WaitForExit();
        // `stash pop` on a conflict exits non-zero on purpose; callers that care check the state.
    }

    /// <summary>
    /// A repository with the shapes the parsers actually have to handle: a root commit, a rename,
    /// a merge (two parents), a tag, a branch, and a dirty working tree with staged, unstaged and
    /// untracked entries.
    /// </summary>
    private void Build()
    {
        Git("init -q -b main");
        Git("config user.email test@example.com");
        Git("config user.name \"Test User\"");
        Git("config commit.gpgsign false");

        File.WriteAllText(System.IO.Path.Combine(Path, "a.txt"), "one\ntwo\nthree\n");
        Git("add .");
        Git("commit -q -m \"first commit\"");
        Git("tag v1.0");

        Git("switch -q -c feature");
        File.WriteAllText(System.IO.Path.Combine(Path, "b.txt"), "feature\n");
        Git("add .");
        Git("commit -q -m \"add b on feature\"");

        Git("switch -q main");
        File.WriteAllText(System.IO.Path.Combine(Path, "a.txt"), "one\nCHANGED\nthree\n");
        Git("add .");
        Git("commit -q -m \"change a on main\"");

        Git("merge --no-ff --no-edit -q feature");   // a real two-parent merge

        Git("mv a.txt renamed.txt");
        Git("commit -q -m \"rename a to renamed\"");

        // Dirty tree: one staged, one unstaged, one untracked.
        File.WriteAllText(System.IO.Path.Combine(Path, "staged.txt"), "staged\n");
        Git("add staged.txt");
        File.AppendAllText(System.IO.Path.Combine(Path, "renamed.txt"), "unstaged edit\n");
        File.WriteAllText(System.IO.Path.Combine(Path, "untracked.txt"), "untracked\n");
    }

    public void Dispose()
    {
        try
        {
            // git marks objects read-only; clear it or the delete fails.
            foreach (string f in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(Path, recursive: true);
        }
        catch { /* a leftover temp dir is not worth failing a test run over */ }
    }
}

[Collection("e2e")]
public class EndToEndSmokeTests : IClassFixture<ScratchRepo>
{
    private readonly ScratchRepo _repo;
    public EndToEndSmokeTests(ScratchRepo repo) => _repo = repo;

    private GitRepository Open()
    {
        Assert.True(_repo.Usable, "scratch repo unavailable: " + _repo.SkipReason);
        GitRepository? git = GitRepository.Open(_repo.Path, out string? error);
        Assert.Null(error);
        Assert.NotNull(git);
        return git!;
    }

    [Fact]
    public void TheNativeCoreLoadsAndReportsAVersion()
    {
        Assert.True(_repo.Usable, "scratch repo unavailable: " + _repo.SkipReason);
        Assert.False(string.IsNullOrWhiteSpace(NativeCore.Version()));
        Assert.Equal(42, NativeCore.Add(40, 2));   // the interop round-trip itself
    }

    [Fact]
    public void OpeningReportsTheRootAndBranch()
    {
        GitRepository git = Open();
        Assert.Equal("main", git.Branch);
        Assert.False(string.IsNullOrWhiteSpace(git.RootPath));
    }

    [Fact]
    public void OpeningANonRepositoryFails()
    {
        string empty = Path.Combine(Path.GetTempPath(), "ms-not-a-repo-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(empty);
        try
        {
            Assert.Null(GitRepository.Open(empty, out string? error));
            Assert.False(string.IsNullOrWhiteSpace(error));
        }
        finally { Directory.Delete(empty, true); }
    }

    [Fact]
    public void TheLogParsesIntoCommitsWithParents()
    {
        IReadOnlyList<CommitRow> log = Open().Log(order: 0, maxCount: 100);

        Assert.NotEmpty(log);
        Assert.All(log, c => Assert.False(string.IsNullOrWhiteSpace(c.FullHash)));
        Assert.All(log, c => Assert.False(string.IsNullOrWhiteSpace(c.Author)));
        Assert.Contains(log, c => c.Message == "first commit");

        // The root commit has no parents; the merge has two. Both shapes must survive the round trip.
        Assert.Contains(log, c => c.ParentHashes.Length == 0);
        Assert.Contains(log, c => c.ParentHashes.Length == 2);
    }

    [Fact]
    public void TheCommitGraphIsLaidOutOverRealHistory()
    {
        // The fixture has a real two-parent merge and a branch, so the lanes must actually
        // branch. Asserted against real git rather than a hand-built adjacency list, which is
        // the only way to catch the graph being laid out over the wrong rows.
        IReadOnlyList<CommitRow> log = Open().Log(order: 1, maxCount: 100);

        Assert.NotEmpty(log);
        Assert.All(log, c => Assert.NotNull(c.Graph.Dot));
        Assert.All(log, c => Assert.True(c.Graph.LaneCount >= 1));

        // The merge widens the graph: at least one row has to use a second lane.
        Assert.Contains(log, c => c.Graph.LaneCount > 1);

        // And at least one row draws a diagonal -- a line that changes lane between the top and
        // the bottom of its row is what a fork or a join looks like.
        Assert.Contains(log, c => c.Graph.Lines.Exists(l => l.X1 != l.X2));

        // Every segment stays inside its row's declared width, which is what the renderer bets on.
        foreach (CommitRow c in log)
        {
            Assert.True(c.Graph.Dot!.Lane < c.Graph.LaneCount);
            foreach (GraphLine l in c.Graph.Lines)
            {
                Assert.InRange(l.X1, 0, c.Graph.LaneCount - 1);
                Assert.InRange(l.X2, 0, c.Graph.LaneCount - 1);
                Assert.InRange(l.Y1, 0.0, 1.0);
                Assert.InRange(l.Y2, 0.0, 1.0);
            }
        }
    }

    [Fact]
    public void RefsIncludeTheBranchesAndTheTag()
    {
        GitRepository.RefList refs = Open().ListRefs();

        Assert.Contains(refs.Branches, b => b.Name == "main");
        Assert.Contains(refs.Branches, b => b.Name == "feature");
        Assert.Contains(refs.Tags, t => t.Name == "v1.0");
        Assert.Contains(refs.Branches, b => b.IsCurrent);   // %(HEAD) actually resolved
    }

    [Fact]
    public void StatusSplitsStagedUnstagedAndUntracked()
    {
        GitRepository.WorkTreeStatus status = Open().Status();

        Assert.Contains(status.Staged, f => f.Path == "staged.txt");
        Assert.Contains(status.Unstaged, f => f.Path == "renamed.txt");
        Assert.Contains(status.Untracked, f => f.Path == "untracked.txt");
        Assert.Empty(status.Conflicted);
    }

    [Fact]
    public void AWorkingTreeDiffParsesWithNoPhantomTrailingLine()
    {
        // The bug the unit tests caught: every diff used to end with a blank context row carrying a
        // line number. Asserted here against REAL git output, not a hand-written sample.
        var (lines, isBinary) = Open().WorkTreeDiff("renamed.txt", WorkTreeArea.Unstaged,
                                                    WhitespaceMode.None);

        Assert.False(isBinary);
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.Kind == DiffLineKind.Hunk);
        Assert.Contains(lines, l => l.Kind == DiffLineKind.Added && l.Text == "unstaged edit");
        Assert.False(lines[^1].Kind == DiffLineKind.Context && lines[^1].Text.Length == 0);
    }

    [Fact]
    public void ACommitDiffParses()
    {
        GitRepository git = Open();
        CommitRow commit = git.Log(0, 100).First(c => c.Message == "change a on main");

        IReadOnlyList<ChangedFile> files = git.ChangedFiles(commit.FullHash);
        Assert.Contains(files, f => f.Path == "a.txt");

        var (lines, _) = git.FileDiff(commit.FullHash, "a.txt", WhitespaceMode.None);
        Assert.Contains(lines, l => l.Kind == DiffLineKind.Added && l.Text == "CHANGED");
        Assert.Contains(lines, l => l.Kind == DiffLineKind.Removed && l.Text == "two");
    }

    [Fact]
    public void ARenameIsReportedAgainstTheNewPath()
    {
        GitRepository git = Open();
        CommitRow commit = git.Log(0, 100).First(c => c.Message == "rename a to renamed");

        IReadOnlyList<ChangedFile> files = git.ChangedFiles(commit.FullHash);

        Assert.Contains(files, f => f.Status == FileChangeStatus.Renamed && f.Path == "renamed.txt");
    }

    [Fact]
    public void ShortStatCountsTheCommit()
    {
        GitRepository git = Open();
        CommitRow commit = git.Log(0, 100).First(c => c.Message == "change a on main");

        DiffStat stat = git.CommitStat(commit.FullHash);

        Assert.False(stat.IsEmpty);
        Assert.Equal(1, stat.Files);
    }

    [Fact]
    public void BlameAttributesLinesToRealCommits()
    {
        IReadOnlyList<BlameLine> lines = Open().Blame("HEAD", "renamed.txt", ignoreWhitespace: false,
                                             BlameMoveDetection.None, out string? error);

        Assert.Null(error);
        Assert.NotEmpty(lines);
        Assert.All(lines, l => Assert.False(string.IsNullOrWhiteSpace(l.Sha)));
        // The per-sha header cache: every line must carry an author, not just the group starts.
        Assert.All(lines, l => Assert.False(string.IsNullOrWhiteSpace(l.Author)));
    }

    [Fact]
    public void ACommitMessageContainingTheSeparatorBytesRoundTripsIntact()
    {
        // The bug Phase D's log format exists to kill, proven against real git rather than against
        // a hand-written sample. Under the old format (records ended with %x1e, message split
        // across %s and %b) this commit came back with a truncated subject, a body holding the
        // subject's tail, and a phantom half-record that the field-count floor then dropped.
        //
        // Its own repository, not the shared fixture: a commit this odd should not be able to
        // perturb what every other test reads.
        Assert.True(_repo.Usable, "scratch repo unavailable: " + _repo.SkipReason);

        string dir = Path.Combine(Path.GetTempPath(), "ms-sep-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            void Run(string args)
            {
                var psi = new ProcessStartInfo("git", args)
                {
                    WorkingDirectory = dir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                using Process p = Process.Start(psi) ?? throw new InvalidOperationException("git did not start");
                p.WaitForExit();
            }

            Run("init -q -b main");
            Run("config user.email test@example.com");
            Run("config user.name \"Test User\"");
            Run("config commit.gpgsign false");
            File.WriteAllText(Path.Combine(dir, "f.txt"), "x\n");

            // 0x1F in the subject AND 0x1E in the body -- the two failures are independent.
            const string subject = "sub\u001fject line";
            const string body = "body\u001etail";
            string messageFile = Path.Combine(dir, "msg.txt");
            File.WriteAllText(messageFile, subject + "\n\n" + body + "\n");

            Run("add f.txt");
            Run("commit -q -F msg.txt");

            GitRepository? git = GitRepository.Open(dir, out string? error);
            Assert.Null(error);
            Assert.NotNull(git);

            CommitRow row = Assert.Single(git!.Log(order: 0, maxCount: 10));
            Assert.Equal(subject, row.Message);
            Assert.Equal(body, row.Body);
        }
        finally
        {
            try
            {
                foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(dir, recursive: true);
            }
            catch { /* a leftover temp dir is not worth failing a test run over */ }
        }
    }

    [Fact]
    public void SearchFindsACommitByMessage()
    {
        IReadOnlyList<CommitRow> hits = Open().SearchLog(
            SearchMode.Message, "first commit", pathFilter: "", order: 0, maxCount: 50,
            matchCase: false, useRegex: false, allBranches: true);

        Assert.Contains(hits, c => c.Message == "first commit");
    }

    [Fact]
    public void TheStashListParsesAgainstRealGit()
    {
        // D5 moved stash parsing into the native core and changed its git format (-z, the
        // free-form %gs last, the offset off %aI). The shared fixture has no stash and adding one
        // would disturb the dirty tree every other test reads, so this builds its own repository.
        Assert.True(_repo.Usable, "scratch repo unavailable: " + _repo.SkipReason);

        string dir = Path.Combine(Path.GetTempPath(), "ms-stash-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            void Run(string args)
            {
                var psi = new ProcessStartInfo("git", args)
                {
                    WorkingDirectory = dir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                using Process p = Process.Start(psi) ?? throw new InvalidOperationException("git did not start");
                p.WaitForExit();
            }

            Run("init -q -b main");
            Run("config user.email test@example.com");
            Run("config user.name \"Test User\"");
            Run("config commit.gpgsign false");
            File.WriteAllText(Path.Combine(dir, "f.txt"), "one\n");
            Run("add f.txt");
            Run("commit -q -m base");

            // Two stashes: one with git's composed "WIP on <branch>" subject, one with a message
            // the user supplied, so both halves of the subject split are exercised.
            File.WriteAllText(Path.Combine(dir, "f.txt"), "two\n");
            Run("stash push");
            File.WriteAllText(Path.Combine(dir, "f.txt"), "three\n");
            Run("stash push -m \"my own message\"");

            GitRepository? git = GitRepository.Open(dir, out string? error);
            Assert.Null(error);
            Assert.NotNull(git);

            IReadOnlyList<StashEntry> stashes = git!.ListStashes();
            Assert.Equal(2, stashes.Count);

            // Newest first, and the selectors are positional.
            Assert.Equal("stash@{0}", stashes[0].Selector);
            Assert.Equal("stash@{1}", stashes[1].Selector);
            Assert.Equal(0, stashes[0].Index);
            Assert.Equal(1, stashes[1].Index);

            // The user-supplied message keeps its own text; git's composed one yields a branch.
            Assert.Contains(stashes, e => e.Message == "my own message");
            Assert.Contains(stashes, e => e.Branch == "main");

            Assert.All(stashes, e => Assert.False(string.IsNullOrWhiteSpace(e.Sha)));
            Assert.All(stashes, e => Assert.NotEqual(default, e.When));
        }
        finally
        {
            try
            {
                foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(dir, recursive: true);
            }
            catch { /* a leftover temp dir is not worth failing a test run over */ }
        }
    }

    [Fact]
    public void TheReflogIsReadable()
    {
        IReadOnlyList<ReflogEntry> entries = Open().Reflog("HEAD", 50);

        Assert.NotEmpty(entries);
        Assert.All(entries, e => Assert.False(string.IsNullOrWhiteSpace(e.Selector)));
    }

    [Fact]
    public void RepositoryStateIsCleanOnAHealthyRepo()
    {
        RepositoryState state = Open().State();

        Assert.Equal(RepoOperation.None, state.Op);
        Assert.False(state.IsActive);
    }
}
