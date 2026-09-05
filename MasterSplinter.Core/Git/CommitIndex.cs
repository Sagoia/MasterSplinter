using System;
using System.Collections.Generic;
using MasterSplinter.Entrypoint.Models;

namespace MasterSplinter.Entrypoint.Git
{
    /// <summary>
    /// Position lookups over one loaded commit list.
    /// <para>
    /// Built once per log load and thrown away with it — the rows themselves are rebuilt on every
    /// refresh, so an index that outlived them would point at stale objects.
    /// </para>
    /// <para>
    /// This exists for the commit graph. Lane layout walks the log and, for each commit, has to find
    /// where its parents sit; doing that by scanning is quadratic on a 2000-commit log, and the log
    /// is exactly the size where that starts to show. The view model's own hash lookups (restoring a
    /// selection after refresh, jumping to a commit from the reflog or from blame) get the same
    /// lookup for free.
    /// </para>
    /// </summary>
    public sealed class CommitIndex
    {
        private readonly Dictionary<string, int> _byHash;
        private readonly Dictionary<CommitRow, int> _byRow;

        public CommitIndex(IReadOnlyList<CommitRow> commits)
        {
            Commits = commits ?? throw new ArgumentNullException(nameof(commits));
            _byHash = new Dictionary<string, int>(commits.Count, StringComparer.Ordinal);
            // Reference identity, not value equality: CommitRow has none, and rows are rebuilt on
            // every refresh, so two rows for the same commit are legitimately different objects.
            _byRow = new Dictionary<CommitRow, int>(commits.Count, ReferenceEqualityComparer.Instance);

            for (int i = 0; i < commits.Count; i++)
            {
                // TryAdd, not [], so the FIRST occurrence wins — matching what a scan would find.
                // Duplicates are not expected, but `--all` over a repository with odd refs is not
                // worth crashing on.
                _byHash.TryAdd(commits[i].FullHash, i);
                _byRow.TryAdd(commits[i], i);
            }
        }

        /// <summary>The list this index was built over, in display order.</summary>
        public IReadOnlyList<CommitRow> Commits { get; }

        public int Count => Commits.Count;

        /// <summary>The row for a full SHA, or null when it is not in the loaded window.</summary>
        /// <remarks>
        /// A miss is ordinary, not an error: the log is capped, so a commit named by the reflog or by
        /// blame is routinely outside it.
        /// </remarks>
        public CommitRow? ByHash(string? fullHash)
            => fullHash != null && _byHash.TryGetValue(fullHash, out int i) ? Commits[i] : null;

        /// <summary>Display position of a SHA, or -1 when it is not loaded.</summary>
        public int PositionOfHash(string? fullHash)
            => fullHash != null && _byHash.TryGetValue(fullHash, out int i) ? i : -1;

        /// <summary>Display position of a row, or -1 when it belongs to a different load.</summary>
        public int PositionOf(CommitRow? row)
            => row != null && _byRow.TryGetValue(row, out int i) ? i : -1;

        public bool Contains(CommitRow? row) => PositionOf(row) >= 0;

        /// <summary>An index over nothing, so callers never need a null check.</summary>
        public static readonly CommitIndex Empty = new(Array.Empty<CommitRow>());
    }
}
