using System;

namespace MasterSplinter.Entrypoint.Models
{
    // Reading a repository rather than changing it: blame, search, and half-finished operations.

    // ---- Blame (Phase 8, BLAME-001) -------------------------------------------------------------

    /// <summary>How hard git looks for lines that moved or were copied, rather than written here.</summary>
    public enum BlameMoveDetection { None, WithinFile, AcrossFiles, Aggressive }

    /// <summary>
    /// One blamed line. <see cref="IsGroupStart"/> is true on the first line of each run of
    /// consecutive lines from the same commit — the gutter paints sha/author/date only there, so a
    /// commit's block reads as one thing instead of the same text repeated down the page.
    /// <see cref="SourcePath"/> differs from the blamed file only when -M/-C found the line
    /// elsewhere.
    /// </summary>
    public sealed record BlameLine(string Sha, string ShortSha, string Author, string AuthorEmail,
                                   DateTimeOffset When, string Summary, string SourcePath,
                                   int OrigLine, int FinalLine, string Text, bool IsGroupStart)
    {
        /// <summary>An all-zero sha is git's marker for a line that is not committed yet.</summary>
        public bool IsUncommitted => Sha.Length > 0 && Sha.Trim('0').Length == 0;

        public string GutterSha => IsGroupStart ? (IsUncommitted ? "(working)" : ShortSha) : "";
        public string GutterAuthor => IsGroupStart ? Author : "";
        public string GutterDate => IsGroupStart && !IsUncommitted ? When.ToLocalTime().ToString("yyyy-MM-dd") : "";
    }

    // ---- Search (Phase 8, SEARCH-001/002) -------------------------------------------------------

    /// <summary>
    /// Which single git predicate a search runs. Deliberately one at a time: git ANDs --grep with
    /// --author rather than ORing them, so a combined "message or author" search would quietly
    /// return the intersection instead of the union the user expects.
    /// </summary>
    public enum SearchMode { Message, Author, Content, Path, Hash }

    // ---- Repository state (Phase 7, MERGE-002/003, REBASE-002) ----------------------------------

    /// <summary>Which multi-step git operation, if any, is half-finished in this repository.</summary>
    public enum RepoOperation { None, Merging, Rebasing, CherryPicking, Reverting }

    /// <summary>
    /// A snapshot of "what is git in the middle of". <see cref="Detail"/> is the branch being
    /// rebased, or the short sha of MERGE_HEAD / CHERRY_PICK_HEAD / REVERT_HEAD.
    /// <see cref="Message"/> is MERGE_MSG (comment lines already stripped), which is what the
    /// commit editor should open with once the conflicts are resolved.
    /// </summary>
    public sealed record RepositoryState(RepoOperation Op, string Detail, int Step, int Total,
                                         string Message)
    {
        public static readonly RepositoryState None =
            new(RepoOperation.None, "", 0, 0, "");

        public bool IsActive => Op != RepoOperation.None;

        /// <summary>A rebase knows how many commits it has left; nothing else does.</summary>
        public bool HasSteps => Total > 0;

        /// <summary>`git merge` has no --skip: there is only one commit to apply, so skipping it
        /// would mean abandoning the merge, which is what Abort is for.</summary>
        public bool CanSkip => Op is RepoOperation.Rebasing or RepoOperation.CherryPicking
                                  or RepoOperation.Reverting;

        /// <summary>The git subcommand this state's continue/abort/skip must be sent to.</summary>
        public string GitCommand => Op switch
        {
            RepoOperation.Merging => "merge",
            RepoOperation.Rebasing => "rebase",
            RepoOperation.CherryPicking => "cherry-pick",
            RepoOperation.Reverting => "revert",
            _ => "",
        };
    }
}
