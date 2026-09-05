using System;
using System.Collections.Generic;
using MasterSplinter.Entrypoint.Interop;
using MasterSplinter.Entrypoint.Models;

namespace MasterSplinter.Entrypoint.Git
{
    // Merge, rebase, cherry-pick, revert and the repository-state probe.
    // Mirrors GitBackend.Sequencer.cpp.
    public sealed partial class GitRepository
    {
        // ---- Merge / rebase / cherry-pick / revert (Phase 7) -----------------------------------
        // Each returns null on success, or git's text. A CONFLICT arrives here as an error string
        // even though it is a normal outcome — callers must refresh regardless and re-read
        // <see cref="State"/>, because the repository really did change.

        /// <summary>MERGE-001.</summary>
        public string? Merge(string refName, bool noFastForward, bool noCommit,
                             Func<string, bool>? onProgress)
            => ParseOkErr(NativeLogic.GitMerge(RootPath, refName, noFastForward, noCommit, onProgress));

        /// <summary>REBASE-001. Non-interactive, never auto-stashed.</summary>
        public string? Rebase(string upstream, Func<string, bool>? onProgress)
            => ParseOkErr(NativeLogic.GitRebase(RootPath, upstream, onProgress));

        /// <summary>CHERRY-001/002. <paramref name="shas"/> must already be oldest-first — git
        /// applies them left to right.</summary>
        public string? CherryPick(IEnumerable<string> shas, bool noCommit, Func<string, bool>? onProgress)
            => ParseOkErr(NativeLogic.GitCherryPick(RootPath, JoinPaths(shas), noCommit, onProgress));

        /// <summary>REVERT-001. <paramref name="mainline"/> is 1-based and required for a merge
        /// commit; 0 omits it.</summary>
        public string? Revert(string sha, int mainline, bool noCommit, Func<string, bool>? onProgress)
            => ParseOkErr(NativeLogic.GitRevert(RootPath, sha, mainline, noCommit, onProgress));

        /// <summary>MERGE-002 / REBASE-002. <paramref name="operation"/> and
        /// <paramref name="action"/> are validated by the native allowlist.</summary>
        public string? SequencerAction(string operation, string action, Func<string, bool>? onProgress)
            => ParseOkErr(NativeLogic.GitSequencerAction(RootPath, operation, action, onProgress));

        /// <summary>MERGE-004. Blank <paramref name="tool"/> defers to the repository's own
        /// merge.tool configuration.</summary>
        public string? MergeTool(string path, string tool, Func<string, bool>? onProgress)
            => ParseOkErr(NativeLogic.GitMergeTool(RootPath, path, tool, onProgress));

        /// <summary>What git is in the middle of, if anything (MERGE-003, REBASE-002). Returns
        /// <see cref="RepositoryState.None"/> when nothing is in progress or the state could not
        /// be read — a banner that fails to appear is better than one that lies.</summary>
        public RepositoryState State()
        {
            string[] f = NativeLogic.GitRepositoryState(RootPath).Split(US);
            if (f.Length < 6 || f[0] != "OK")
                return RepositoryState.None;

            RepoOperation op = f[1] switch
            {
                "merging" => RepoOperation.Merging,
                "rebasing" => RepoOperation.Rebasing,
                "cherry-picking" => RepoOperation.CherryPicking,
                "reverting" => RepoOperation.Reverting,
                _ => RepoOperation.None,
            };
            if (op == RepoOperation.None)
                return RepositoryState.None;

            int.TryParse(f[3], out int step);
            int.TryParse(f[4], out int total);
            return new RepositoryState(op, f[2], step, total, f[5]);
        }

        private static string JoinPaths(IEnumerable<string> paths) => string.Join(RS, paths);

        // "OK" -> null; "ERR<US>message" -> message; anything else -> a generic failure.
        internal static string? ParseOkErr(string raw)
        {
            string[] parts = raw.Split(US, 2);
            if (parts[0] == "OK")
                return null;
            return parts.Length >= 2 && parts[1].Length > 0
                ? parts[1]
                : "The git operation failed (is git installed and on PATH?).";
        }
    }
}
