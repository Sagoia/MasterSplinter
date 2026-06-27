using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MasterSplinter.Entrypoint.Models;
using MasterSplinter.Entrypoint.ViewModels;

namespace MasterSplinter.Entrypoint.Infrastructure
{
    /// <summary>Header vs. leaf rows in the navigation sidebar.</summary>
    public sealed class SidebarTemplateSelector : DataTemplateSelector
    {
        public DataTemplate? Header { get; set; }
        public DataTemplate? Leaf { get; set; }

        protected override DataTemplate SelectTemplateCore(object item)
            => item is SidebarItemVM s && s.IsHeader ? Header! : Leaf!;

        protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
            => SelectTemplateCore(item);
    }

    /// <summary>Picks a badge template per <see cref="BadgeKind"/> so each gets its own icon/colors.</summary>
    public sealed class BadgeTemplateSelector : DataTemplateSelector
    {
        public DataTemplate? LocalBranch { get; set; }
        public DataTemplate? RemoteBranch { get; set; }
        public DataTemplate? Tag { get; set; }
        public DataTemplate? Head { get; set; }

        protected override DataTemplate SelectTemplateCore(object item)
            => item is Badge b
                ? b.Kind switch
                {
                    BadgeKind.LocalBranch => LocalBranch!,
                    BadgeKind.RemoteBranch => RemoteBranch!,
                    BadgeKind.Tag => Tag!,
                    BadgeKind.Head => Head!,
                    _ => LocalBranch!,
                }
                : LocalBranch!;

        protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
            => SelectTemplateCore(item);
    }

    /// <summary>Picks a diff-line template per kind. Using templates keeps theme brushes live.</summary>
    public sealed class DiffLineTemplateSelector : DataTemplateSelector
    {
        public DataTemplate? Context { get; set; }
        public DataTemplate? Added { get; set; }
        public DataTemplate? Removed { get; set; }
        public DataTemplate? Hunk { get; set; }

        protected override DataTemplate SelectTemplateCore(object item)
            => item is DiffLine d
                ? d.Kind switch
                {
                    DiffLineKind.Added => Added!,
                    DiffLineKind.Removed => Removed!,
                    DiffLineKind.Hunk => Hunk!,
                    _ => Context!,
                }
                : Context!;

        protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
            => SelectTemplateCore(item);
    }

    /// <summary>Picks a side-by-side row template: a full-width hunk header vs. a two-cell split row (DIFF-002).</summary>
    public sealed class DiffRowTemplateSelector : DataTemplateSelector
    {
        public DataTemplate? Hunk { get; set; }
        public DataTemplate? Split { get; set; }

        protected override DataTemplate SelectTemplateCore(object item)
            => item is DiffRow r && r.IsHunk ? Hunk! : Split!;

        protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
            => SelectTemplateCore(item);
    }

    /// <summary>Picks a per-cell template for one side of a side-by-side row (DIFF-002). Shared by both columns.</summary>
    public sealed class DiffCellTemplateSelector : DataTemplateSelector
    {
        public DataTemplate? Empty { get; set; }
        public DataTemplate? Context { get; set; }
        public DataTemplate? Added { get; set; }
        public DataTemplate? Removed { get; set; }

        protected override DataTemplate SelectTemplateCore(object item)
            => item is DiffCell c
                ? (!c.Present
                    ? Empty!
                    : c.Kind switch
                    {
                        DiffLineKind.Added => Added!,
                        DiffLineKind.Removed => Removed!,
                        _ => Context!,
                    })
                : Empty!;

        protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
            => SelectTemplateCore(item);
    }
}
