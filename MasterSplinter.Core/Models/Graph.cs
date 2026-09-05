using System.Collections.Generic;

namespace MasterSplinter.Entrypoint.Models
{
    // Drawing instructions for one row of the branch graph.
    // These are PLACEHOLDER-shaped: they describe a pre-laid-out row, which is what the current
    // single-lane stand-in produces. Real lane layout returns a packed display list instead, so
    // this whole file is expected to go when the Direct2D renderer lands - see docs/graph.md.

    // ---- Commit graph primitives ---------------------------------------------------------------

    /// <summary>Vivid lane colors used by the commit graph. Intentionally theme-independent.</summary>
    public enum GraphColor { Blue, Green, Orange, Red, Gray, Purple }

    /// <summary>
    /// A single drawn segment of the commit graph. Coordinates are expressed in graph units:
    /// X is a lane index, Y is a vertical fraction of the row (0 = top, 0.5 = center, 1 = bottom).
    /// </summary>
    public sealed class GraphLine
    {
        public double X1 { get; }
        public double Y1 { get; }
        public double X2 { get; }
        public double Y2 { get; }
        public GraphColor Color { get; }

        public GraphLine(double x1, double y1, double x2, double y2, GraphColor color)
        {
            X1 = x1; Y1 = y1; X2 = x2; Y2 = y2; Color = color;
        }
    }

    /// <summary>The commit marker drawn on a particular lane of a row.</summary>
    public sealed class GraphDot
    {
        public int Lane { get; set; }
        public GraphColor Color { get; set; }
        public bool Open { get; set; }
    }

    /// <summary>All drawing instructions for one commit row's graph cell.</summary>
    public sealed class GraphRow
    {
        public List<GraphLine> Lines { get; } = new();
        public GraphDot? Dot { get; set; }
        public int LaneCount { get; set; } = 1;
    }

    /// <summary>Small fluent helper so the sample graph reads compactly.</summary>
    public sealed class GraphBuilder
    {
        private readonly GraphRow _row = new();

        public GraphBuilder(int lanes) { _row.LaneCount = lanes; }

        /// <summary>Full-height vertical pass-through line on a lane.</summary>
        public GraphBuilder V(int lane, GraphColor c)
        {
            _row.Lines.Add(new GraphLine(lane, 0, lane, 1, c));
            return this;
        }

        /// <summary>Arbitrary segment (used for diagonals / half lines).</summary>
        public GraphBuilder Seg(double x1, double y1, double x2, double y2, GraphColor c)
        {
            _row.Lines.Add(new GraphLine(x1, y1, x2, y2, c));
            return this;
        }

        public GraphBuilder Dot(int lane, GraphColor c, bool open = false)
        {
            _row.Dot = new GraphDot { Lane = lane, Color = c, Open = open };
            return this;
        }

        public GraphRow Done() => _row;
    }
}
