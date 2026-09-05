using System.Collections.Generic;
using MasterSplinter.Entrypoint.Git;
using MasterSplinter.Entrypoint.Models;

namespace MasterSplinter.Core.Tests;

/// <summary>
/// The record-shaped reads that are still parsed on the host: name-status and --shortstat.
/// <para>
/// The 12-field commit format used to be here too. It moved to the native core in Phase D --
/// see the LogRecords and SplitMessage suites in <c>parse_test.cpp</c>, and
/// <c>LogUnpackTests</c> for the host half.
/// </para>
/// Separators are written \u001f / \u001e, never \x1f -- C# hex escapes are greedy.
/// </summary>
public class RecordParserTests
{
    // ---- name-status --------------------------------------------------------------------------

    [Fact]
    public void NameStatusMapsCodesToStatuses()
    {
        IReadOnlyList<ChangedFile> files = GitRepository.ParseNameStatus(
            "A\u001eadded.txt\u001eM\u001emodified.txt\u001eD\u001egone.txt\u001e");

        Assert.Equal(3, files.Count);
        Assert.Equal(FileChangeStatus.Added, files[0].Status);
        Assert.Equal(FileChangeStatus.Modified, files[1].Status);
        Assert.Equal(FileChangeStatus.Deleted, files[2].Status);
    }

    [Fact]
    public void RenameTargetsTheNewPathSoDiffsResolve()
    {
        // "R100" RS "old" RS "new" - diff and show must address the NEW path.
        ChangedFile file = Assert.Single(GitRepository.ParseNameStatus("R100\u001eold.txt\u001enew.txt\u001e"));

        Assert.Equal(FileChangeStatus.Renamed, file.Status);
        Assert.Equal("new.txt", file.Path);
    }

    [Fact]
    public void CopyIsTreatedLikeARename()
    {
        ChangedFile file = Assert.Single(GitRepository.ParseNameStatus("C75\u001esrc.txt\u001ecopy.txt\u001e"));
        Assert.Equal(FileChangeStatus.Renamed, file.Status);
        Assert.Equal("copy.txt", file.Path);
    }

    [Fact]
    public void PathsWithControlCharactersSurviveBecauseOfMinusZ()
    {
        // Regression, found by comparing against TortoiseGit (which uses -z everywhere for this
        // reason): the line-based format C-quotes such a path, so the parser used to hand the
        // rest of the app a quoted, backslash-escaped name - and every later diff or stage then
        // addressed a file that does not exist. core.quotePath=false does NOT prevent it.
        ChangedFile file = Assert.Single(GitRepository.ParseNameStatus("A\u001ea\nb.txt\u001e"));

        Assert.Equal("a\nb.txt", file.Path);
        Assert.Equal(FileChangeStatus.Added, file.Status);
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
