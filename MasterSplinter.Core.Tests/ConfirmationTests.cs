using MasterSplinter.Entrypoint.Git;

namespace MasterSplinter.Core.Tests;

/// <summary>
/// What each confirmation promises the user. These are pinned because the wording *is* the safety
/// feature: "permanently delete" versus "discard" is the difference between an accurate warning and
/// a misleading one, and which button holds the Return key decides what a reflexive Enter does.
/// </summary>
public class ConfirmationTests
{
    // ---- Destructive actions must not default to the destructive button --------------------------

    [Fact]
    public void EveryDestructiveConfirmationDefaultsToCancel()
    {
        Confirmation[] destructive =
        {
            Confirmations.DiscardFile("f.txt", untracked: false),
            Confirmations.DiscardFile("f.txt", untracked: true),
            Confirmations.AmendCommit("subject"),
            Confirmations.DeleteBranch("feature"),
            Confirmations.DeleteBranchAnyway("error: not fully merged"),
            Confirmations.DeleteTag("v1.0"),
            Confirmations.SkipCommit(),
            Confirmations.AbortOperation("merge"),
            Confirmations.ConflictMarkers("f.txt"),
        };

        Assert.All(destructive, c => Assert.False(c.DefaultIsPrimary));
    }

    [Fact]
    public void EveryConfirmationIsFullyPopulated()
    {
        Confirmation[] all =
        {
            Confirmations.DiscardFile("f.txt", false),
            Confirmations.AmendCommit(null),
            Confirmations.SwitchBranch(1, "main"),
            Confirmations.DeleteBranch("b"),
            Confirmations.DeleteBranchAnyway("e"),
            Confirmations.DeleteTag("t"),
            Confirmations.SkipCommit(),
            Confirmations.AbortOperation("rebase"),
            Confirmations.ConflictMarkers("f.txt"),
        };

        Assert.All(all, c =>
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Title));
            Assert.False(string.IsNullOrWhiteSpace(c.Message));
            Assert.False(string.IsNullOrWhiteSpace(c.PrimaryText));
        });
    }

    // ---- Discard: the untracked case is a different promise ---------------------------------------

    [Fact]
    public void DiscardingATrackedFileTalksAboutChanges()
    {
        Confirmation c = Confirmations.DiscardFile("src/a.cs", untracked: false);

        Assert.Equal("Discard changes", c.Title);
        Assert.Equal("Discard", c.PrimaryText);
        Assert.Contains("src/a.cs", c.Message);
        Assert.DoesNotContain("delete", c.Message, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DiscardingAnUntrackedFileSaysItIsDeletedFromDisk()
    {
        // git has no copy of an untracked file, so "discard" here is an unrecoverable delete.
        // Reusing the tracked wording would understate it.
        Confirmation c = Confirmations.DiscardFile("notes.txt", untracked: true);

        Assert.Equal("Delete untracked file", c.Title);
        Assert.Equal("Delete", c.PrimaryText);
        Assert.Contains("permanently delete", c.Message);
        Assert.Contains("not tracked by git", c.Message);
    }

    // ---- Amend names the commit being replaced ----------------------------------------------------

    [Fact]
    public void AmendQuotesTheCommitBeingRewritten()
    {
        Confirmation c = Confirmations.AmendCommit("Fix the parser");

        Assert.Contains("Fix the parser", c.Message);
        Assert.Contains("rewrites history", c.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AmendWithNoHeadSubjectOmitsTheQuoteRatherThanShowingAnEmptyOne(string? subject)
    {
        Confirmation c = Confirmations.AmendCommit(subject);

        Assert.Contains("rewrites history", c.Message);
        Assert.DoesNotContain("Current commit", c.Message);
    }

    // ---- Switch is a heads-up, not a guard --------------------------------------------------------

    [Fact]
    public void SwitchBranchDefaultsToProceedingBecauseGitItselfRefusesWhenUnsafe()
    {
        Assert.True(Confirmations.SwitchBranch(3, "main").DefaultIsPrimary);
    }

    [Theory]
    [InlineData(1, "1 uncommitted change", "carry it across")]
    [InlineData(2, "2 uncommitted changes", "carry them across")]
    public void SwitchBranchAgreesWithItselfOnPlurals(int changes, string count, string pronoun)
    {
        Confirmation c = Confirmations.SwitchBranch(changes, "main");

        Assert.Contains(count, c.Message);
        Assert.Contains(pronoun, c.Message);
        Assert.Contains("main", c.Message);
    }

    // ---- Delete branch: git's own refusal is quoted, not paraphrased -------------------------------

    [Fact]
    public void ForcingABranchDeleteQuotesGitsRefusalVerbatim()
    {
        const string gitSaid = "error: the branch 'feature' is not fully merged";
        Confirmation c = Confirmations.DeleteBranchAnyway(gitSaid);

        Assert.Contains(gitSaid, c.Message);
        Assert.Contains("discards commits", c.Message);
        Assert.Equal("Delete Anyway", c.PrimaryText);
    }

    [Fact]
    public void DeletingATagSaysItIsLocalOnly()
    {
        // Without this the dialog reads far scarier than the action is.
        Confirmation c = Confirmations.DeleteTag("v2.0");

        Assert.Contains("v2.0", c.Message);
        Assert.Contains("this repository only", c.Message);
    }

    // ---- Sequencer ---------------------------------------------------------------------------------

    [Fact]
    public void SkippingSaysTheChangesLandNowhere()
        => Assert.Contains("will not appear anywhere", Confirmations.SkipCommit().Message);

    [Theory]
    [InlineData("merge")]
    [InlineData("rebase")]
    [InlineData("cherry-pick")]
    public void AbortNamesTheOperationAndWarnsAboutLostResolutions(string command)
    {
        Confirmation c = Confirmations.AbortOperation(command);

        Assert.Equal($"Abort {command}", c.Title);
        Assert.Contains(command, c.Message);
        Assert.Contains("already resolved will be lost", c.Message);
    }

    [Fact]
    public void ConflictMarkerWarningNamesTheFileAndTheMarkers()
    {
        Confirmation c = Confirmations.ConflictMarkers("src/a.cs");

        Assert.Contains("src/a.cs", c.Message);
        Assert.Contains("<<<<<<<", c.Message);
        Assert.Contains("would commit them", c.Message);
    }
}
