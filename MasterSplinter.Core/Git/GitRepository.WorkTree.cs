using System.Collections.Generic;
using MasterSplinter.Entrypoint.Interop;
using MasterSplinter.Entrypoint.Models;

namespace MasterSplinter.Entrypoint.Git
{
    // Working-tree status and the staging/commit writes.
    // Mirrors GitBackend.WorkTree.cpp.
    public sealed partial class GitRepository
    {
        // ---- Working tree status (STATUS-001/002) ------------------------------------------------

        public sealed record WorkTreeStatus(List<ChangedFile> Staged, List<ChangedFile> Unstaged,
                                            List<ChangedFile> Untracked, List<ChangedFile> Conflicted);

        /// <summary>
        /// Snapshot of the working tree: conflicted / staged / unstaged / untracked entries. A file
        /// that is both staged and modified again (XY = "MM") appears once in each of the staged and
        /// unstaged sections; a conflicted file appears once, in its own section (MERGE-003).
        /// </summary>
        public WorkTreeStatus Status()
        {
            var staged = new List<ChangedFile>();
            var unstaged = new List<ChangedFile>();
            var untracked = new List<ChangedFile>();
            var conflicted = new List<ChangedFile>();

            // porcelain v1 -z records ("XY <path>"), with NUL separators already translated to RS
            // by the native layer. A rename/copy record is followed by one extra record holding the
            // ORIGINAL path (-z puts the new path first).
            string[] records = NativeLogic.GitStatus(RootPath).Split(RS);
            for (int i = 0; i < records.Length; i++)
            {
                string rec = records[i];
                if (rec.Length < 4 || rec[2] != ' ')
                    continue;

                char x = rec[0], y = rec[1];
                string path = rec[3..];
                string oldPath = "";
                if (x is 'R' or 'C' || y is 'R' or 'C')
                {
                    i++;
                    if (i < records.Length)
                        oldPath = records[i];
                }

                if (x == '?' && y == '?')
                {
                    untracked.Add(new ChangedFile
                    {
                        Path = path,
                        Status = FileChangeStatus.Untracked,
                        Area = WorkTreeArea.Untracked,
                        IsWorkingTree = true,
                    });
                    continue;
                }

                if (IsUnmerged(x, y))
                {
                    conflicted.Add(new ChangedFile
                    {
                        Path = path,
                        Status = FileChangeStatus.Conflicted,
                        Area = WorkTreeArea.Conflicted,
                        IsWorkingTree = true,
                    });
                    continue;
                }

                if (x is not ' ' and not '?')
                {
                    staged.Add(new ChangedFile
                    {
                        Path = path,
                        OldPath = x is 'R' or 'C' ? oldPath : "",
                        Status = MapStatus(x),
                        Area = WorkTreeArea.Staged,
                        IsWorkingTree = true,
                    });
                }
                if (y is not ' ' and not '?')
                {
                    unstaged.Add(new ChangedFile
                    {
                        Path = path,
                        OldPath = y is 'R' or 'C' ? oldPath : "",
                        Status = MapStatus(y),
                        Area = WorkTreeArea.Unstaged,
                        IsWorkingTree = true,
                    });
                }
            }
            return new WorkTreeStatus(staged, unstaged, untracked, conflicted);
        }

        // ---- Staging & commit (Phase 4, COMMIT-001..007) ---------------------------------------
        // Each mutation returns null on success, or the git error text for the InfoBar.

        public string? StagePaths(IEnumerable<string> paths)
            => ParseOkErr(NativeLogic.GitStagePaths(RootPath, JoinPaths(paths)));

        public string? StageAll()
            => ParseOkErr(NativeLogic.GitStageAll(RootPath));

        /// <summary>For a staged rename include BOTH the new and the old path.</summary>
        public string? UnstagePaths(IEnumerable<string> paths)
            => ParseOkErr(NativeLogic.GitUnstagePaths(RootPath, JoinPaths(paths)));

        /// <summary>Restores unstaged changes from the index (tracked files only — deleting an
        /// untracked file is a plain filesystem delete, handled by the caller).</summary>
        public string? DiscardPaths(IEnumerable<string> paths)
            => ParseOkErr(NativeLogic.GitDiscardPaths(RootPath, JoinPaths(paths)));

        public string? Commit(string message, bool amend)
            => ParseOkErr(NativeLogic.GitCommit(RootPath, NormalizeMessage(message), amend));

        /// <summary>Multi-line WinUI TextBoxes report their line breaks as a bare CR, which git
        /// would store verbatim — a two-line message would come back as one line with an embedded
        /// control character. Every message headed for git goes through here.</summary>
        internal static string NormalizeMessage(string message)
            => message.Replace("\r\n", "\n").Replace('\r', '\n');

        /// <summary>Subject and body of the HEAD commit (amend pre-fill), or null if there is no
        /// commit yet.</summary>
        public (string Subject, string Body)? HeadMessage()
        {
            string[] parts = NativeLogic.GitHeadMessage(RootPath).Split(US);
            if (parts.Length < 2 || parts[0] != "OK")
                return null;
            return (parts[1], parts.Length >= 3 ? parts[2] : "");
        }
    }
}
