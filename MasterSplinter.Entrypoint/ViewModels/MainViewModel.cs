using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using MasterSplinter.Entrypoint.Infrastructure;
using MasterSplinter.Entrypoint.Models;

namespace MasterSplinter.Entrypoint.ViewModels
{
    public sealed class MainViewModel : ObservableObject
    {
        public ObservableCollection<CommitRow> Commits { get; } = new();
        public ObservableCollection<SidebarItemVM> Sidebar { get; } = new();

        public string[] BranchFilterOptions { get; } =
            { "All Branches", "master", "develop", "origin/master", "gh-pages" };
        public string[] OrderOptions { get; } =
            { "Date Order", "Topological Order", "Reverse Date Order", "Author Date" };

        private CommitRow? _selectedCommit;
        public CommitRow? SelectedCommit
        {
            get => _selectedCommit;
            set
            {
                if (Set(ref _selectedCommit, value))
                    SelectedFile = value?.Files.FirstOrDefault();
            }
        }

        private ChangedFile? _selectedFile;
        public ChangedFile? SelectedFile
        {
            get => _selectedFile;
            set => Set(ref _selectedFile, value);
        }

        public MainViewModel()
        {
            BuildSidebar();
            BuildCommits();
            SelectedCommit = Commits.FirstOrDefault();
        }

        // ---- Sidebar -------------------------------------------------------------------------

        private void BuildSidebar()
        {
            void Add(string text, SidebarKind kind, int level, SidebarItemVM? parent)
                => Sidebar.Add(new SidebarItemVM { Text = text, Kind = kind, Level = level, ParentItem = parent });

            Add("FILE STATUS", SidebarKind.SectionHeader, 0, null);
            var fileStatus = Sidebar[^1];
            Add("Working Copy", SidebarKind.WorkingCopy, 1, fileStatus);

            Add("BRANCHES", SidebarKind.SectionHeader, 0, null);
            var branches = Sidebar[^1];
            Add("master", SidebarKind.Branch, 1, branches);

            Add("TAGS", SidebarKind.SectionHeader, 0, null);
            var tags = Sidebar[^1];
            foreach (var t in new[] { "0.10.7", "0.11.0", "0.11.1", "0.8.6", "v0.6.4", "v0.7.4" })
                Add(t, SidebarKind.Tag, 1, tags);

            Add("REMOTES", SidebarKind.SectionHeader, 0, null);
            var remotes = Sidebar[^1];
            Add("origin", SidebarKind.Remote, 1, remotes);
            var origin = Sidebar[^1];
            foreach (var rb in new[] { "gh-pages", "HEAD", "master", "touch", "v0.10" })
                Add(rb, SidebarKind.RemoteBranch, 2, origin);

            // Default selection mirrors the spec's highlighted item.
            Sidebar.First(i => i.Kind == SidebarKind.Branch).IsSelected = true;
        }

        public void ToggleSidebar(SidebarItemVM item)
        {
            if (!item.IsExpandable) return;
            item.IsExpanded = !item.IsExpanded;
            RecomputeVisibility();
        }

        public void HandleSidebarTap(SidebarItemVM item)
        {
            if (item.IsHeader) { ToggleSidebar(item); return; }
            foreach (var i in Sidebar) i.IsSelected = false;
            item.IsSelected = true;
            if (item.IsExpandable) ToggleSidebar(item); // e.g. "origin"
        }

        private void RecomputeVisibility()
        {
            foreach (var item in Sidebar)
            {
                bool visible = true;
                var p = item.ParentItem;
                while (p != null)
                {
                    if (!p.IsExpanded) { visible = false; break; }
                    p = p.ParentItem;
                }
                item.IsVisible = visible;
            }
        }

        // ---- Commits & graph -----------------------------------------------------------------

        private static Badge Local(string s) => new() { Text = s, Kind = BadgeKind.LocalBranch };
        private static Badge Remote(string s) => new() { Text = s, Kind = BadgeKind.RemoteBranch };
        private static Badge Tag(string s) => new() { Text = s, Kind = BadgeKind.Tag };
        private static Badge Head(string s) => new() { Text = s, Kind = BadgeKind.Head };

        private void BuildCommits()
        {
            const GraphColor B = GraphColor.Blue, G = GraphColor.Green, O = GraphColor.Orange, R = GraphColor.Red;

            void C(GraphRow graph, string msg, string date, string author, string email,
                   string hash, string parents, IEnumerable<Badge>? badges = null,
                   IEnumerable<(string path, FileChangeStatus st)>? files = null)
            {
                var c = new CommitRow
                {
                    Graph = graph,
                    Message = msg,
                    Date = date,
                    Author = author,
                    AuthorEmail = email,
                    Hash = hash,
                    FullHash = hash + "f4c2e9b1d7a3",
                    Parents = parents,
                    Committer = author,
                    CommitterDate = date,
                };
                if (badges != null)
                    foreach (var b in badges) c.Badges.Add(b);

                var fileList = files?.ToList() ?? new List<(string, FileChangeStatus)>
                {
                    ("ui/widgets/carousel.js", FileChangeStatus.Modified),
                };
                foreach (var (path, st) in fileList)
                {
                    var cf = new ChangedFile { Path = path, Status = st };
                    foreach (var d in BuildDiff(path, st)) cf.Diff.Add(d);
                    c.Files.Add(cf);
                }
                Commits.Add(c);
            }

            C(new GraphBuilder(2).Seg(0, 0.5, 0, 1, B).Seg(0, 0.5, 1, 1, G).Dot(0, B).Done(),
                "Merge branch 'feature/touch-support' into master", "17 Apr 2013 7:04",
                "Jane Mercer", "jane.mercer@example.com", "a06026a", "b71d4ef, 9c3a812",
                new[] { Local("master"), Remote("origin/master"), Head("origin/HEAD") },
                new[] { ("ui/widgets/carousel.js", FileChangeStatus.Modified),
                        ("ui/jquery.ui.touch.js", FileChangeStatus.Added),
                        ("tests/unit/carousel.html", FileChangeStatus.Modified) });

            C(new GraphBuilder(2).V(0, B).V(1, G).Dot(0, B).Done(),
                "Docs: clarify autoplay option and add demo page", "17 Apr 2013 6:51",
                "Tom Riddle", "t.riddle@example.com", "b71d4ef", "5e8f0c2",
                new[] { Remote("origin/gh-pages") },
                new[] { ("docs/options.md", FileChangeStatus.Modified),
                        ("demos/autoplay/index.html", FileChangeStatus.Added) });

            C(new GraphBuilder(2).V(0, B).V(1, G).Dot(1, G).Done(),
                "Touch: add swipe gesture handling for slides", "16 Apr 2013 18:22",
                "Anil Gupta", "anil@example.com", "9c3a812", "47ab9d0");

            C(new GraphBuilder(2).V(0, B).V(1, G).Dot(1, G).Done(),
                "Touch: scaffold pointer/touch event abstraction", "16 Apr 2013 17:05",
                "Anil Gupta", "anil@example.com", "47ab9d0", "5e8f0c2");

            C(new GraphBuilder(2).V(0, B).Seg(1, 0, 1, 0.5, G).Seg(1, 0.5, 0, 1, G).Dot(1, G).Done(),
                "Start feature/touch-support branch", "16 Apr 2013 16:40",
                "Anil Gupta", "anil@example.com", "5e8f0c2", "5e8f0c2");

            C(new GraphBuilder(1).V(0, B).Dot(0, B).Done(),
                "Release 0.11.1 — fix resize jitter on init", "15 Apr 2013 11:18",
                "Jane Mercer", "jane.mercer@example.com", "d4f7c10", "1aa2b3c",
                new[] { Tag("0.11.1"), Tag("0.11.0") });

            C(new GraphBuilder(3).Seg(0, 0, 0, 0.5, B).Seg(0, 0.5, 0, 1, B).Seg(0, 0.5, 2, 1, O).Dot(0, B).Done(),
                "Merge branch 'fix/rtl-layout'", "15 Apr 2013 10:02",
                "Jane Mercer", "jane.mercer@example.com", "1aa2b3c", "7f3e9a1, c0d2e44");

            C(new GraphBuilder(3).V(0, B).V(2, O).Dot(0, B).Done(),
                "Build: bump grunt-contrib-qunit to 0.3.0", "14 Apr 2013 9:47",
                "Marek Novak", "marek@example.com", "7f3e9a1", "e1b5c88");

            C(new GraphBuilder(3).V(0, B).V(2, O).Dot(2, O).Done(),
                "RTL: mirror navigation arrows in right-to-left", "14 Apr 2013 9:12",
                "Sara Lopez", "sara.lopez@example.com", "c0d2e44", "9b7a6f3");

            C(new GraphBuilder(3).V(0, B).V(2, O).Dot(2, O).Done(),
                "RTL: compute slide offsets from container direction", "13 Apr 2013 20:31",
                "Sara Lopez", "sara.lopez@example.com", "9b7a6f3", "e1b5c88");

            C(new GraphBuilder(3).V(0, B).Seg(2, 0, 2, 0.5, O).Seg(2, 0.5, 0, 1, O).Dot(2, O).Done(),
                "Start fix/rtl-layout branch", "13 Apr 2013 19:58",
                "Sara Lopez", "sara.lopez@example.com", "e1b5c88", "e1b5c88");

            C(new GraphBuilder(1).V(0, B).Dot(0, B).Done(),
                "Release 0.10.7 — accessibility pass on controls", "12 Apr 2013 14:20",
                "Jane Mercer", "jane.mercer@example.com", "8c1d2e9", "33aa01b",
                new[] { Tag("0.10.7") });

            C(new GraphBuilder(2).Seg(0, 0, 0, 0.5, B).Seg(0, 0.5, 0, 1, B).Seg(0, 0.5, 1, 1, R).Dot(0, B).Done(),
                "Merge hotfix/ie9-transform", "12 Apr 2013 13:05",
                "Jane Mercer", "jane.mercer@example.com", "33aa01b", "5d9c7e2, ab12cd3");

            C(new GraphBuilder(2).V(0, B).V(1, R).Dot(0, B).Done(),
                "Tests: add regression for keyboard navigation", "11 Apr 2013 16:44",
                "Tom Riddle", "t.riddle@example.com", "5d9c7e2", "ab12cd3");

            C(new GraphBuilder(2).V(0, B).V(1, R).Dot(1, R).Done(),
                "Hotfix: guard transform for IE9 fallback", "11 Apr 2013 15:30",
                "Marek Novak", "marek@example.com", "ab12cd3", "0f4e6a2");

            C(new GraphBuilder(2).V(0, B).Seg(1, 0, 1, 0.5, R).Seg(1, 0.5, 0, 1, R).Dot(1, R).Done(),
                "Start hotfix/ie9-transform branch", "11 Apr 2013 15:10",
                "Marek Novak", "marek@example.com", "0f4e6a2", "0f4e6a2");

            C(new GraphBuilder(1).V(0, B).Dot(0, B).Done(),
                "Refactor: extract Pager into its own module", "10 Apr 2013 10:08",
                "Anil Gupta", "anil@example.com", "62b9f4d", "init000",
                new[] { Tag("v0.7.4") });

            C(new GraphBuilder(1).Seg(0, 0, 0, 0.5, B).Dot(0, B).Done(),
                "Initial commit — project scaffolding", "9 Apr 2013 8:00",
                "Jane Mercer", "jane.mercer@example.com", "init000", "—");
        }

        private static IEnumerable<DiffLine> BuildDiff(string path, FileChangeStatus status)
        {
            string name = path.Split('/').Last();
            if (status == FileChangeStatus.Added)
            {
                yield return new DiffLine { Kind = DiffLineKind.Hunk, OldNo = "", NewNo = "", Text = $"@@ -0,0 +1,9 @@ new file {name}" };
                yield return new DiffLine { Kind = DiffLineKind.Added, OldNo = "", NewNo = "1", Text = "(function ($) {" };
                yield return new DiffLine { Kind = DiffLineKind.Added, OldNo = "", NewNo = "2", Text = "    \"use strict\";" };
                yield return new DiffLine { Kind = DiffLineKind.Added, OldNo = "", NewNo = "3", Text = "" };
                yield return new DiffLine { Kind = DiffLineKind.Added, OldNo = "", NewNo = "4", Text = "    $.widget(\"ui.touch\", {" };
                yield return new DiffLine { Kind = DiffLineKind.Added, OldNo = "", NewNo = "5", Text = "        options: { threshold: 30 }," };
                yield return new DiffLine { Kind = DiffLineKind.Added, OldNo = "", NewNo = "6", Text = "        _create: function () {" };
                yield return new DiffLine { Kind = DiffLineKind.Added, OldNo = "", NewNo = "7", Text = "            this._on(this.element, { touchstart: \"_start\" });" };
                yield return new DiffLine { Kind = DiffLineKind.Added, OldNo = "", NewNo = "8", Text = "        }" };
                yield return new DiffLine { Kind = DiffLineKind.Added, OldNo = "", NewNo = "9", Text = "    });" };
                yield break;
            }

            if (status == FileChangeStatus.Deleted)
            {
                yield return new DiffLine { Kind = DiffLineKind.Hunk, OldNo = "", NewNo = "", Text = $"@@ -1,5 +0,0 @@ deleted {name}" };
                yield return new DiffLine { Kind = DiffLineKind.Removed, OldNo = "1", NewNo = "", Text = "// legacy shim — no longer required" };
                yield return new DiffLine { Kind = DiffLineKind.Removed, OldNo = "2", NewNo = "", Text = "var legacy = require('./legacy');" };
                yield return new DiffLine { Kind = DiffLineKind.Removed, OldNo = "3", NewNo = "", Text = "legacy.install(window);" };
                yield break;
            }

            yield return new DiffLine { Kind = DiffLineKind.Hunk, OldNo = "", NewNo = "", Text = $"@@ -42,9 +42,11 @@ $.widget(\"ui.{name}\", {{" };
            yield return new DiffLine { Kind = DiffLineKind.Context, OldNo = "42", NewNo = "42", Text = "    _refresh: function () {" };
            yield return new DiffLine { Kind = DiffLineKind.Context, OldNo = "43", NewNo = "43", Text = "        var items = this.element.children();" };
            yield return new DiffLine { Kind = DiffLineKind.Removed, OldNo = "44", NewNo = "", Text = "        this.width = items.outerWidth();" };
            yield return new DiffLine { Kind = DiffLineKind.Added, OldNo = "", NewNo = "44", Text = "        this.width = items.outerWidth(true);" };
            yield return new DiffLine { Kind = DiffLineKind.Added, OldNo = "", NewNo = "45", Text = "        this.gap = parseInt(items.css(\"marginRight\"), 10) || 0;" };
            yield return new DiffLine { Kind = DiffLineKind.Context, OldNo = "45", NewNo = "46", Text = "        this._setupPager(items.length);" };
            yield return new DiffLine { Kind = DiffLineKind.Context, OldNo = "46", NewNo = "47", Text = "" };
            yield return new DiffLine { Kind = DiffLineKind.Hunk, OldNo = "", NewNo = "", Text = "@@ -61,6 +63,8 @@ _goTo: function (index) {" };
            yield return new DiffLine { Kind = DiffLineKind.Context, OldNo = "61", NewNo = "63", Text = "        index = Math.max(0, Math.min(index, this.count - 1));" };
            yield return new DiffLine { Kind = DiffLineKind.Removed, OldNo = "62", NewNo = "", Text = "        this.element.scrollLeft(index * this.width);" };
            yield return new DiffLine { Kind = DiffLineKind.Added, OldNo = "", NewNo = "64", Text = "        var offset = index * (this.width + this.gap);" };
            yield return new DiffLine { Kind = DiffLineKind.Added, OldNo = "", NewNo = "65", Text = "        this.element.animate({ scrollLeft: offset }, this.options.speed);" };
            yield return new DiffLine { Kind = DiffLineKind.Context, OldNo = "63", NewNo = "66", Text = "        this._trigger(\"change\", null, { index: index });" };
            yield return new DiffLine { Kind = DiffLineKind.Context, OldNo = "64", NewNo = "67", Text = "    }," };
        }
    }
}
