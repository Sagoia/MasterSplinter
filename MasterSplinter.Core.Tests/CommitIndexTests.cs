using System.Collections.Generic;
using System.Linq;
using MasterSplinter.Entrypoint.Git;
using MasterSplinter.Entrypoint.Models;

namespace MasterSplinter.Core.Tests;

/// <summary>
/// The lookup the commit graph is built on. Lane layout walks the log and resolves each commit's
/// parents to positions, so these semantics — hash lookup, identity, and what a miss means — are
/// load-bearing before any lane code exists.
/// </summary>
public class CommitIndexTests
{
    private static CommitRow Commit(string hash, params string[] parents)
        => new() { FullHash = hash, Hash = hash[..System.Math.Min(7, hash.Length)], ParentHashes = parents };

    private static CommitIndex Index(params CommitRow[] rows) => new(rows);

    [Fact]
    public void RowsAreFoundByFullHash()
    {
        CommitRow a = Commit("aaa"), b = Commit("bbb");
        CommitIndex index = Index(a, b);

        Assert.Same(a, index.ByHash("aaa"));
        Assert.Same(b, index.ByHash("bbb"));
    }

    [Fact]
    public void PositionsFollowDisplayOrder()
    {
        CommitRow a = Commit("aaa"), b = Commit("bbb"), c = Commit("ccc");
        CommitIndex index = Index(a, b, c);

        Assert.Equal(0, index.PositionOfHash("aaa"));
        Assert.Equal(2, index.PositionOfHash("ccc"));
        Assert.Equal(1, index.PositionOf(b));
    }

    [Fact]
    public void AParentOutsideTheLoadedWindowIsAMissNotAnError()
    {
        // The log is capped at MaxCommits, so a commit's parent routinely falls outside it. Lane
        // layout has to treat that as "this edge leaves the window", not as a failure.
        CommitIndex index = Index(Commit("aaa", "older-than-the-window"));

        Assert.Null(index.ByHash("older-than-the-window"));
        Assert.Equal(-1, index.PositionOfHash("older-than-the-window"));
    }

    [Fact]
    public void ParentsResolveToPositionsWhichIsWhatLaneLayoutNeeds()
    {
        // newest -> oldest, with a merge whose two parents are both loaded.
        CommitRow merge = Commit("mmm", "aaa", "bbb");
        CommitRow a = Commit("aaa"), b = Commit("bbb");
        CommitIndex index = Index(merge, a, b);

        int[] parentRows = merge.ParentHashes.Select(index.PositionOfHash).ToArray();

        Assert.Equal(new[] { 1, 2 }, parentRows);
    }

    [Fact]
    public void IdentityDecidesMembershipNotHashEquality()
    {
        // Rows are rebuilt on every refresh, so a row from a previous load has the same hash but is
        // a different object — and must not be treated as belonging to this list.
        CommitRow loaded = Commit("aaa");
        CommitIndex index = Index(loaded);

        Assert.True(index.Contains(loaded));
        Assert.False(index.Contains(Commit("aaa")));
        Assert.Equal(-1, index.PositionOf(Commit("aaa")));
    }

    [Fact]
    public void ByHashStillFindsTheLoadedRowForADuplicateInstance()
    {
        // The complement of the test above: identity governs membership, but a hash from anywhere
        // (a reflog entry, a blame line) still resolves to the loaded row.
        CommitRow loaded = Commit("aaa");
        Assert.Same(loaded, Index(loaded).ByHash("aaa"));
    }

    [Fact]
    public void DuplicateHashesKeepTheFirstOccurrence()
    {
        CommitRow first = Commit("aaa"), second = Commit("aaa");
        CommitIndex index = Index(first, second);

        Assert.Same(first, index.ByHash("aaa"));
        Assert.Equal(0, index.PositionOfHash("aaa"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nope")]
    public void UnknownOrMissingHashesReturnNothing(string? hash)
    {
        CommitIndex index = Index(Commit("aaa"));
        Assert.Null(index.ByHash(hash));
        Assert.Equal(-1, index.PositionOfHash(hash));
    }

    [Fact]
    public void NullRowsAreNotMembers()
    {
        CommitIndex index = Index(Commit("aaa"));
        Assert.False(index.Contains(null));
        Assert.Equal(-1, index.PositionOf(null));
    }

    [Fact]
    public void TheEmptyIndexIsUsableWithoutNullChecks()
    {
        Assert.Equal(0, CommitIndex.Empty.Count);
        Assert.Empty(CommitIndex.Empty.Commits);
        Assert.Null(CommitIndex.Empty.ByHash("aaa"));
        Assert.False(CommitIndex.Empty.Contains(Commit("aaa")));
    }

    [Fact]
    public void ItExposesTheListItWasBuiltOver()
    {
        CommitRow a = Commit("aaa"), b = Commit("bbb");
        CommitIndex index = Index(a, b);

        Assert.Equal(2, index.Count);
        Assert.Same(a, index.Commits[0]);
    }
}
