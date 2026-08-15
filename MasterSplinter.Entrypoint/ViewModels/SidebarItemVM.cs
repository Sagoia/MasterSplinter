using MasterSplinter.Entrypoint.Infrastructure;

namespace MasterSplinter.Entrypoint.ViewModels
{
    public enum SidebarKind { SectionHeader, WorkingCopy, Branch, Tag, Remote, RemoteBranch, Stash, Reflog }

    public sealed class SidebarItemVM : ObservableObject
    {
        public string Text { get; set; } = "";
        public SidebarKind Kind { get; set; }
        public int Level { get; set; }
        public SidebarItemVM? ParentItem { get; set; }

        // ---- Ref identity (Phase 5) ------------------------------------------------------------
        // Text is the display label, which is not always what git wants: a remote branch renders
        // as "dev" under its "origin" node but must be checked out as "origin/dev". ShortName is
        // the form git verbs take; RefName is the full ref, kept for ref-scoped operations.

        public string RefName { get; set; } = "";
        public string ShortName { get; set; } = "";
        public string Sha { get; set; } = "";
        public string Upstream { get; set; } = "";
        public int Ahead { get; set; }
        public int Behind { get; set; }
        public bool UpstreamGone { get; set; }

        public bool IsHeader => Kind == SidebarKind.SectionHeader;
        public bool IsExpandable => Kind == SidebarKind.SectionHeader || Kind == SidebarKind.Remote;

        private bool _isExpanded = true;
        public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set { if (Set(ref _isSelected, value)) Raise(nameof(IsEmphasized)); }
        }

        private bool _isVisible = true;
        public bool IsVisible { get => _isVisible; set => Set(ref _isVisible, value); }

        /// <summary>Whether this is the branch HEAD points at. Deliberately separate from
        /// <see cref="IsSelected"/>: selection is where the user is looking, currency is where
        /// HEAD is, and only the latter survives a detached HEAD or a branch named like a
        /// remote path.</summary>
        private bool _isCurrent;
        public bool IsCurrent
        {
            get => _isCurrent;
            set { if (Set(ref _isCurrent, value)) Raise(nameof(IsEmphasized)); }
        }

        public bool IsEmphasized => IsSelected || IsCurrent;

        /// <summary>Upstream divergence badge: "↑2 ↓1", "gone", or empty when in sync (or when
        /// there is no upstream, or the track text could not be parsed). Shared with the
        /// repository header (REMOTE-003) so both read the same way.</summary>
        public static string FormatTrack(int ahead, int behind, bool upstreamGone)
        {
            if (upstreamGone) return "gone";
            if (ahead == 0 && behind == 0) return "";
            if (ahead > 0 && behind > 0) return $"↑{ahead} ↓{behind}";
            return ahead > 0 ? $"↑{ahead}" : $"↓{behind}";
        }

        public string TrackText => FormatTrack(Ahead, Behind, UpstreamGone);

        public bool HasTrack => TrackText.Length > 0;
    }
}
