using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MasterSplinter.Entrypoint.Git;
using MasterSplinter.Entrypoint.Models;

namespace MasterSplinter.Entrypoint.ViewModels
{
    // The sidebar tree. Construction lives in Core (SidebarBuilder); this is selection and taps.
    public sealed partial class MainViewModel
    {
        // ---- Sidebar ---------------------------------------------------------------------------

        /// <summary>Push a freshly-read ref set into the sidebar, the compare picker, and the
        /// caches the branch/tag dialogs read. Called from both load and refresh.</summary>
        private void ApplyRefs(GitRepository.RefList refs, IReadOnlyList<StashEntry> stashes)
        {
            Stashes.Clear();
            foreach (var s in stashes)
                Stashes.Add(s);
            OnPropertyChanged(nameof(HasStashes));

            BuildSidebar(refs, stashes);

            // Ref names for the compare-refs picker (DIFF-007) and the create-branch start point
            // (BR-004): HEAD + branches + remotes + tags.
            _refNames = new List<string> { "HEAD" };
            _refNames.AddRange(refs.Branches.Select(b => b.Name));
            _refNames.AddRange(refs.Remotes.Select(r => $"{r.Remote}/{r.Name}"));
            _refNames.AddRange(refs.Tags.Select(t => t.Name));
            OnPropertyChanged(nameof(CompareRefNames));

            // REMOTE-003: the header's divergence comes from the ref list we already have —
            // %(upstream:track) is parsed per branch in ListRefs, so this costs no extra git call.
            BranchInfo? current = refs.Branches.FirstOrDefault(b => b.IsCurrent);
            CurrentBranchName = current?.Name ?? "";
            UpstreamName = current?.Upstream ?? "";
            HeaderTrackText = current == null
                ? ""
                : SidebarItemVM.FormatTrack(current.Ahead, current.Behind, current.UpstreamGone);
            (UpstreamRemote, UpstreamBranch) = SidebarBuilder.SplitUpstream(
                UpstreamName, refs.Remotes.Select(r => r.Remote));
        }

        private void BuildSidebar(GitRepository.RefList refs, IReadOnlyList<StashEntry> stashes)
            => Sidebar.Reset(SidebarBuilder.Build(refs, stashes));

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
            if (item.Kind == SidebarKind.WorkingCopy)
            {
                _ = EnterWorkingCopyAsync(); // STATUS-001: switch to the working-copy view
                return;
            }
            if (item.Kind == SidebarKind.Reflog)
            {
                _ = EnterReflogAsync(); // REFLOG-001
                return;
            }
            if (item.IsExpandable) ToggleSidebar(item); // e.g. a remote node
        }

        private void RecomputeVisibility() => SidebarBuilder.RecomputeVisibility(Sidebar);
    }
}
