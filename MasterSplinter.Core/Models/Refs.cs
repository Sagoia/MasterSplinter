using System;

namespace MasterSplinter.Entrypoint.Models
{
    // Everything that names a commit: branches, tags, remotes, stashes, reflog entries.

    // ---- Refs (Phase 5, BR-001..007 / TAG-001..003) ---------------------------------------------
    // One record each from MsGitRefDetails. `Name` is the short form git verbs take (what gets
    // passed to switch/branch/tag); `RefName` is the full ref, kept for ref-scoped operations.

    /// <summary>A local branch. Ahead/Behind are relative to this branch's own upstream, and are
    /// 0/0 both when in sync and when the track text could not be parsed.</summary>
    public sealed record BranchInfo(string RefName, string Name, string Sha, string Upstream,
                                    int Ahead, int Behind, bool UpstreamGone, bool IsCurrent);

    /// <summary>A tag. CommitSha is the peeled commit, so annotated tags point at the commit
    /// rather than at their tag object.</summary>
    public sealed record TagInfo(string RefName, string Name, string CommitSha, bool IsAnnotated);

    /// <summary>A remote-tracking branch, pre-split for the sidebar's remote grouping. `Name` is
    /// the part below the remote ("feature/x" for origin/feature/x).</summary>
    public sealed record RemoteBranchInfo(string RefName, string Remote, string Name, string Sha);

    // ---- Remotes (Phase 6, REMOTE-001/008) ------------------------------------------------------

    /// <summary>A configured remote. <see cref="PushUrl"/> equals <see cref="FetchUrl"/> unless a
    /// separate push URL was configured, which is how `git remote -v` reports it.</summary>
    public sealed record RemoteInfo(string Name, string FetchUrl, string PushUrl)
    {
        /// <summary>True when the remote pushes somewhere other than it fetches — worth showing
        /// separately, and rare enough that hiding it otherwise keeps the dialog quiet.</summary>
        public bool HasSeparatePushUrl => PushUrl.Length > 0 && PushUrl != FetchUrl;
    }

    // ---- Stash (Phase 8, STASH-001..004) --------------------------------------------------------

    /// <summary>
    /// One entry in the stash. <see cref="Selector"/> ("stash@{2}") is what apply/pop/drop take —
    /// and it is positional, so it is only valid until the next stash mutation renumbers the list.
    /// <see cref="Branch"/> and <see cref="Message"/> are split out of git's reflog subject
    /// ("WIP on main: 1a2b3c4 fix the thing").
    /// </summary>
    public sealed record StashEntry(int Index, string Selector, string Sha, string ShortSha,
                                    string Message, string Branch, DateTimeOffset When,
                                    string Author)
    {
        /// <summary>"stash@{0}  ·  main" for the sidebar tooltip.</summary>
        public string DisplayBranch => Branch.Length > 0 ? Branch : "(unknown branch)";
    }

    // ---- Reflog (Phase 8, REFLOG-001) -----------------------------------------------------------

    /// <summary>
    /// One reflog entry. git packs the operation and its argument into a single reflog subject
    /// ("checkout: moving from main to feature"); <see cref="Action"/> is the part before the first
    /// ": " and <see cref="Detail"/> the rest. <see cref="Subject"/> is the commit's own subject,
    /// which is often fuller than the reflog's.
    /// </summary>
    public sealed record ReflogEntry(int Index, string Selector, string Sha, string ShortSha,
                                     string Action, string Detail, string Subject,
                                     DateTimeOffset When, string Author)
    {
        /// <summary>What to show in the description column: the reflog's own detail when it says
        /// something ("moving from main to feature"), else the commit subject.</summary>
        public string Description => Detail.Length > 0 ? Detail : Subject;

        public string DateText => When == DateTimeOffset.MinValue
            ? ""
            : When.ToLocalTime().ToString("d MMM yyyy H:mm");
    }
}
