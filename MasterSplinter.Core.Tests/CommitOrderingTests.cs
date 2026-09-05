using System.Collections.Generic;
using System.Linq;
using MasterSplinter.Entrypoint.Git;
using MasterSplinter.Entrypoint.Models;

namespace MasterSplinter.Core.Tests;

/// <summary>
/// CHERRY-002. git cherry-pick applies its arguments left to right, so this ordering is a
/// correctness requirement: get it backwards and a picked range applies in reverse, conflicting
/// against itself. The log's sequence is the source of truth — never the commit dates, which can
/// tie or run backwards after a rebase.
/// </summary>
public class CommitOrderingTests
{
    private static CommitRow Commit(string hash) => new() { FullHash = hash, Hash = hash };

    /// <summary>A newest-first log, as every mode except "Reverse Date Order" produces.</summary>
    private static List<CommitRow> Log(params string[] hashes)
        => hashes.Select(Commit).ToList();

    private static string[] Hashes(IEnumerable<CommitRow> rows) => rows.Select(r => r.FullHash).ToArray();

    [Fact]
    public void NewestFirstLogIsReversedSoTheOldestCommitGoesFirst()
    {
        List<CommitRow> log = Log("c", "b", "a");          // newest -> oldest
        List<CommitRow> selection = new() { log[0], log[2] }; // c and a, in click order

        List<CommitRow> ordered = CommitOrdering.OldestFirst(log, selection, logIsOldestFirst: false);

        Assert.Equal(new[] { "a", "c" }, Hashes(ordered));
    }

    [Fact]
    public void OldestFirstLogKeepsItsOwnOrder()
    {
        List<CommitRow> log = Log("a", "b", "c");          // already oldest -> newest
        List<CommitRow> selection = new() { log[2], log[0] };

        List<CommitRow> ordered = CommitOrdering.OldestFirst(log, selection, logIsOldestFirst: true);

        Assert.Equal(new[] { "a", "c" }, Hashes(ordered));
    }

    [Fact]
    public void ClickOrderDoesNotAffectTheResult()
    {
        List<CommitRow> log = Log("d", "c", "b", "a");
        var forwards = new List<CommitRow> { log[0], log[1], log[3] };
        var backwards = new List<CommitRow> { log[3], log[1], log[0] };

        Assert.Equal(Hashes(CommitOrdering.OldestFirst(log, forwards, false)),
                     Hashes(CommitOrdering.OldestFirst(log, backwards, false)));
    }

    [Fact]
    public void DatesAreIgnoredEntirely()
    {
        // Two commits with the SAME timestamp, and a third dated before both: a date sort would
        // scramble these, the log sequence must not.
        List<CommitRow> log = Log("newest", "middle", "oldest");
        log[0].AuthorDate = new System.DateTimeOffset(2020, 1, 1, 0, 0, 0, System.TimeSpan.Zero);
        log[1].AuthorDate = new System.DateTimeOffset(2020, 1, 1, 0, 0, 0, System.TimeSpan.Zero);
        log[2].AuthorDate = new System.DateTimeOffset(2030, 1, 1, 0, 0, 0, System.TimeSpan.Zero);

        List<CommitRow> ordered = CommitOrdering.OldestFirst(log, log.ToList(), logIsOldestFirst: false);

        Assert.Equal(new[] { "oldest", "middle", "newest" }, Hashes(ordered));
    }

    [Fact]
    public void AnUnplaceableRowLeavesTheCallersOrderAlone()
    {
        // A row that is not in the loaded log (e.g. arrived from a search result) means we cannot
        // reason about sequence at all — guessing would be worse than passing it through.
        List<CommitRow> log = Log("b", "a");
        var selection = new List<CommitRow> { log[0], Commit("stranger") };

        List<CommitRow> ordered = CommitOrdering.OldestFirst(log, selection, logIsOldestFirst: false);

        Assert.Equal(new[] { "b", "stranger" }, Hashes(ordered));
    }

    [Fact]
    public void IdentityNotEqualityDecidesPlacement()
    {
        // CommitRow has no value equality. Two rows with the same hash are different objects, and
        // the one that is not in the log must count as unplaceable.
        List<CommitRow> log = Log("a");
        var selection = new List<CommitRow> { Commit("a") };   // same hash, different instance

        List<CommitRow> ordered = CommitOrdering.OldestFirst(log, selection, logIsOldestFirst: false);

        Assert.Single(ordered);
        Assert.NotSame(log[0], ordered[0]);
    }

    [Fact]
    public void SingleSelectionIsReturnedAsIs()
    {
        List<CommitRow> log = Log("b", "a");
        List<CommitRow> ordered = CommitOrdering.OldestFirst(log, new List<CommitRow> { log[1] }, false);

        Assert.Equal(new[] { "a" }, Hashes(ordered));
    }

    [Fact]
    public void EmptySelectionYieldsNothing()
        => Assert.Empty(CommitOrdering.OldestFirst(Log("a"), new List<CommitRow>(), false));

    [Fact]
    public void TheWholeLogRoundTripsIntoStrictOldestFirstOrder()
    {
        List<CommitRow> log = Log("e", "d", "c", "b", "a");

        List<CommitRow> ordered = CommitOrdering.OldestFirst(log, log.ToList(), logIsOldestFirst: false);

        Assert.Equal(new[] { "a", "b", "c", "d", "e" }, Hashes(ordered));
    }

    [Fact]
    public void ALargeSelectionIsOrderedCorrectly()
    {
        // Also the shape that used to be quadratic: selection x log with IndexOf per row.
        List<CommitRow> log = Log(Enumerable.Range(0, 500).Select(i => $"c{499 - i:D3}").ToArray());

        List<CommitRow> ordered = CommitOrdering.OldestFirst(log, log.ToList(), logIsOldestFirst: false);

        Assert.Equal(500, ordered.Count);
        Assert.Equal("c000", ordered[0].FullHash);
        Assert.Equal("c499", ordered[^1].FullHash);
    }
}
