namespace MasterSplinter.Entrypoint.Git
{
    /// <summary>
    /// The warning shown before an operation that a dirty working tree can make git refuse.
    /// <para>
    /// <b>Tracked and untracked changes are separate categories, and conflating them has bitten
    /// twice.</b> Untracked files never block a branch <i>switch</i>, but they do block a
    /// fast-forward when an incoming commit adds a file where one of them already sits — a
    /// different risk, needing different wording and a different fix. Composing the sentence from
    /// the two counts is what keeps the advice matched to the actual situation instead of telling
    /// someone to "commit or discard changes" to a file git has never tracked.
    /// </para>
    /// </summary>
    public static class WorkingTreeWarning
    {
        /// <param name="tracked">Uncommitted changes to tracked files.</param>
        /// <param name="untracked">Untracked files (ignored files are not untracked to git, so
        /// build output does not appear here).</param>
        /// <param name="branch">The branch the operation targets.</param>
        /// <param name="verb">What git would refuse to do — "fast-forward", "switch", …</param>
        public static string Describe(int tracked, int untracked, string branch,
                                      string verb = "fast-forward")
        {
            string what = (tracked, untracked) switch
            {
                ( > 0, > 0) => $"You have {Count(tracked, "uncommitted change")} and "
                             + $"{Count(untracked, "untracked file")}.",
                ( > 0, _) => $"You have {Count(tracked, "uncommitted change")}.",
                _ => $"You have {Count(untracked, "untracked file")}.",
            };

            string risk = tracked > 0
                ? $"Git will refuse to {verb} {branch} if the incoming commits change the "
                  + "same files"
                : $"Git will refuse to {verb} {branch} if the incoming commits add a file "
                  + "where one of yours already sits";
            if (tracked > 0 && untracked > 0)
                risk += ", or add a file where one of your untracked files already sits";

            string fix = (tracked, untracked) switch
            {
                ( > 0, > 0) => "Commit or stash your changes, and move the untracked files aside, "
                             + "if you would rather be safe.",
                ( > 0, _) => "Commit or stash first if you would rather be safe.",
                _ => "Move or delete them first if you would rather be safe.",
            };

            return $"{what} {risk}. {fix}";
        }

        /// <summary>"1 uncommitted change" / "3 uncommitted changes". Public because the same
        /// phrasing appears in the messages that are not full warnings (e.g. the rebase refusal).
        /// </summary>
        public static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";
    }
}
