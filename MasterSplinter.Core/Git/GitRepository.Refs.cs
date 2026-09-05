using System;
using MasterSplinter.Entrypoint.Interop;
using MasterSplinter.Entrypoint.Models;

namespace MasterSplinter.Entrypoint.Git
{
    // Branch and tag mutations. Mirrors GitBackend.Refs.cpp.
    public sealed partial class GitRepository
    {
        // ---- Branches & tags (Phase 5, BR-001..007 / TAG-001..003) -----------------------------
        // Each mutation returns null on success, or the git error text for the InfoBar.

        /// <summary>BR-003. Not forced: git carries uncommitted changes across when it safely can
        /// and refuses otherwise, and that refusal is what the caller shows.</summary>
        public string? Checkout(string refName, bool detach)
            => ParseOkErr(NativeLogic.GitCheckout(RootPath, refName, detach));

        /// <summary>BR-004. Empty <paramref name="startPoint"/> means HEAD.</summary>
        public string? CreateBranch(string name, string startPoint, bool checkout)
            => ParseOkErr(NativeLogic.GitCreateBranch(RootPath, name, startPoint, checkout));

        /// <summary>BR-005. Callers must try force=false first, so an unmerged branch can only go
        /// after git has refused once and the user has confirmed again.</summary>
        public string? DeleteBranch(string name, bool force)
            => ParseOkErr(NativeLogic.GitDeleteBranch(RootPath, name, force));

        /// <summary>BR-006. Works on the current branch too.</summary>
        public string? RenameBranch(string oldName, string newName)
            => ParseOkErr(NativeLogic.GitRenameBranch(RootPath, oldName, newName));

        /// <summary>TAG-002. Blank message = lightweight tag; otherwise annotated. Empty
        /// <paramref name="commitish"/> means HEAD.</summary>
        public string? CreateTag(string name, string commitish, string message)
            => ParseOkErr(NativeLogic.GitCreateTag(RootPath, name, commitish, NormalizeMessage(message)));

        /// <summary>TAG-003.</summary>
        public string? DeleteTag(string name)
            => ParseOkErr(NativeLogic.GitDeleteTag(RootPath, name));

        /// <summary>BR-007. Commits in <paramref name="b"/> but not <paramref name="a"/> (Ahead)
        /// and vice versa (Behind). (0, 0) when git failed or the output was unexpected.</summary>
        public (int Ahead, int Behind) AheadBehind(string a, string b)
        {
            // "<onlyInA>\t<onlyInB>" — left is what a has that b lacks, i.e. how far b is behind.
            string[] parts = NativeLogic.GitAheadBehind(RootPath, a, b)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !int.TryParse(parts[0], out int left) || !int.TryParse(parts[1], out int right))
                return (0, 0);
            return (right, left);
        }
    }
}
