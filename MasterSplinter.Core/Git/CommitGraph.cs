using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using MasterSplinter.Entrypoint.Models;

namespace MasterSplinter.Entrypoint.Git
{
    /// <summary>
    /// Turns the native commit-graph display list into per-row drawing instructions.
    /// <para>
    /// Layout itself is native (<c>Graph/GraphLayout.{h,cpp}</c>) and rides in the packed log
    /// buffer's extra section, computed in the same pass that builds the records — that is the
    /// only place each commit's parents are already resolved to row positions. What is left here
    /// is unpacking.
    /// </para>
    /// <para>
    /// <b>This is a translation layer with a known expiry date.</b> It exists so the display list
    /// can be drawn by the existing per-row <c>GraphCanvas</c> while the algorithm is proved
    /// against real repositories. When the Direct2D renderer lands it consumes the same bytes
    /// directly, and this file and <c>Models/Graph.cs</c> both go — see docs/graph.md.
    /// </para>
    /// </summary>
    public static class CommitGraph
    {
        // Wire layout, mirroring Graph/GraphLayout.h.
        private const int HeaderSize = 4;    // u32 rowCount
        private const int RowHeaderSize = 5; // laneCount, dotLane, colorIndex, flags, segCount
        private const int SegmentSize = 5;   // x1, y1, x2, y2, colorIndex

        private const byte FlagBoundary = 0x04;

        // Y arrives in HALF-ROW units (0 top, 1 centre, 2 bottom); GraphLine wants a fraction of
        // the row. Integers on the wire keep the whole list byte-sized.
        private const double HalfRow = 0.5;

        /// <summary>
        /// Assigns <see cref="CommitRow.Graph"/> for every row from the packed display list.
        /// <para>
        /// A buffer that is empty, truncated, or describes a different number of rows than were
        /// loaded leaves every row with its default empty graph. That is a blank graph column,
        /// which is the right failure: the commit list itself is still perfectly usable.
        /// </para>
        /// </summary>
        public static void Assign(IReadOnlyList<CommitRow> commits, ReadOnlySpan<byte> displayList)
        {
            if (commits.Count == 0 || displayList.Length < HeaderSize)
                return;

            int rowCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(displayList);
            if (rowCount != commits.Count)
                return;

            int at = HeaderSize;
            for (int i = 0; i < rowCount; i++)
            {
                if (at + RowHeaderSize > displayList.Length)
                    return;

                int laneCount = displayList[at];
                int dotLane = displayList[at + 1];
                int dotColor = displayList[at + 2];
                byte flags = displayList[at + 3];
                int segCount = displayList[at + 4];
                at += RowHeaderSize;

                if (at + (segCount * SegmentSize) > displayList.Length)
                    return;

                var row = new GraphRow { LaneCount = Math.Max(1, laneCount) };
                for (int s = 0; s < segCount; s++)
                {
                    row.Lines.Add(new GraphLine(
                        displayList[at],
                        displayList[at + 1] * HalfRow,
                        displayList[at + 2],
                        displayList[at + 3] * HalfRow,
                        Color(displayList[at + 4])));
                    at += SegmentSize;
                }

                row.Dot = new GraphDot
                {
                    Lane = dotLane,
                    Color = Color(dotColor),
                    // A hollow dot marks a commit whose history continues past the loaded window,
                    // so the line leaving the bottom of the row does not look like a dead end.
                    Open = (flags & FlagBoundary) != 0,
                };
                commits[i].Graph = row;
            }
        }

        /// <summary>
        /// Maps a native colour index onto the palette. The native side cycles 0..5 on lane
        /// allocation and knows nothing about these names.
        /// </summary>
        private static GraphColor Color(int index) => (GraphColor)(index % 6);
    }
}
