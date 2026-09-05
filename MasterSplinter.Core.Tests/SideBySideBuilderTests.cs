using System.Collections.Generic;
using MasterSplinter.Entrypoint.Infrastructure;
using MasterSplinter.Entrypoint.Models;

namespace MasterSplinter.Core.Tests;

/// <summary>
/// DIFF-002: consecutive removed lines pair positionally with consecutive added lines; whatever is
/// left over becomes a one-sided row. These are the cases where "positionally" actually bites.
/// </summary>
public class SideBySideBuilderTests
{
    private static DiffLine Line(DiffLineKind kind, string text, string oldNo = "", string newNo = "")
        => new() { Kind = kind, Text = text, OldNo = oldNo, NewNo = newNo };

    private static List<DiffRow> Build(params DiffLine[] lines)
        => SideBySideBuilder.Build(lines, "csharp");

    [Fact]
    public void ContextLineFillsBothSides()
    {
        List<DiffRow> rows = Build(Line(DiffLineKind.Context, "same", "1", "1"));

        DiffRow row = Assert.Single(rows);
        Assert.False(row.IsHunk);
        Assert.True(row.Left.Present);
        Assert.True(row.Right.Present);
        Assert.Equal("same", row.Left.Text);
        Assert.Equal("same", row.Right.Text);
        Assert.Equal("1", row.Left.No);
    }

    [Fact]
    public void HunkHeaderBecomesAFullWidthRow()
    {
        DiffRow row = Assert.Single(Build(Line(DiffLineKind.Hunk, "@@ -1,3 +1,4 @@")));

        Assert.True(row.IsHunk);
        Assert.Equal("@@ -1,3 +1,4 @@", row.HunkText);
    }

    [Fact]
    public void EqualRunsOfRemovedAndAddedPairUpOneToOne()
    {
        List<DiffRow> rows = Build(
            Line(DiffLineKind.Removed, "old1", oldNo: "1"),
            Line(DiffLineKind.Removed, "old2", oldNo: "2"),
            Line(DiffLineKind.Added, "new1", newNo: "1"),
            Line(DiffLineKind.Added, "new2", newNo: "2"));

        Assert.Equal(2, rows.Count);
        Assert.Equal("old1", rows[0].Left.Text);
        Assert.Equal("new1", rows[0].Right.Text);
        Assert.Equal("old2", rows[1].Left.Text);
        Assert.Equal("new2", rows[1].Right.Text);
    }

    [Fact]
    public void MoreRemovedThanAddedLeavesTheRightSideEmpty()
    {
        List<DiffRow> rows = Build(
            Line(DiffLineKind.Removed, "old1", oldNo: "1"),
            Line(DiffLineKind.Removed, "old2", oldNo: "2"),
            Line(DiffLineKind.Added, "new1", newNo: "1"));

        Assert.Equal(2, rows.Count);
        Assert.True(rows[1].Left.Present);
        Assert.False(rows[1].Right.Present);   // filler, not a phantom line
        Assert.Equal("", rows[1].Right.Text);
    }

    [Fact]
    public void MoreAddedThanRemovedLeavesTheLeftSideEmpty()
    {
        List<DiffRow> rows = Build(
            Line(DiffLineKind.Removed, "old1", oldNo: "1"),
            Line(DiffLineKind.Added, "new1", newNo: "1"),
            Line(DiffLineKind.Added, "new2", newNo: "2"));

        Assert.Equal(2, rows.Count);
        Assert.False(rows[1].Left.Present);
        Assert.True(rows[1].Right.Present);
        Assert.Equal("new2", rows[1].Right.Text);
    }

    [Fact]
    public void PureAdditionHasNoLeftSideAtAll()
    {
        List<DiffRow> rows = Build(
            Line(DiffLineKind.Added, "a", newNo: "1"),
            Line(DiffLineKind.Added, "b", newNo: "2"));

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.False(r.Left.Present));
        Assert.All(rows, r => Assert.True(r.Right.Present));
    }

    [Fact]
    public void AddedBeforeRemovedStartsANewBlockRatherThanPairing()
    {
        // The builder consumes removed-then-added. An added run followed by a removed run is two
        // separate blocks, so they must NOT pair up into one row.
        List<DiffRow> rows = Build(
            Line(DiffLineKind.Added, "added", newNo: "1"),
            Line(DiffLineKind.Removed, "removed", oldNo: "1"));

        Assert.Equal(2, rows.Count);
        Assert.False(rows[0].Left.Present);
        Assert.Equal("added", rows[0].Right.Text);
        Assert.Equal("removed", rows[1].Left.Text);
        Assert.False(rows[1].Right.Present);
    }

    [Fact]
    public void LanguageIdReachesEveryPopulatedCell()
    {
        List<DiffRow> rows = Build(
            Line(DiffLineKind.Context, "ctx", "1", "1"),
            Line(DiffLineKind.Removed, "old", oldNo: "2"),
            Line(DiffLineKind.Added, "new", newNo: "2"));

        Assert.All(rows, r =>
        {
            if (r.Left.Present) Assert.Equal("csharp", r.Left.LanguageId);
            if (r.Right.Present) Assert.Equal("csharp", r.Right.LanguageId);
        });
    }

    [Fact]
    public void EmptyInputProducesNoRows() => Assert.Empty(Build());
}
