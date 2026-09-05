namespace MasterSplinter.Entrypoint.Git
{
    /// <summary>
    /// Everything a yes/no confirmation says, with no reference to how it is shown.
    /// </summary>
    /// <param name="DefaultIsPrimary">
    /// Which button is focused. <b>False for anything destructive</b> — Cancel takes the Return key,
    /// so a reflexive Enter cannot discard work. True only where proceeding is the safe, expected
    /// outcome and the dialog is a heads-up rather than a guard.
    /// </param>
    public sealed record Confirmation(string Title, string Message, string PrimaryText,
                                      bool DefaultIsPrimary = false);

    /// <summary>
    /// The wording for every destructive or surprising action the UI confirms.
    /// <para>
    /// These live in Core because what a confirmation *says* is a product decision worth pinning —
    /// "permanently delete" versus "discard", whether git's own refusal is quoted back, whether the
    /// commit being rewritten is named. The dialog that renders them is the view's business; the
    /// promise made to the user is not.
    /// </para>
    /// </summary>
    public static class Confirmations
    {
        /// <summary>
        /// COMMIT-004. Distinct wording for untracked files, where "discard" is not a revert at all
        /// — git has no copy, so the file is deleted from disk and is genuinely unrecoverable.
        /// </summary>
        public static Confirmation DiscardFile(string path, bool untracked)
            => untracked
                ? new Confirmation(
                    "Delete untracked file",
                    $"{path} is not tracked by git. Discarding will permanently delete the file from disk.",
                    "Delete")
                : new Confirmation(
                    "Discard changes",
                    $"Discard changes to {path}? This cannot be undone.",
                    "Discard");

        /// <summary>
        /// COMMIT-007. Amending rewrites history, so the commit being replaced is named — the whole
        /// risk is amending something other than what you had in mind.
        /// </summary>
        public static Confirmation AmendCommit(string? headSubject)
        {
            string current = string.IsNullOrEmpty(headSubject)
                ? ""
                : $"\n\nCurrent commit: “{headSubject}”";
            return new Confirmation(
                "Amend last commit",
                "Amend the last commit? This rewrites history — do not amend commits that are "
                + "already pushed." + current,
                "Amend");
        }

        /// <summary>
        /// BR-003. A heads-up, not a guard: git carries changes across when it safely can and
        /// refuses otherwise, so proceeding is the expected answer and Enter should take it.
        /// </summary>
        public static Confirmation SwitchBranch(int localChanges, string branch)
            => new(
                "Switch branch",
                $"You have {WorkingTreeWarning.Count(localChanges, "uncommitted change")}. "
                + $"Git will carry {(localChanges == 1 ? "it" : "them")} across to {branch} when it "
                + "safely can, and refuse the switch otherwise.",
                "Switch",
                DefaultIsPrimary: true);

        /// <summary>BR-005, first attempt — the safe `branch -d`.</summary>
        public static Confirmation DeleteBranch(string name)
            => new("Delete branch", $"Delete branch {name}? This cannot be undone.", "Delete");

        /// <summary>
        /// BR-005, second attempt. Git's own refusal is quoted rather than paraphrased: it names the
        /// commits at risk far better than we could, and the user has to see it before forcing.
        /// </summary>
        public static Confirmation DeleteBranchAnyway(string gitError)
            => new(
                "Branch not deleted",
                $"{gitError}\n\nDeleting anyway discards commits that exist only on this branch.",
                "Delete Anyway");

        /// <summary>TAG-003. Local only — worth saying, or this reads scarier than it is.</summary>
        public static Confirmation DeleteTag(string name)
            => new(
                "Delete tag",
                $"Delete tag {name}? This removes it from this repository only; a copy already "
                + "pushed to a remote is unaffected.",
                "Delete");

        /// <summary>REBASE-002. The skipped commit's changes land nowhere — say so plainly.</summary>
        public static Confirmation SkipCommit()
            => new(
                "Skip this commit",
                "The commit currently being applied will be dropped and the operation will move on "
                + "to the next one. Its changes will not appear anywhere.",
                "Skip");

        /// <summary>
        /// MERGE-002. Aborting restores the pre-operation state, which also means throwing away any
        /// conflict resolution done so far. Saying that is the whole confirmation.
        /// </summary>
        public static Confirmation AbortOperation(string command)
            => new(
                $"Abort {command}",
                $"Git will put the branch and the working tree back the way they were before the "
                + $"{command} started. Any conflicts you have already resolved will be lost.",
                "Abort");

        /// <summary>
        /// MERGE-003. Marking a file resolved while it still holds markers commits them — the one
        /// case where the app knows better than the user's click.
        /// </summary>
        public static Confirmation ConflictMarkers(string path)
            => new(
                "Conflict markers found",
                $"{path} still contains conflict markers (<<<<<<<, =======, >>>>>>>). Marking it "
                + "resolved now would commit them.",
                "Mark Resolved Anyway");
    }
}
