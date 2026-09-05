using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using MasterSplinter.Entrypoint.Git;
using MasterSplinter.Entrypoint.Models;

namespace MasterSplinter.Core.Tests;

/// <summary>
/// <c>CommitGraph.Assign</c> — the host half of the commit graph. Layout itself is native
/// (<c>Graph/GraphLayout.cpp</c>, covered by the GraphLayout suite in the gtest project); what is
/// pinned here is the display-list decode and the two things a bad buffer must not do.
/// </summary>
public class CommitGraphTests
{
    private const byte FlagMerge = 0x01;
    private const byte FlagRoot = 0x02;
    private const byte FlagBoundary = 0x04;

    private sealed record Seg(byte X1, byte Y1, byte X2, byte Y2, byte Color);

    /// <summary>Builds a display list by hand, from the layout in Graph/GraphLayout.h.</summary>
    private static byte[] DisplayList(params (byte LaneCount, byte DotLane, byte Color, byte Flags, Seg[] Segments)[] rows)
    {
        var bytes = new List<byte>();
        var count = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(count, (uint)rows.Length);
        bytes.AddRange(count);

        foreach (var r in rows)
        {
            bytes.Add(r.LaneCount);
            bytes.Add(r.DotLane);
            bytes.Add(r.Color);
            bytes.Add(r.Flags);
            bytes.Add((byte)r.Segments.Length);
            foreach (Seg s in r.Segments)
            {
                bytes.Add(s.X1);
                bytes.Add(s.Y1);
                bytes.Add(s.X2);
                bytes.Add(s.Y2);
                bytes.Add(s.Color);
            }
        }
        return bytes.ToArray();
    }

    private static List<CommitRow> Rows(int n)
    {
        var list = new List<CommitRow>();
        for (int i = 0; i < n; i++)
            list.Add(new CommitRow { FullHash = "h" + i });
        return list;
    }

    [Fact]
    public void EachRowGetsItsDotLaneCountAndSegments()
    {
        List<CommitRow> commits = Rows(1);
        CommitGraph.Assign(commits, DisplayList(
            (2, 1, 0, 0, new[] { new Seg(0, 0, 0, 2, 1), new Seg(1, 1, 1, 2, 0) })));

        GraphRow g = commits[0].Graph;
        Assert.Equal(2, g.LaneCount);
        Assert.NotNull(g.Dot);
        Assert.Equal(1, g.Dot!.Lane);
        Assert.Equal(2, g.Lines.Count);
    }

    [Fact]
    public void HalfRowUnitsBecomeFractionsOfTheRow()
    {
        // Y travels as 0 = top, 1 = centre, 2 = bottom so the whole list stays byte-sized;
        // GraphLine wants a fraction, which is what the renderer scales by row height.
        List<CommitRow> commits = Rows(1);
        CommitGraph.Assign(commits, DisplayList(
            (1, 0, 0, 0, new[] { new Seg(0, 0, 0, 1, 0), new Seg(0, 1, 0, 2, 0) })));

        GraphLine top = commits[0].Graph.Lines[0];
        Assert.Equal(0.0, top.Y1);
        Assert.Equal(0.5, top.Y2);

        GraphLine bottom = commits[0].Graph.Lines[1];
        Assert.Equal(0.5, bottom.Y1);
        Assert.Equal(1.0, bottom.Y2);
    }

    [Fact]
    public void TheColourIndexMapsOntoThePaletteInDeclarationOrder()
    {
        // The native side cycles 0..5 on lane allocation and knows nothing about these names.
        List<CommitRow> commits = Rows(6);
        CommitGraph.Assign(commits, DisplayList(
            (1, 0, 0, 0, System.Array.Empty<Seg>()),
            (1, 0, 1, 0, System.Array.Empty<Seg>()),
            (1, 0, 2, 0, System.Array.Empty<Seg>()),
            (1, 0, 3, 0, System.Array.Empty<Seg>()),
            (1, 0, 4, 0, System.Array.Empty<Seg>()),
            (1, 0, 5, 0, System.Array.Empty<Seg>())));

        Assert.Equal(GraphColor.Blue, commits[0].Graph.Dot!.Color);
        Assert.Equal(GraphColor.Green, commits[1].Graph.Dot!.Color);
        Assert.Equal(GraphColor.Orange, commits[2].Graph.Dot!.Color);
        Assert.Equal(GraphColor.Red, commits[3].Graph.Dot!.Color);
        Assert.Equal(GraphColor.Gray, commits[4].Graph.Dot!.Color);
        Assert.Equal(GraphColor.Purple, commits[5].Graph.Dot!.Color);
    }

    [Fact]
    public void ABoundaryRowGetsAHollowDot()
    {
        // Its history continues past the loaded window, so the line leaving the bottom of the row
        // should not read as a dead end.
        List<CommitRow> commits = Rows(2);
        CommitGraph.Assign(commits, DisplayList(
            (1, 0, 0, 0, System.Array.Empty<Seg>()),
            (1, 0, 0, FlagBoundary, System.Array.Empty<Seg>())));

        Assert.False(commits[0].Graph.Dot!.Open);
        Assert.True(commits[1].Graph.Dot!.Open);
    }

    [Fact]
    public void AMergeOrRootFlagDoesNotHollowTheDot()
    {
        List<CommitRow> commits = Rows(2);
        CommitGraph.Assign(commits, DisplayList(
            (1, 0, 0, FlagMerge, System.Array.Empty<Seg>()),
            (1, 0, 0, FlagRoot, System.Array.Empty<Seg>())));

        Assert.False(commits[0].Graph.Dot!.Open);
        Assert.False(commits[1].Graph.Dot!.Open);
    }

    [Fact]
    public void ARowCountThatDisagreesWithTheListIsIgnoredEntirely()
    {
        // Rather than assigning half a graph: the two came from one buffer, so a mismatch means
        // something is wrong with the buffer, not with one row.
        List<CommitRow> commits = Rows(3);
        CommitGraph.Assign(commits, DisplayList((1, 0, 0, 0, System.Array.Empty<Seg>())));

        Assert.All(commits, c => Assert.Empty(c.Graph.Lines));
        Assert.All(commits, c => Assert.Null(c.Graph.Dot));
    }

    [Fact]
    public void ATruncatedBufferLeavesTheRemainingRowsBlankRatherThanThrowing()
    {
        byte[] full = DisplayList(
            (1, 0, 0, 0, new[] { new Seg(0, 1, 0, 2, 0) }),
            (1, 0, 0, 0, new[] { new Seg(0, 0, 0, 1, 0) }));
        byte[] cut = full[..(full.Length - 3)];

        List<CommitRow> commits = Rows(2);
        CommitGraph.Assign(commits, cut);

        // The first row decoded; the second ran out of bytes and stayed blank. A blank graph
        // column is the right failure -- the commit list itself is still perfectly usable.
        Assert.Single(commits[0].Graph.Lines);
        Assert.Empty(commits[1].Graph.Lines);
    }

    [Fact]
    public void AnEmptyDisplayListLeavesEveryRowBlank()
    {
        List<CommitRow> commits = Rows(2);
        CommitGraph.Assign(commits, ReadOnlySpan<byte>.Empty);

        Assert.All(commits, c => Assert.Null(c.Graph.Dot));
    }

    [Fact]
    public void AnEmptyCommitListIsHarmless()
        => CommitGraph.Assign(new List<CommitRow>(), DisplayList());
}
