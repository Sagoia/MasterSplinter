using System;
using System.Collections.ObjectModel;

namespace MasterSplinter.Entrypoint.Models
{
    // The commit list: a row, its decoration badges, and the repository it came from.

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
}
