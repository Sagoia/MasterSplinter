using System.Collections.ObjectModel;

namespace MasterSplinter.Entrypoint.Models
{
    // Changed files and the two diff layouts (unified lines, side-by-side rows).

    // ---- Changed files & diff -------------------------------------------------------------------

    public enum FileChangeStatus { Added, Modified, Deleted, Renamed, Untracked, Conflicted }

    /// <summary>Which working-tree section a status entry belongs to (STATUS-002, MERGE-003).</summary>
    public enum WorkTreeArea { Staged, Unstaged, Untracked, Conflicted }

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
