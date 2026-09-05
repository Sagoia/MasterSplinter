using System.Collections.Generic;
using MasterSplinter.Entrypoint.Models;

namespace MasterSplinter.Entrypoint.Git
{
    /// <summary>
    /// Lays out the branch graph for a loaded commit list.
    /// <para>
    /// <b>This is the seam the real graph replaces.</b> Today it produces a placeholder: one blue
    /// lane with a dot on every row, identical regardless of topology. What matters is that it is a
    /// single pass over the whole log rather than something the record parser does per row — real
    /// lane assignment is inherently cross-row (a commit's lane depends on where its children sat),
    /// so a per-row hook could never have grown into it.
    /// </para>
    /// <para>
    /// When the native lane layout lands, only this method's body changes: it will hand the commit
    /// list to the core and unpack the display list that comes back. Parsing, the view model and the
    /// history list all stay as they are.
    /// </para>
    /// </summary>
    public static class CommitGraph
    {
        /// <summary>
        /// Assigns <see cref="CommitRow.Graph"/> for every row.
        /// <paramref name="index"/> is what real layout needs — parents are named by hash, and
        /// resolving them to positions is the whole job — so it is threaded through now even though
        /// the placeholder ignores it.
        /// </summary>
        public static void Assign(IReadOnlyList<CommitRow> commits, CommitIndex index)
        {
            // Placeholder: every row draws the same single lane, so they can share one instance
            // rather than allocating five objects per commit for identical output. Real layout will
            // produce a distinct row each time, at which point this sharing goes away with it.
            GraphRow shared = SingleLane();
            foreach (CommitRow row in commits)
                row.Graph = shared;
        }

        /// <summary>One lane, one dot, connecting vertically across rows.</summary>
        private static GraphRow SingleLane()
            => new GraphBuilder(1).V(0, GraphColor.Blue).Dot(0, GraphColor.Blue).Done();
    }
}
