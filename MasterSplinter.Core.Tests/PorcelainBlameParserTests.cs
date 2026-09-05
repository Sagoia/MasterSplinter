using System.Collections.Generic;
using MasterSplinter.Entrypoint.Git;
using MasterSplinter.Entrypoint.Models;

namespace MasterSplinter.Core.Tests;

/// <summary>
/// ParsePorcelainBlame. The sample below is real `git blame --porcelain` output, kept verbatim
/// because its whole point is the third group: it repeats the FIRST group's sha and carries no
/// header lines at all. git only emits the author/summary block on a commit's first group, so
/// without per-sha caching most lines render with a blank author — that is a correctness
/// requirement, not an optimisation.
/// </summary>
public class PorcelainBlameParserTests
{
    private const string ShaA = "7f3f6795392cb18d48d799d944894d635a5cd95f";
    private const string ShaB = "fefe296b524832304e6ab15c0d21dfba5bb93b86";

    private const string Sample =
        ShaA + " 1 1 1\n" +
        "author Alice\n" +
        "author-mail <a@a>\n" +
        "author-time 1787394695\n" +
        "author-tz +0700\n" +
        "committer Alice\n" +
        "summary first commit\n" +
        "boundary\n" +
        "filename f.txt\n" +
        "\tone\n" +
        ShaB + " 2 2 1\n" +
        "author Bob\n" +
        "author-mail <b@b>\n" +
        "author-time 1787394695\n" +
        "author-tz +0700\n" +
        "committer Bob\n" +
        "summary second commit\n" +
        "previous " + ShaA + " f.txt\n" +
        "filename f.txt\n" +
        "\tINSERTED\n" +
        ShaA + " 2 3 2\n" +
        "\ttwo\n" +
        ShaA + " 3 4\n" +
        "\tthree\n";

    private static List<BlameLine> Parse() => GitRepository.ParsePorcelainBlame(Sample, "f.txt");

    [Fact]
    public void EveryContentLineBecomesOneBlameLine()
    {
        List<BlameLine> lines = Parse();
        Assert.Equal(4, lines.Count);
        Assert.Equal(new[] { "one", "INSERTED", "two", "three" },
                     lines.ConvertAll(l => l.Text).ToArray());
    }

    [Fact]
    public void HeadersAreCachedAndReusedForLaterGroupsOfTheSameCommit()
    {
        List<BlameLine> lines = Parse();

        // Lines 3 and 4 belong to ShaA, whose header block appeared only on the FIRST group.
        Assert.Equal(ShaA, lines[2].Sha);
        Assert.Equal("Alice", lines[2].Author);
        Assert.Equal("a@a", lines[2].AuthorEmail);
        Assert.Equal("first commit", lines[2].Summary);

        Assert.Equal(ShaA, lines[3].Sha);
        Assert.Equal("Alice", lines[3].Author);
        Assert.Equal("first commit", lines[3].Summary);
    }

    [Fact]
    public void EachCommitKeepsItsOwnAuthor()
    {
        List<BlameLine> lines = Parse();
        Assert.Equal("Alice", lines[0].Author);
        Assert.Equal("Bob", lines[1].Author);
        Assert.Equal("second commit", lines[1].Summary);
    }

    [Fact]
    public void FinalAndOriginalLineNumbersAreTracked()
    {
        List<BlameLine> lines = Parse();
        Assert.Equal(new[] { 1, 2, 3, 4 }, lines.ConvertAll(l => l.FinalLine).ToArray());
        // "two" moved from original line 2 to final line 3 when INSERTED was added above it.
        Assert.Equal(2, lines[2].OrigLine);
        Assert.Equal(3, lines[2].FinalLine);
    }

    [Fact]
    public void AuthorTimeAndTimezoneSurvive()
    {
        BlameLine first = Parse()[0];
        Assert.Equal(System.TimeSpan.FromHours(7), first.When.Offset);
        Assert.Equal(1787394695, first.When.ToUnixTimeSeconds());
    }

    [Fact]
    public void GroupStartsDriveTheGutter()
    {
        List<BlameLine> lines = Parse();
        // Every line here starts a new group except the last, which continues ShaA's second group.
        Assert.True(lines[0].IsGroupStart);
        Assert.True(lines[1].IsGroupStart);
        Assert.True(lines[2].IsGroupStart);
        Assert.False(lines[3].IsGroupStart);
        Assert.Equal("", lines[3].GutterAuthor);   // blank so runs read as one block
        Assert.NotEqual("", lines[2].GutterAuthor);
    }

    [Fact]
    public void ShortShaIsDerivedFromTheFullSha()
    {
        BlameLine first = Parse()[0];
        Assert.Equal(ShaA[..7], first.ShortSha);
    }

    [Fact]
    public void UncommittedLinesAreFlagged()
    {
        // git uses an all-zero sha for lines that are not committed yet.
        string zeros = new('0', 40);
        List<BlameLine> lines = GitRepository.ParsePorcelainBlame(
            zeros + " 1 1 1\nauthor Not Committed Yet\nauthor-mail <not.committed.yet>\n" +
            "author-time 1787394695\nauthor-tz +0000\nsummary x\nfilename f.txt\n\tdraft\n",
            "f.txt");

        BlameLine only = Assert.Single(lines);
        Assert.True(only.IsUncommitted);
        Assert.Equal("(working)", only.GutterSha);
        Assert.Equal("", only.GutterDate);   // no date for an uncommitted line
    }

    [Fact]
    public void EmptyInputProducesNoLines()
        => Assert.Empty(GitRepository.ParsePorcelainBlame("", "f.txt"));
}
