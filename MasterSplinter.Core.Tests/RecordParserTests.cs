using MasterSplinter.Entrypoint.Git;
using MasterSplinter.Entrypoint.Models;

namespace MasterSplinter.Core.Tests;

/// <summary>
/// The one record-shaped read still parsed on the host: --shortstat.
/// <para>
/// The commit format and name-status used to be here too. Both moved to the native core in
/// Phase D -- see the LogRecords, SplitMessage and NameStatus suites in the gtest project,
/// and <c>LogUnpackTests</c> / <c>FileUnpackTests</c> for the host halves.
/// </para>
/// Separators are written \u001f / \u001e, never \x1f -- C# hex escapes are greedy.
/// </summary>
public class RecordParserTests
{
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
