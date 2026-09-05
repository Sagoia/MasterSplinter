using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MasterSplinter.Entrypoint.Controls
{
    /// <summary>A value read back from a dialog field once it closes.</summary>
    internal sealed class Field<T>
    {
        private readonly Func<T> _read;

        internal Field(Control control, Func<T> read)
        {
            Control = control;
            _read = read;
        }

        /// <summary>The control itself, so fields can be wired to each other.</summary>
        internal Control Control { get; }

        /// <summary>The control's current value. Read after <see cref="DialogBuilder.ShowAsync"/>.</summary>
        public T Value => _read();
    }

    /// <summary>
    /// Fluent construction for the app's input dialogs.
    /// <para>
    /// Every one of these was previously hand-built: a <c>StackPanel</c>, the same
    /// <c>Spacing</c>/<c>MinWidth</c>, the same Cancel button, then a block at the bottom reading
    /// each control back out. That repetition is why the dialogs had drifted apart on width and on
    /// which button was default. Building them through here keeps them consistent by construction.
    /// </para>
    /// <para>
    /// This deliberately stays in the app project: unlike <c>Confirmations</c> — where the wording is
    /// a product promise worth pinning in Core — an input dialog is genuinely UI. What it is worth
    /// extracting from is the boilerplate, not the decisions.
    /// </para>
    /// </summary>
    internal sealed class DialogBuilder
    {
        private readonly StackPanel _panel = new() { Spacing = 12, MinWidth = 380 };
        private readonly ContentDialog _dialog;

        internal DialogBuilder(XamlRoot? xamlRoot, string title, string primaryText)
        {
            _dialog = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = title,
                Content = _panel,
                PrimaryButtonText = primaryText,
                CloseButtonText = "Cancel",
                // Primary by default: an input dialog the user deliberately opened and filled in is
                // not a destructive prompt. Destructive confirmations go through ConfirmAsync, which
                // defaults to Cancel instead.
                DefaultButton = ContentDialogButton.Primary,
            };
        }

        /// <summary>The underlying dialog, for the few cases needing a validation hook.</summary>
        internal ContentDialog Dialog => _dialog;

        /// <summary>
        /// Makes Cancel the default button. For an input dialog whose action is destructive — a
        /// rebase rewrites history — so Return does not trigger it.
        /// </summary>
        internal DialogBuilder DefaultToCancel()
        {
            _dialog.DefaultButton = ContentDialogButton.Close;
            return this;
        }

        /// <summary>
        /// A full-weight wrapped paragraph, for warnings the user is meant to actually read.
        /// Distinct from <see cref="Note"/>, which is deliberately muted and small.
        /// </summary>
        internal DialogBuilder Paragraph(string text)
        {
            _panel.Children.Add(new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 400,
            });
            return this;
        }

        /// <summary>A muted explanatory paragraph.</summary>
        internal DialogBuilder Note(string text)
        {
            _panel.Children.Add(new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                // Muted via opacity rather than a ThemeResource lookup, which does not resolve
                // reliably through Application.Current.Resources for theme-dictionary brushes.
                Opacity = 0.7,
                FontSize = 12,
            });
            return this;
        }

        internal Field<string> TextBox(string header, string initial = "", string placeholder = "")
        {
            var box = new Microsoft.UI.Xaml.Controls.TextBox
            {
                Header = header,
                Text = initial,
                PlaceholderText = placeholder,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            _panel.Children.Add(box);
            return new Field<string>(box, () => box.Text ?? "");
        }

        internal Field<string> Combo(string header, IReadOnlyList<string> items, int selectedIndex = 0)
        {
            var box = new ComboBox
            {
                Header = header,
                ItemsSource = items,
                SelectedIndex = items.Count == 0 ? -1 : Math.Clamp(selectedIndex, 0, items.Count - 1),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            _panel.Children.Add(box);
            return new Field<string>(box, () => box.SelectedItem as string ?? "");
        }

        /// <summary>
        /// A combo whose <b>index</b> is the answer rather than its label — e.g. revert's mainline
        /// parent, where git wants the 1-based parent number and the text is only there to identify
        /// which parent that is.
        /// </summary>
        internal Field<int> ComboIndex(string header, IReadOnlyList<string> items, int selectedIndex = 0)
        {
            var box = new ComboBox
            {
                Header = header,
                ItemsSource = items,
                SelectedIndex = items.Count == 0 ? -1 : Math.Clamp(selectedIndex, 0, items.Count - 1),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            _panel.Children.Add(box);
            return new Field<int>(box, () => box.SelectedIndex);
        }

        /// <param name="enabled">
        /// False greys the box out — e.g. "include untracked files (0)" when there are none. The
        /// option stays visible so the count still tells the user something.
        /// </param>
        internal Field<bool> Check(string content, bool isChecked = false, bool enabled = true)
        {
            var box = new CheckBox { Content = content, IsChecked = isChecked, IsEnabled = enabled };
            _panel.Children.Add(box);
            return new Field<bool>(box, () => box.IsChecked == true);
        }

        /// <summary>
        /// Greys out <paramref name="field"/> while <paramref name="toggle"/> is checked — e.g.
        /// "fetch from all remotes" makes the remote picker meaningless, and leaving it live implies
        /// a choice that no longer has any effect.
        /// </summary>
        internal DialogBuilder DisableWhile<T>(Field<T> field, Field<bool> toggle)
        {
            if (toggle.Control is CheckBox box)
            {
                box.Checked += (_, _) => field.Control.IsEnabled = false;
                box.Unchecked += (_, _) => field.Control.IsEnabled = true;
                field.Control.IsEnabled = box.IsChecked != true;
            }
            return this;
        }

        /// <summary>Anything the helpers above do not cover.</summary>
        internal DialogBuilder Add(FrameworkElement element)
        {
            _panel.Children.Add(element);
            return this;
        }

        /// <summary>
        /// Enables the primary button only while <paramref name="field"/> satisfies
        /// <paramref name="isValid"/> — e.g. a branch dialog cannot submit a blank name.
        /// <para>
        /// Hooks only that field's own control. Watching every textbox in the panel would make an
        /// optional second field (a tag message, say) re-validate the required one, which is
        /// harmless here but exactly the sort of coupling that stops being harmless later.
        /// </para>
        /// </summary>
        internal DialogBuilder EnablePrimaryWhen(Field<string> field, Func<string, bool>? isValid = null)
        {
            isValid ??= v => v.Trim().Length > 0;
            void Sync() => _dialog.IsPrimaryButtonEnabled = isValid(field.Value);

            switch (field.Control)
            {
                case Microsoft.UI.Xaml.Controls.TextBox box:
                    box.TextChanged += (_, _) => Sync();
                    break;
                case ComboBox combo:
                    combo.SelectionChanged += (_, _) => Sync();
                    break;
            }
            Sync();
            return this;
        }

        /// <summary>
        /// Focuses <paramref name="field"/> when the dialog opens, optionally selecting its text so
        /// typing replaces it — what a rename dialog wants, where the box is pre-filled with the
        /// current name.
        /// </summary>
        internal DialogBuilder FocusOnOpen(Field<string> field, bool selectAll = false)
        {
            _dialog.Opened += (_, _) =>
            {
                field.Control.Focus(FocusState.Programmatic);
                if (selectAll && field.Control is Microsoft.UI.Xaml.Controls.TextBox box)
                    box.SelectAll();
            };
            return this;
        }

        /// <summary>A multi-line text area (a commit or tag message).</summary>
        internal Field<string> MultilineTextBox(string header, double minHeight = 60,
                                                double maxHeight = 140)
        {
            var box = new Microsoft.UI.Xaml.Controls.TextBox
            {
                Header = header,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = minHeight,
                MaxHeight = maxHeight,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            _panel.Children.Add(box);
            return new Field<string>(box, () => box.Text ?? "");
        }

        /// <summary>
        /// Checks <paramref name="field"/> when the user submits, showing
        /// <paramref name="validate"/>'s message beneath it and keeping the dialog open.
        /// <para>
        /// Deliberately validate-on-submit rather than disabling the primary button: a greyed-out
        /// Save tells the user nothing about *why*. The message appears where the mistake is.
        /// </para>
        /// </summary>
        internal DialogBuilder ValidateOnSubmit(Field<string> field, Func<string, string?> validate)
        {
            var error = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Visibility = Visibility.Collapsed,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed),
            };
            _panel.Children.Add(error);

            _dialog.PrimaryButtonClick += (_, args) =>
            {
                string? problem = validate(field.Value);
                if (problem == null)
                    return;
                args.Cancel = true;
                error.Text = problem;
                error.Visibility = Visibility.Visible;
            };
            return this;
        }

        /// <summary>True when the user confirmed; read the fields afterwards.</summary>
        internal async Task<bool> ShowAsync()
            => await _dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
