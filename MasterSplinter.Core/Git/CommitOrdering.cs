using System.Collections.Generic;
using System.Linq;
using MasterSplinter.Entrypoint.Models;

namespace MasterSplinter.Entrypoint.Git
{
    /// <summary>Ordering rules that depend on the log's own sequence rather than on dates.</summary>
    public static class CommitOrdering
    {
        /// <summary>
        /// The selected commits in the order git must apply them for a cherry-pick.
        /// <para>
        /// The log's own ordering is the source of truth. Sorting by date would break a picked range
        /// whose commits share a timestamp or were rewritten out of chronological order — git applies
        /// them left to right, so the sequence here is a correctness requirement, not a nicety.
        /// "Reverse Date Order" is the one log mode that already lists oldest-first; every other mode
        /// lists newest-first and therefore has to be reversed.
        /// </para>
        /// <para>
        /// If any selected row cannot be placed in <paramref name="log"/>, the caller's order is
        /// returned untouched rather than guessed at.
        /// </para>
        /// </summary>
        /// <param name="log">The loaded commit list, in display order.</param>
        /// <param name="selection">The rows to order; a subset of <paramref name="log"/>.</param>
        /// <param name="logIsOldestFirst">True when the log is already oldest-first.</param>
        public static List<CommitRow> OldestFirst(IReadOnlyList<CommitRow> log,
                                                  IReadOnlyList<CommitRow> selection,
                                                  bool logIsOldestFirst)
        {
            // A position lookup rather than IndexOf per selected row: IndexOf inside the projection
            // made this O(selection x log), which on a 2000-commit log and a large multi-select is
            // the kind of quadratic that only shows up on real repositories.
            //
            // CommitRow has no value equality, so this keys on reference identity exactly as IndexOf
            // did, and TryAdd keeps the FIRST occurrence — also matching IndexOf.
            var position = new Dictionary<CommitRow, int>(log.Count, ReferenceEqualityComparer.Instance);
            for (int i = 0; i < log.Count; i++)
                position.TryAdd(log[i], i);

            var indexed = new List<(CommitRow Commit, int Index)>(selection.Count);
            foreach (CommitRow commit in selection)
            {
                if (!position.TryGetValue(commit, out int index))
                    return selection.ToList(); // unplaceable row: keep the caller's order
                indexed.Add((commit, index));
            }

            return (logIsOldestFirst
                    ? indexed.OrderBy(t => t.Index)
                    : indexed.OrderByDescending(t => t.Index))
                .Select(t => t.Commit)
                .ToList();
        }
    }
}
