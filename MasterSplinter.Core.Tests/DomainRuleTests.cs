using System;
using MasterSplinter.Entrypoint.Git;
using MasterSplinter.Entrypoint.Infrastructure;
using MasterSplinter.Entrypoint.Models;

namespace MasterSplinter.Core.Tests;

/// <summary>
/// Domain rules that used to live in the workspace code-behind, where they could not be reached
/// without a XamlRoot and a live repository.
/// </summary>
public class RemoteUrlTests
{
    /// <summary>No filesystem: local-path checks answer false unless a test says otherwise.</summary>
    private static string? Validate(string? url, Func<string, bool>? exists = null)
        => RemoteUrl.Validate(url, exists ?? (_ => false));

    [Theory]
    [InlineData("https://github.com/owner/repo.git")]
    [InlineData("http://host/repo")]
    [InlineData("ssh://git@host:22/owner/repo.git")]
    [InlineData("git://host/repo.git")]
    [InlineData("file:///c/repos/thing")]
    public void SchemeUrlsAreAccepted(string url) => Assert.Null(Validate(url));

    [Theory]
    [InlineData("git@github.com:owner/repo.git")]
    [InlineData("user@host:path/to/repo")]
    public void ScpStyleUrlsAreAccepted(string url) => Assert.Null(Validate(url));

    [Fact]
    public void AnExistingLocalPathIsAccepted()
        => Assert.Null(Validate(@"C:\repos\other", exists: _ => true));

    [Fact]
    public void APathThatDoesNotExistIsRejected()
    {
        string? problem = Validate(@"C:\nope");
        Assert.NotNull(problem);
        Assert.Contains("https://host/repo.git", problem);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void BlankIsRejectedWithAShortMessage(string? url)
        => Assert.Equal("Enter a URL.", Validate(url));

    [Fact]
    public void WhitespaceInsideAUrlIsRejected()
        => Assert.Equal("A remote URL cannot contain spaces.",
                        Validate("https://host/my repo.git"));

    [Fact]
    public void AMissingSchemeIsCalledOutSpecifically()
        => Assert.Contains("missing its scheme", Validate("://host/repo")!);

    [Fact]
    public void ASchemeWithNoHostIsCalledOutSpecifically()
        => Assert.Contains("no host", Validate("https://")!);

    [Fact]
    public void SurroundingWhitespaceIsTrimmedRatherThanRejected()
        => Assert.Null(Validate("  https://host/repo.git  "));
}

/// <summary>
/// Tracked and untracked changes are separate categories, and conflating them has bitten twice:
/// untracked files never block a branch switch, but they DO block a fast-forward when an incoming
/// commit adds a file where one already sits. The wording has to match which situation it is.
/// </summary>
public class WorkingTreeWarningTests
{
    [Fact]
    public void TrackedOnlyTalksAboutChangingTheSameFiles()
    {
        string text = WorkingTreeWarning.Describe(tracked: 2, untracked: 0, "main");

        Assert.Contains("2 uncommitted changes", text);
        Assert.DoesNotContain("untracked", text);
        Assert.Contains("change the same files", text);
        Assert.Contains("Commit or stash first", text);
    }

    [Fact]
    public void UntrackedOnlyTalksAboutCollidingPathsAndNeverSaysCommit()
    {
        // Telling someone to "commit or stash" a file git has never tracked is the exact
        // contradiction this split exists to avoid.
        string text = WorkingTreeWarning.Describe(tracked: 0, untracked: 3, "main");

        Assert.Contains("3 untracked files", text);
        Assert.DoesNotContain("uncommitted", text);
        Assert.Contains("add a file where one of yours already sits", text);
        Assert.Contains("Move or delete them first", text);
    }

    [Fact]
    public void BothCategoriesGetBothRisksAndBothFixes()
    {
        string text = WorkingTreeWarning.Describe(tracked: 1, untracked: 1, "main");

        Assert.Contains("1 uncommitted change", text);
        Assert.Contains("1 untracked file", text);
        Assert.Contains("change the same files", text);
        Assert.Contains("untracked files already sits", text);
        Assert.Contains("move the untracked files aside", text);
    }

    [Fact]
    public void TheBranchAndVerbAreNamed()
    {
        string text = WorkingTreeWarning.Describe(1, 0, "feature/x", verb: "switch");
        Assert.Contains("refuse to switch feature/x", text);
    }

    [Theory]
    [InlineData(1, "1 uncommitted change")]
    [InlineData(2, "2 uncommitted changes")]
    [InlineData(0, "0 uncommitted changes")]
    public void CountPluralisesOnTheNumber(int n, string expected)
        => Assert.Equal(expected, WorkingTreeWarning.Count(n, "uncommitted change"));
}

/// <summary>
/// The search banner names the predicate that actually ran. Each mode is exactly one git predicate,
/// so describing it accurately is what keeps an empty result comprehensible.
/// </summary>
public class SearchBannerTests
{
    [Theory]
    [InlineData(SearchMode.Message, "message")]
    [InlineData(SearchMode.Author, "author")]
    [InlineData(SearchMode.Content, "changes containing")]
    [InlineData(SearchMode.Path, "commits touching")]
    [InlineData(SearchMode.Hash, "commit")]
    public void EachModeNamesItsOwnPredicate(SearchMode mode, string expected)
        => Assert.Contains(expected, SearchBanner.Describe(mode, "q", "", 1));

    [Theory]
    [InlineData(1, "1 result ")]
    [InlineData(0, "0 results ")]
    [InlineData(7, "7 results ")]
    public void ResultCountPluralises(int count, string expected)
        => Assert.StartsWith(expected, SearchBanner.Describe(SearchMode.Message, "q", "", count));

    [Fact]
    public void APathFilterNarrowsTheDescription()
    {
        string text = SearchBanner.Describe(SearchMode.Author, "alice", "src/", 3);

        Assert.Contains("author", text);
        Assert.Contains("under", text);
        Assert.Contains("src/", text);
    }

    [Fact]
    public void ABlankQueryWithAPathDescribesWhatActuallyRan()
    {
        // Mode says "author", but with no query this is a path search — say so.
        string text = SearchBanner.Describe(SearchMode.Author, "", "src/", 2);

        Assert.Contains("commits touching", text);
        Assert.Contains("src/", text);
        Assert.DoesNotContain("author", text);
    }
}

/// <summary>Splitting a configured editor command into exe + arguments (STATUS-006).</summary>
public class EditorCommandTests
{
    [Fact]
    public void APlainExeHasNoArguments()
        => Assert.Equal(("notepad.exe", ""), EditorLauncher.SplitCommand("notepad.exe"));

    [Fact]
    public void ArgumentsSplitAtTheFirstSpace()
        => Assert.Equal(("code", "-g {path}"), EditorLauncher.SplitCommand("code -g {path}"));

    [Fact]
    public void AQuotedExePathKeepsItsSpaces()
    {
        // The whole reason quoting is handled: Program Files.
        var (exe, args) = EditorLauncher.SplitCommand("\"C:\\Program Files\\App\\app.exe\" {path}");

        Assert.Equal("C:\\Program Files\\App\\app.exe", exe);
        Assert.Equal("{path}", args);
    }

    [Fact]
    public void AQuotedExeWithNoArgumentsYieldsEmptyArgs()
        => Assert.Equal(("C:\\Program Files\\App\\app.exe", ""),
                        EditorLauncher.SplitCommand("\"C:\\Program Files\\App\\app.exe\""));

    [Fact]
    public void SurroundingWhitespaceIsIgnored()
        => Assert.Equal(("code", "{path}"), EditorLauncher.SplitCommand("   code   {path}   "));

    [Fact]
    public void AnUnclosedQuoteFallsBackToSpaceSplitting()
    {
        // Malformed input must not throw; the worst case is a command git-style tools will reject.
        var (exe, _) = EditorLauncher.SplitCommand("\"C:\\App\\app.exe {path}");
        Assert.StartsWith("\"C:", exe);
    }

    [Fact]
    public void AnEmptyCommandYieldsAnEmptyExe()
        => Assert.Equal(("", ""), EditorLauncher.SplitCommand("   "));
}
