using System.Collections.Generic;
using MasterSplinter.Entrypoint.Git;
using MasterSplinter.Entrypoint.Models;

namespace MasterSplinter.Core.Tests;

/// <summary>
/// The record-shaped reads: the 12-field commit format (shared by Log and SearchLog), name-status,
/// and --shortstat. Separators are written \u001f / \u001e, never \x1f — C# hex escapes are greedy.
/// </summary>
public class RecordParserTests
{
    private const string US = "\u001f";
    private const string RS = "\u001e";

    /// <summary>One commit record in the exact field order MsGitLog emits.</summary>
    private static string Record(string parents, string decorations, string subject)
        => string.Join(US,
               "a1b2c3d4e5f6a7b8c9d0", "a1b2c3d", parents,
               "Alice", "a@a", "2026-01-02T03:04:05+00:00",
               "Bob", "b@b", "2026-01-02T04:05:06+00:00",
               decorations, subject, "Body text") + RS;

    private static string Sample(string parents = "", string decorations = "", string subject = "Subject")
        => Record(parents, decorations, subject);

    [Fact]
    public void AllTwelveFieldsLandInTheRightPlaces()
    {
        CommitRow row = Assert.Single(GitRepository.ParseCommitRecords(Sample()));

        Assert.Equal("a1b2c3d4e5f6a7b8c9d0", row.FullHash);
        Assert.Equal("a1b2c3d", row.Hash);
        Assert.Equal("Alice", row.Author);
        Assert.Equal("a@a", row.AuthorEmail);
        Assert.Equal("Bob", row.Committer);
        Assert.Equal("b@b", row.CommitterEmail);
        Assert.Equal("Subject", row.Message);
        Assert.Equal("Body text", row.Body);
    }

    [Fact]
    public void RecordsShorterThanTwelveFieldsAreDropped()
    {
        // The field-count floor is what keeps git's error text off the commit list when a read
        // ignores the exit code.
        Assert.Empty(GitRepository.ParseCommitRecords(
            "fatal: your current branch does not have any commits yet" + RS));
    }

    [Fact]
    public void MultipleRecordsAreSeparatedByRs()
    {
        List<CommitRow> rows = GitRepository.ParseCommitRecords(
            Sample(subject: "first") + Sample(subject: "second"));

        Assert.Equal(2, rows.Count);
        Assert.Equal("first", rows[0].Message);
        Assert.Equal("second", rows[1].Message);
    }

    [Fact]
    public void ParentsAreSplitOnSpaces()
    {
        CommitRow row = Assert.Single(GitRepository.ParseCommitRecords(
            Sample(parents: "1111111111 2222222222")));

        Assert.Equal(2, row.ParentHashes.Length);
        Assert.Equal("1111111111", row.ParentHashes[0]);
        Assert.Equal("2222222222", row.ParentHashes[1]);
    }

    [Fact]
    public void ARootCommitHasNoParents()
        => Assert.Empty(Assert.Single(GitRepository.ParseCommitRecords(Sample())).ParentHashes);

    [Fact]
    public void MergeCommitsAreDetectableFromParentCount()
    {
        CommitRow merge = Assert.Single(GitRepository.ParseCommitRecords(
            Sample(parents: "aaaaaaa bbbbbbb")));
        Assert.True(merge.ParentHashes.Length > 1);
    }

    [Fact]
    public void DecorationsBecomeBadges()
    {
        CommitRow row = Assert.Single(GitRepository.ParseCommitRecords(
            Sample(decorations: "HEAD -> main, origin/main, tag: v1.0")));
        Assert.NotEmpty(row.Badges);
    }

    [Fact]
    public void NoDecorationsMeansNoBadges()
        => Assert.Empty(Assert.Single(GitRepository.ParseCommitRecords(Sample())).Badges);

    [Fact]
    public void EmptyInputYieldsNoCommits()
        => Assert.Empty(GitRepository.ParseCommitRecords(""));

    // ---- name-status --------------------------------------------------------------------------

    [Fact]
    public void NameStatusMapsCodesToStatuses()
    {
        IReadOnlyList<ChangedFile> files = GitRepository.ParseNameStatus(
            "A\tadded.txt\nM\tmodified.txt\nD\tgone.txt\n");

        Assert.Equal(3, files.Count);
        Assert.Equal(FileChangeStatus.Added, files[0].Status);
        Assert.Equal(FileChangeStatus.Modified, files[1].Status);
        Assert.Equal(FileChangeStatus.Deleted, files[2].Status);
    }

    [Fact]
    public void RenameTargetsTheNewPathSoDiffsResolve()
    {
        // "R100<TAB>old<TAB>new" — diff and show must address the NEW path.
        ChangedFile file = Assert.Single(GitRepository.ParseNameStatus("R100\told.txt\tnew.txt\n"));

        Assert.Equal(FileChangeStatus.Renamed, file.Status);
        Assert.Equal("new.txt", file.Path);
    }

    [Fact]
    public void CopyIsTreatedLikeARename()
    {
        ChangedFile file = Assert.Single(GitRepository.ParseNameStatus("C75\tsrc.txt\tcopy.txt\n"));
        Assert.Equal(FileChangeStatus.Renamed, file.Status);
        Assert.Equal("copy.txt", file.Path);
    }

    [Fact]
    public void MalformedNameStatusLinesAreSkipped()
        => Assert.Empty(GitRepository.ParseNameStatus("no-tab-here\n\n"));

    // ---- shortstat ----------------------------------------------------------------------------

    [Fact]
    public void ShortStatReadsAllThreeNumbers()
    {
        DiffStat stat = GitRepository.ParseShortStat(
            " 3 files changed, 12 insertions(+), 4 deletions(-)");

        Assert.Equal(3, stat.Files);
        Assert.Equal(12, stat.Insertions);
        Assert.Equal(4, stat.Deletions);
        Assert.False(stat.IsEmpty);
    }

    [Fact]
    public void ShortStatHandlesTheSingularForms()
    {
        DiffStat stat = GitRepository.ParseShortStat(
            " 1 file changed, 1 insertion(+), 1 deletion(-)");

        Assert.Equal(1, stat.Files);
        Assert.Equal(1, stat.Insertions);
        Assert.Equal(1, stat.Deletions);
    }

    [Fact]
    public void ShortStatTreatsAMissingClauseAsZero()
    {
        DiffStat stat = GitRepository.ParseShortStat(" 2 files changed, 7 insertions(+)");

        Assert.Equal(2, stat.Files);
        Assert.Equal(7, stat.Insertions);
        Assert.Equal(0, stat.Deletions);
    }

    [Fact]
    public void EmptyShortStatIsEmpty() => Assert.True(GitRepository.ParseShortStat("").IsEmpty);
}
