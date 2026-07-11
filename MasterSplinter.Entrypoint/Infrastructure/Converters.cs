using System;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Microsoft.UI.Text;
using MasterSplinter.Entrypoint.Models;
using MasterSplinter.Entrypoint.ViewModels;

namespace MasterSplinter.Entrypoint.Infrastructure
{
    /// <summary>Segoe Fluent / MDL2 glyph code points used throughout the app (kept in one place).</summary>
    public static class Glyphs
    {
        public static string Of(int code) => char.ConvertFromUtf32(code);

        public const int ChevronDown = 0xE70D;
        public const int ChevronRight = 0xE76C;
        public const int Folder = 0xE8B7;
        public const int FolderOpen = 0xED25;
        public const int Tag = 0xE8EC;
        public const int Globe = 0xE774;
        public const int Cloud = 0xE753;
        public const int Add = 0xE710;
        public const int Remove = 0xE738;
        public const int Edit = 0xE70F;
        public const int Forward = 0xE72A;
        public const int CheckMark = 0xE73E;
        public const int Upload = 0xE898;
        public const int Download = 0xE896;
        public const int Sync = 0xE895;
        public const int Undo = 0xE7A7;
        public const int Settings = 0xE713;
        public const int CommandPrompt = 0xE756;
        public const int Flag = 0xE7C1;
        public const int More = 0xE712;
        public const int Search = 0xE721;
        public const int Cancel = 0xE711;
        public const int Unknown = 0xE9CE;
        public const int Refresh = 0xE72C;
    }

    public sealed class BoolToVisibilityConverter : IValueConverter
    {
        public bool Invert { get; set; }
        public object Convert(object value, Type t, object p, string l)
        {
            bool b = value is bool v && v;
            if (Invert) b = !b;
            return b ? Visibility.Visible : Visibility.Collapsed;
        }
        public object ConvertBack(object value, Type t, object p, string l) => throw new NotImplementedException();
    }

    /// <summary>Translucent neutral selection fill that reads on both light and dark themes.</summary>
    public sealed class SelectedToBackgroundConverter : IValueConverter
    {
        private static readonly SolidColorBrush Selected = new(Color.FromArgb(0x40, 0x80, 0x80, 0x80));
        private static readonly SolidColorBrush None = new(Colors.Transparent);
        public object Convert(object value, Type t, object p, string l)
            => value is bool b && b ? Selected : None;
        public object ConvertBack(object value, Type t, object p, string l) => throw new NotImplementedException();
    }

    public sealed class SelectedToFontWeightConverter : IValueConverter
    {
        public object Convert(object value, Type t, object p, string l)
            => value is bool b && b ? FontWeights.SemiBold : FontWeights.Normal;
        public object ConvertBack(object value, Type t, object p, string l) => throw new NotImplementedException();
    }

    public sealed class ExpandedToChevronConverter : IValueConverter
    {
        public object Convert(object value, Type t, object p, string l)
            => Glyphs.Of(value is bool b && b ? Glyphs.ChevronDown : Glyphs.ChevronRight);
        public object ConvertBack(object value, Type t, object p, string l) => throw new NotImplementedException();
    }

    public sealed class LevelToMarginConverter : IValueConverter
    {
        public double Step { get; set; } = 16;
        public double Base { get; set; } = 8;
        public object Convert(object value, Type t, object p, string l)
        {
            int lvl = value is int i ? i : 0;
            return new Thickness(Base + lvl * Step, 0, 0, 0);
        }
        public object ConvertBack(object value, Type t, object p, string l) => throw new NotImplementedException();
    }

    /// <summary>Glyph for sidebar leaves. Branch kinds return empty (drawn as a vector instead).</summary>
    public sealed class SidebarKindToGlyphConverter : IValueConverter
    {
        public object Convert(object value, Type t, object p, string l) => value switch
        {
            SidebarKind.WorkingCopy => Glyphs.Of(Glyphs.Folder),
            SidebarKind.Tag => Glyphs.Of(Glyphs.Tag),
            SidebarKind.Remote => Glyphs.Of(Glyphs.Globe),
            _ => string.Empty
        };
        public object ConvertBack(object value, Type t, object p, string l) => throw new NotImplementedException();
    }

    public sealed class SidebarKindToBranchVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type t, object p, string l)
            => value is SidebarKind.Branch or SidebarKind.RemoteBranch ? Visibility.Visible : Visibility.Collapsed;
        public object ConvertBack(object value, Type t, object p, string l) => throw new NotImplementedException();
    }

    public sealed class StatusToBrushConverter : IValueConverter
    {
        private static readonly SolidColorBrush Added = new(Color.FromArgb(0xFF, 0x2D, 0xA4, 0x4E));
        private static readonly SolidColorBrush Modified = new(Color.FromArgb(0xFF, 0xD2, 0x99, 0x22));
        private static readonly SolidColorBrush Deleted = new(Color.FromArgb(0xFF, 0xCF, 0x22, 0x2E));
        private static readonly SolidColorBrush Renamed = new(Color.FromArgb(0xFF, 0x58, 0x6A, 0xE3));
        private static readonly SolidColorBrush Untracked = new(Color.FromArgb(0xFF, 0x82, 0x50, 0xDF));
        public object Convert(object value, Type t, object p, string l) => value switch
        {
            FileChangeStatus.Added => Added,
            FileChangeStatus.Modified => Modified,
            FileChangeStatus.Deleted => Deleted,
            FileChangeStatus.Renamed => Renamed,
            FileChangeStatus.Untracked => Untracked,
            _ => Modified
        };
        public object ConvertBack(object value, Type t, object p, string l) => throw new NotImplementedException();
    }

    /// <summary>Visible when the bound string equals the ConverterParameter (null treated as empty).</summary>
    public sealed class StringEqualsVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type t, object p, string l)
        {
            string v = value as string ?? "";
            string target = p as string ?? "";
            return v == target ? Visibility.Visible : Visibility.Collapsed;
        }
        public object ConvertBack(object value, Type t, object p, string l) => throw new NotImplementedException();
    }

    public sealed class StatusToGlyphConverter : IValueConverter
    {
        public object Convert(object value, Type t, object p, string l) => Glyphs.Of(value switch
        {
            FileChangeStatus.Added => Glyphs.Add,
            FileChangeStatus.Modified => Glyphs.Edit,
            FileChangeStatus.Deleted => Glyphs.Remove,
            FileChangeStatus.Renamed => Glyphs.Forward,
            FileChangeStatus.Untracked => Glyphs.Unknown,
            _ => Glyphs.Edit
        });
        public object ConvertBack(object value, Type t, object p, string l) => throw new NotImplementedException();
    }
}
