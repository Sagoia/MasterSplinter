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

            // Porcelain parsing lives in Parse/StatusParser.cpp now. It emits one record per
            // (file, section) -- a file that is both staged and modified again yields two, a
            // conflicted file exactly one -- so all that is left here is bucketing.
            foreach (ChangedFile file in ReadFiles(NativeLogic.GitStatus(RootPath)))
            {
                switch (file.Area)
                {
                    case WorkTreeArea.Staged: staged.Add(file); break;
                    case WorkTreeArea.Unstaged: unstaged.Add(file); break;
                    case WorkTreeArea.Untracked: untracked.Add(file); break;
                    case WorkTreeArea.Conflicted: conflicted.Add(file); break;
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
