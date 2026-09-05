using System;
using System.Collections.Generic;
using System.Linq;
using MasterSplinter.Entrypoint.Git;
using MasterSplinter.Entrypoint.Models;

namespace MasterSplinter.Entrypoint.ViewModels
{
    /// <summary>
    /// Builds the sidebar tree from a ref set. Pure: it takes refs and stashes and returns a flat,
    /// parent-linked list — no dispatcher, no repository, no observable collections — so the tree
    /// shape can be asserted directly.
    /// </summary>
    public static class SidebarBuilder
    {
        /// <summary>
        /// The sidebar as a flat list in display order; nesting is carried by
        /// <see cref="SidebarItemVM.ParentItem"/> and <see cref="SidebarItemVM.Level"/>.
        /// Sections with nothing in them are omitted entirely rather than shown empty.
        /// </summary>
        public static List<SidebarItemVM> Build(GitRepository.RefList refs,
                                                IReadOnlyList<StashEntry> stashes)
        {
            var items = new List<SidebarItemVM>();

            void Add(SidebarItemVM item) => items.Add(item);
            void AddPlain(string text, SidebarKind kind, int level, SidebarItemVM? parent)
                => Add(new SidebarItemVM { Text = text, Kind = kind, Level = level, ParentItem = parent });

            AddPlain("FILE STATUS", SidebarKind.SectionHeader, 0, null);
            SidebarItemVM fileStatus = items[^1];
            AddPlain("Working Copy", SidebarKind.WorkingCopy, 1, fileStatus);
            // REFLOG-001. Lives beside Working Copy rather than in its own section: like the working
            // copy it is a VIEW of this repository, not a ref you can act on.
            AddPlain("Reflog", SidebarKind.Reflog, 1, fileStatus);

            AddPlain("BRANCHES", SidebarKind.SectionHeader, 0, null);
            SidebarItemVM branches = items[^1];
            foreach (BranchInfo b in refs.Branches)
                Add(new SidebarItemVM
                {
                    Text = b.Name,
                    ShortName = b.Name,
                    RefName = b.RefName,
                    Sha = b.Sha,
                    Upstream = b.Upstream,
                    Ahead = b.Ahead,
                    Behind = b.Behind,
                    UpstreamGone = b.UpstreamGone,
                    IsCurrent = b.IsCurrent,
                    Kind = SidebarKind.Branch,
                    Level = 1,
                    ParentItem = branches,
                });

            if (refs.Tags.Count > 0)
            {
                AddPlain("TAGS", SidebarKind.SectionHeader, 0, null);
                SidebarItemVM tags = items[^1];
                foreach (TagInfo t in refs.Tags)
                    Add(new SidebarItemVM
                    {
                        Text = t.Name,
                        ShortName = t.Name,
                        RefName = t.RefName,
                        Sha = t.CommitSha,
                        Kind = SidebarKind.Tag,
                        Level = 1,
                        ParentItem = tags,
                    });
            }

            if (stashes.Count > 0)
            {
                AddPlain("STASHES", SidebarKind.SectionHeader, 0, null);
                SidebarItemVM stashHeader = items[^1];
                foreach (StashEntry s in stashes)
                    Add(new SidebarItemVM
                    {
                        // The selector is what apply/pop/drop take, and it is POSITIONAL — these rows
                        // are rebuilt on every refresh precisely because a drop renumbers them. The
                        // index is shown as well as stored: which entry a row IS matters when the
                        // messages are git's own "WIP on main" boilerplate and read alike.
                        Text = s.Message.Length > 0 ? $"{{{s.Index}}}  {s.Message}" : s.Selector,
                        ShortName = s.Selector,
                        RefName = s.Selector,
                        Sha = s.Sha,
                        Upstream = s.Branch,
                        Kind = SidebarKind.Stash,
                        Level = 1,
                        ParentItem = stashHeader,
                    });
            }

            if (refs.Remotes.Count > 0)
            {
                AddPlain("REMOTES", SidebarKind.SectionHeader, 0, null);
                SidebarItemVM remotesHeader = items[^1];

                // Group "origin/main", "origin/dev" -> origin { main, dev }.
                foreach (IGrouping<string, RemoteBranchInfo> group in refs.Remotes
                             .GroupBy(r => r.Remote, StringComparer.Ordinal)
                             .OrderBy(g => g.Key, StringComparer.Ordinal))
                {
                    AddPlain(group.Key, SidebarKind.Remote, 1, remotesHeader);
                    SidebarItemVM remoteNode = items[^1];
                    foreach (RemoteBranchInfo r in group.Where(r => r.Name.Length > 0))
                        Add(new SidebarItemVM
                        {
                            // Displayed as "dev" under the "origin" node, but git wants "origin/dev".
                            Text = r.Name,
                            ShortName = $"{r.Remote}/{r.Name}",
                            RefName = r.RefName,
                            Sha = r.Sha,
                            Kind = SidebarKind.RemoteBranch,
                            Level = 2,
                            ParentItem = remoteNode,
                        });
                }
            }

            // Start with the checked-out branch selected. Nothing is current on a detached HEAD,
            // which is a correct no-op rather than an error.
            SidebarItemVM? current = items.FirstOrDefault(i => i.IsCurrent);
            if (current != null)
                current.IsSelected = true;

            RecomputeVisibility(items);
            return items;
        }

        /// <summary>
        /// A row is visible only when every ancestor is expanded. Walking up the parent chain (rather
        /// than tracking a flag per row) is what keeps a collapsed section from leaving its
        /// grandchildren — a remote's branches — stranded visible.
        /// </summary>
        public static void RecomputeVisibility(IEnumerable<SidebarItemVM> items)
        {
            foreach (SidebarItemVM item in items)
            {
                bool visible = true;
                SidebarItemVM? p = item.ParentItem;
                while (p != null)
                {
                    if (!p.IsExpanded) { visible = false; break; }
                    p = p.ParentItem;
                }
                item.IsVisible = visible;
            }
        }

        /// <summary>
        /// Splits "origin/feature/x" into ("origin", "feature/x"). Matching against the KNOWN remote
        /// names first matters: a branch may itself contain slashes, so the first slash is not
        /// reliably the boundary. Longest name first, so "origin" cannot shadow "origin-mirror".
        /// </summary>
        public static (string Remote, string Branch) SplitUpstream(string upstream,
                                                                   IEnumerable<string> remoteNames)
        {
            if (upstream.Length == 0)
                return ("", "");
            foreach (string name in remoteNames.Distinct().OrderByDescending(n => n.Length))
            {
                if (upstream.StartsWith(name + "/", StringComparison.Ordinal))
                    return (name, upstream[(name.Length + 1)..]);
            }
            int slash = upstream.IndexOf('/');
            return slash < 0 ? ("", upstream) : (upstream[..slash], upstream[(slash + 1)..]);
        }
    }
}
