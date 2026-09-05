using System.Collections.Generic;
using MasterSplinter.Entrypoint.Git;
using MasterSplinter.Entrypoint.Models;

namespace MasterSplinter.Core.Tests;

/// <summary>
/// ParseUnifiedDiff, including the combined "@@@" form git emits for a conflicted file. The
/// combined case is here because it shipped broken once: HunkRe matches none of it, so a
/// conflicted file's diff rendered completely empty.
/// </summary>
public class UnifiedDiffParserTests
{
    private static (List<DiffLine> Lines, bool IsBinary) Parse(string raw)
        => GitRepository.ParseUnifiedDiff(raw);

    [Fact]
    public void OrdinaryHunkNumbersBothGutters()
    {
        var (lines, isBinary) = Parse(
            "@@ -1,3 +1,4 @@\n ctx\n-gone\n+added\n+more\n");

        Assert.False(isBinary);
        Assert.Equal(DiffLineKind.Hunk, lines[0].Kind);

        Assert.Equal(DiffLineKind.Context, lines[1].Kind);
        Assert.Equal("1", lines[1].OldNo);
        Assert.Equal("1", lines[1].NewNo);

        Assert.Equal(DiffLineKind.Removed, lines[2].Kind);
        Assert.Equal("2", lines[2].OldNo);
        Assert.Equal("", lines[2].NewNo);      // removed lines have no new-side number

        Assert.Equal(DiffLineKind.Added, lines[3].Kind);
        Assert.Equal("", lines[3].OldNo);
        Assert.Equal("2", lines[3].NewNo);
        Assert.Equal(DiffLineKind.Added, lines[4].Kind);
        Assert.Equal("3", lines[4].NewNo);
    }

    [Fact]
    public void HunkHeaderWithoutCountsIsAccepted()
    {
        // git omits ",<len>" when the range is a single line.
        var (lines, _) = Parse("@@ -1 +1 @@\n-a\n+b\n");

        Assert.Equal(DiffLineKind.Removed, lines[1].Kind);
        Assert.Equal("1", lines[1].OldNo);
        Assert.Equal(DiffLineKind.Added, lines[2].Kind);
        Assert.Equal("1", lines[2].NewNo);
    }

    [Fact]
    public void CombinedDiffMarkerColumnsAreDecoded()
    {
        // Two parents => "@@@" and two marker columns per body line. "++" is added on both sides,
        // " +" / "+ " added on one, and text starts after the marker columns.
        var (lines, _) = Parse(
            "@@@ -1,3 -1,3 +1,7 @@@\n" +
            "++<<<<<<< HEAD\n" +
            " +MAIN\n" +
            "+ FEATURE\n" +
            "  common\n");

        Assert.Equal(DiffLineKind.Hunk, lines[0].Kind);
        Assert.Equal(DiffLineKind.Added, lines[1].Kind);
        Assert.Equal("<<<<<<< HEAD", lines[1].Text);
        Assert.Equal(DiffLineKind.Added, lines[2].Kind);
        Assert.Equal("MAIN", lines[2].Text);
        Assert.Equal(DiffLineKind.Added, lines[3].Kind);
        Assert.Equal("FEATURE", lines[3].Text);
        Assert.Equal(DiffLineKind.Context, lines[4].Kind);
        Assert.Equal("common", lines[4].Text);
    }

    [Fact]
    public void CombinedDiffRemovalInAnyColumnCountsAsRemoved()
    {
        var (lines, _) = Parse("@@@ -1,2 -1,2 +1,2 @@@\n- gone\n");

        Assert.Equal(DiffLineKind.Removed, lines[1].Kind);
        Assert.Equal("gone", lines[1].Text);
    }

    [Fact]
    public void ThreeParentCombinedDiffUsesThreeMarkerColumns()
    {
        var (lines, _) = Parse("@@@@ -1,2 -1,2 -1,2 +1,2 @@@@\n+++octopus\n");

        Assert.Equal(DiffLineKind.Added, lines[1].Kind);
        Assert.Equal("octopus", lines[1].Text);
    }

    [Theory]
    [InlineData("Binary files a/x.png and b/x.png differ\n")]
    [InlineData("GIT binary patch\ndelta 12\nzzzz\n")]
    public void BinaryMarkersAreDetected(string raw) => Assert.True(Parse(raw).IsBinary);

    [Fact]
    public void TextDiffIsNotBinary() => Assert.False(Parse("@@ -1 +1 @@\n-a\n+b\n").IsBinary);

    [Fact]
    public void CarriageReturnsAreTrimmedFromLineEnds()
    {
        var (lines, _) = Parse("@@ -1 +1 @@\r\n+added\r\n");
        Assert.Equal("added", lines[1].Text);
    }

    [Fact]
    public void PreambleBeforeTheFirstHunkIsIgnored()
    {
        // diff --git / index / --- / +++ lines must not be mistaken for content.
        var (lines, _) = Parse(
            "diff --git a/f b/f\nindex 111..222 100644\n--- a/f\n+++ b/f\n@@ -1 +1 @@\n+x\n");

        Assert.Equal(DiffLineKind.Hunk, lines[0].Kind);
        Assert.Equal(2, lines.Count);
        Assert.Equal("x", lines[1].Text);
    }

    [Fact]
    public void EmptyInputYieldsNothing()
    {
        var (lines, isBinary) = Parse("");
        Assert.Empty(lines);
        Assert.False(isBinary);
    }
}
