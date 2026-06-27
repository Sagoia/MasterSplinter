using System.IO;
using ColorCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;

namespace MasterSplinter.Entrypoint.Infrastructure
{
    /// <summary>Maps a file path to a ColorCode language id (DIFF-003). "" means no highlighting.</summary>
    public static class DiffLanguages
    {
        public static string IdForPath(string? path)
        {
            string ext = Path.GetExtension(path ?? "").ToLowerInvariant();
            ILanguage? lang = ext switch
            {
                ".cs" => Languages.CSharp,
                ".c" or ".h" or ".cpp" or ".cc" or ".cxx" or ".hpp" or ".hxx" or ".ino" => Languages.Cpp,
                ".js" or ".jsx" or ".mjs" or ".cjs" or ".json" => Languages.JavaScript,
                ".ts" or ".tsx" => Languages.Typescript,
                ".xml" or ".xaml" or ".axaml" or ".csproj" or ".vbproj" or ".vcxproj" or ".props"
                    or ".targets" or ".config" or ".resx" or ".svg" or ".plist" => Languages.Xml,
                ".html" or ".htm" or ".cshtml" => Languages.Html,
                ".css" => Languages.Css,
                ".sql" => Languages.Sql,
                ".ps1" or ".psm1" or ".psd1" => Languages.PowerShell,
                ".java" => Languages.Java,
                ".py" or ".pyw" => Languages.Python,
                ".md" or ".markdown" => Languages.Markdown,
                ".fs" or ".fsx" => Languages.FSharp,
                ".php" => Languages.Php,
                ".vb" => Languages.VbDotNet,
                _ => null,
            };
            return lang?.Id ?? "";
        }
    }

    /// <summary>Process-wide toggle for syntax highlighting (DIFF-003), read by <see cref="Syntax"/>.</summary>
    public static class SyntaxState
    {
        public static bool Enabled { get; set; } = true;
    }

    /// <summary>
    /// Attached properties that render one line of code into a TextBlock's inlines, optionally
    /// syntax-highlighted via ColorCode (DIFF-003). Highlighting follows the element's ActualTheme
    /// and re-renders on theme change, so the diff recolors automatically on a light/dark toggle.
    /// When highlighting is off (or the language is unknown) a single uncolored Run is emitted, so
    /// the text inherits the TextBlock's themed Foreground.
    /// </summary>
    public static class Syntax
    {
        public static readonly DependencyProperty CodeProperty = DependencyProperty.RegisterAttached(
            "Code", typeof(string), typeof(Syntax), new PropertyMetadata(null, OnChanged));
        public static void SetCode(DependencyObject o, string value) => o.SetValue(CodeProperty, value);
        public static string GetCode(DependencyObject o) => (string)o.GetValue(CodeProperty);

        public static readonly DependencyProperty LangProperty = DependencyProperty.RegisterAttached(
            "Lang", typeof(string), typeof(Syntax), new PropertyMetadata(null, OnChanged));
        public static void SetLang(DependencyObject o, string value) => o.SetValue(LangProperty, value);
        public static string GetLang(DependencyObject o) => (string)o.GetValue(LangProperty);

        private static readonly DependencyProperty HookedProperty = DependencyProperty.RegisterAttached(
            "Hooked", typeof(bool), typeof(Syntax), new PropertyMetadata(false));

        // One formatter per theme; reused across all diff lines (UI thread only).
        private static RichTextBlockFormatter? _light;
        private static RichTextBlockFormatter? _dark;

        private static void OnChanged(DependencyObject o, DependencyPropertyChangedEventArgs e)
        {
            if (o is not TextBlock tb)
                return;
            if (!(bool)tb.GetValue(HookedProperty))
            {
                tb.SetValue(HookedProperty, true);
                tb.ActualThemeChanged += (s, _) => Render((TextBlock)s);
            }
            Render(tb);
        }

        private static void Render(TextBlock tb)
        {
            string code = GetCode(tb) ?? "";
            string langId = GetLang(tb) ?? "";
            tb.Inlines.Clear();

            ILanguage? lang = SyntaxState.Enabled && langId.Length > 0 ? Languages.FindById(langId) : null;
            if (lang == null || code.Length == 0)
            {
                tb.Inlines.Add(new Run { Text = code });
                return;
            }

            try
            {
                RichTextBlockFormatter formatter = tb.ActualTheme == ElementTheme.Dark
                    ? (_dark ??= new RichTextBlockFormatter(ElementTheme.Dark))
                    : (_light ??= new RichTextBlockFormatter(ElementTheme.Light));
                formatter.FormatInlines(code, lang, tb.Inlines);
            }
            catch
            {
                tb.Inlines.Clear();
                tb.Inlines.Add(new Run { Text = code });
            }
        }
    }
}
