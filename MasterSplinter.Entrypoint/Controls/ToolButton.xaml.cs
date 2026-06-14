using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MasterSplinter.Entrypoint.Controls
{
    public sealed partial class ToolButton : UserControl
    {
        public ToolButton()
        {
            InitializeComponent();
            UpdateFade();
            IsEnabledChanged += (_, _) => UpdateFade();
        }

        private void UpdateFade() => Content0.Opacity = IsEnabled ? 1.0 : 0.4;

        /// <summary>Raised when the button is clicked (forwarded from the inner button).</summary>
        public event RoutedEventHandler? Click;

        private void OnRootClick(object sender, RoutedEventArgs e) => Click?.Invoke(this, e);

        public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
            nameof(Glyph), typeof(string), typeof(ToolButton), new PropertyMetadata(string.Empty));

        public string Glyph
        {
            get => (string)GetValue(GlyphProperty);
            set => SetValue(GlyphProperty, value);
        }

        public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
            nameof(Label), typeof(string), typeof(ToolButton), new PropertyMetadata(string.Empty));

        public string Label
        {
            get => (string)GetValue(LabelProperty);
            set => SetValue(LabelProperty, value);
        }

        /// <summary>Empty = use <see cref="Glyph"/>; otherwise "branch" | "merge" | "stash" vector.</summary>
        public static readonly DependencyProperty VectorProperty = DependencyProperty.Register(
            nameof(Vector), typeof(string), typeof(ToolButton), new PropertyMetadata("glyph"));

        public string Vector
        {
            get => (string)GetValue(VectorProperty);
            set => SetValue(VectorProperty, value);
        }
    }
}
