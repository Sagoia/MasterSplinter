using MasterSplinter.Entrypoint.Infrastructure;

namespace MasterSplinter.Entrypoint.ViewModels
{
    public enum SidebarKind { SectionHeader, WorkingCopy, Branch, Tag, Remote, RemoteBranch }

    public sealed class SidebarItemVM : ObservableObject
    {
        public string Text { get; set; } = "";
        public SidebarKind Kind { get; set; }
        public int Level { get; set; }
        public SidebarItemVM? ParentItem { get; set; }

        public bool IsHeader => Kind == SidebarKind.SectionHeader;
        public bool IsExpandable => Kind == SidebarKind.SectionHeader || Kind == SidebarKind.Remote;

        private bool _isExpanded = true;
        public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }

        private bool _isSelected;
        public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

        private bool _isVisible = true;
        public bool IsVisible { get => _isVisible; set => Set(ref _isVisible, value); }
    }
}
