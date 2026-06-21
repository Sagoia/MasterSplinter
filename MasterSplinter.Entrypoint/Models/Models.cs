using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace MasterSplinter.Entrypoint.Models
{
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

    // ---- Description badges ---------------------------------------------------------------------

    public enum BadgeKind { LocalBranch, RemoteBranch, Tag, Head }

    public sealed class Badge
    {
        public string Text { get; set; } = "";
        public BadgeKind Kind { get; set; }
    }

    // ---- Commits --------------------------------------------------------------------------------

    public sealed class CommitRow
    {
        public GraphRow Graph { get; set; } = new();
        public string Message { get; set; } = "";   // subject (used by the history row)
        public string Body { get; set; } = "";       // remaining commit message lines
        public ObservableCollection<Badge> Badges { get; } = new();
        public string Date { get; set; } = "";       // display string, formatted from AuthorDate
        public string Author { get; set; } = "";
        public string AuthorEmail { get; set; } = "";
        public string Hash { get; set; } = "";       // short hash

        // Detail-panel metadata
        public string FullHash { get; set; } = "";
        public string Parents { get; set; } = "";    // short parent hashes, comma-joined (display)
        public string[] ParentHashes { get; set; } = Array.Empty<string>();
        public string Committer { get; set; } = "";
        public string CommitterEmail { get; set; } = "";
        public string CommitterDate { get; set; } = "";
        public DateTimeOffset AuthorDate { get; set; }
        public DateTimeOffset CommitDate { get; set; }

        /// <summary>Subject + body, for the detail panel's full-message box.</summary>
        public string FullMessage => string.IsNullOrEmpty(Body) ? Message : Message + "\n\n" + Body;

        public ObservableCollection<ChangedFile> Files { get; } = new();
        public bool HasBadges => Badges.Count > 0;

        /// <summary>True once changed files have been lazily loaded for this commit.</summary>
        public bool FilesLoaded { get; set; }
    }

    // ---- Repository identity & recents ----------------------------------------------------------

    public sealed class RepositoryInfo
    {
        public string Name { get; set; } = "";
        public string Branch { get; set; } = "";
        public string RootPath { get; set; } = "";
    }

    public sealed class RecentRepository
    {
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        public DateTimeOffset LastOpenedUtc { get; set; }
    }

    // ---- Changed files & diff -------------------------------------------------------------------

    public enum FileChangeStatus { Added, Modified, Deleted, Renamed }

    public sealed class ChangedFile
    {
        public string Path { get; set; } = "";
        public FileChangeStatus Status { get; set; }
        public ObservableCollection<DiffLine> Diff { get; } = new();

        /// <summary>True once the unified diff for this file has been lazily loaded.</summary>
        public bool DiffLoaded { get; set; }
    }

    public enum DiffLineKind { Context, Added, Removed, Hunk }

    public sealed class DiffLine
    {
        public string OldNo { get; set; } = "";
        public string NewNo { get; set; } = "";
        public string Text { get; set; } = "";
        public DiffLineKind Kind { get; set; }
    }
}
