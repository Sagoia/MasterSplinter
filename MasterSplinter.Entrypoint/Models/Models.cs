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

    public enum FileChangeStatus { Added, Modified, Deleted, Renamed, Untracked }

    /// <summary>Which working-tree section a status entry belongs to (STATUS-002).</summary>
    public enum WorkTreeArea { Staged, Unstaged, Untracked }

    /// <summary>How the diff body is laid out (DIFF-002).</summary>
    public enum DiffViewMode { Unified, SideBySide }

    /// <summary>Whitespace handling passed to git (DIFF-004).</summary>
    public enum WhitespaceMode { None, IgnoreChange, IgnoreAll }

    /// <summary>Aggregate change counts for a commit or a two-commit range (DIFF-001).</summary>
    public readonly record struct DiffStat(int Files, int Insertions, int Deletions)
    {
        public bool IsEmpty => Files == 0 && Insertions == 0 && Deletions == 0;
    }

    public sealed class ChangedFile
    {
        public string Path { get; set; } = "";
        public FileChangeStatus Status { get; set; }

        /// <summary>Original path for a rename/copy (working-tree status only); empty otherwise.</summary>
        public string OldPath { get; set; } = "";

        /// <summary>True for a working-tree status entry (STATUS-001) — diffs come from
        /// the worktree/index rather than a commit.</summary>
        public bool IsWorkingTree { get; set; }

        /// <summary>Which section this entry belongs to when <see cref="IsWorkingTree"/> is set.</summary>
        public WorkTreeArea Area { get; set; }

        /// <summary>Path as shown in the file list ("old → new" for renames).</summary>
        public string DisplayPath => OldPath.Length > 0 ? $"{OldPath} → {Path}" : Path;

        /// <summary>Unified-diff lines (DIFF-002 unified mode).</summary>
        public ObservableCollection<DiffLine> Diff { get; } = new();

        /// <summary>Side-by-side rows built from <see cref="Diff"/> (DIFF-002 split mode).</summary>
        public ObservableCollection<DiffRow> Rows { get; } = new();

        /// <summary>True once the diff for this file has been lazily loaded.</summary>
        public bool DiffLoaded { get; set; }

        /// <summary>True if git reported this file as binary (DIFF-005) — no text diff is shown.</summary>
        public bool IsBinary { get; set; }
    }

    /// <summary>
    /// One working-tree section ("Staged files (n)" etc.) for the grouped status list
    /// (STATUS-002). The group itself is the item collection, as WinUI's grouped
    /// CollectionViewSource expects.
    /// </summary>
    public sealed class ChangedFileGroup : ObservableCollection<ChangedFile>
    {
        public string Title { get; set; } = "";
    }

    public enum DiffLineKind { Context, Added, Removed, Hunk }

    public sealed class DiffLine
    {
        public string OldNo { get; set; } = "";
        public string NewNo { get; set; } = "";
        public string Text { get; set; } = "";
        public DiffLineKind Kind { get; set; }

        /// <summary>ColorCode language id for syntax highlighting (DIFF-003); "" = plain.</summary>
        public string LanguageId { get; set; } = "";
    }

    /// <summary>One side (old or new) of a side-by-side row. <see cref="Present"/> is false for a filler.</summary>
    public sealed class DiffCell
    {
        public bool Present { get; set; }
        public string No { get; set; } = "";
        public string Text { get; set; } = "";
        public DiffLineKind Kind { get; set; } = DiffLineKind.Context;
        public string LanguageId { get; set; } = "";
    }

    /// <summary>
    /// One row of the side-by-side view (DIFF-002): an old-side cell and a new-side cell, or a
    /// full-width hunk header. A cell may be absent (e.g. an added line has no old counterpart).
    /// </summary>
    public sealed class DiffRow
    {
        public bool IsHunk { get; set; }
        public string HunkText { get; set; } = "";
        public DiffCell Left { get; set; } = new();
        public DiffCell Right { get; set; } = new();
    }
}
