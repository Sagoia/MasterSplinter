using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MasterSplinter.Entrypoint.Infrastructure;

namespace MasterSplinter.Entrypoint.Controls
{
    /// <summary>
    /// REMOTE-007: the modal that fronts a long-running git command. Streams git's output into a
    /// monospace log as it arrives, offers Cancel while the command runs, and — when the command
    /// does not succeed — stays open with the raw output plus an actionable hint (REMOTE-009).
    ///
    /// Phase 7 shares it with merge/rebase/cherry-pick/revert, which is why it is no longer named
    /// for the remote commands. Those can end in a third way: stopped on a conflict, which is a
    /// normal outcome and is titled as such rather than as a failure.
    ///
    /// Built in code rather than XAML to match the other dialogs in this project
    /// (see RepositoryWorkspace's create-branch / create-tag dialogs).
    /// </summary>
    public static class GitProgressDialog
    {
        /// <summary>
        /// Shows the dialog, runs <paramref name="operation"/>, and returns git's error text
        /// (null on success). The dialog closes itself on success; on failure it waits for the
        /// user to dismiss it, so the output can be read.
        /// </summary>
        public static async Task<string?> RunAsync(
            XamlRoot? xamlRoot,
            string title,
            string commandLabel,
            Func<IProgress<string>, CancellationToken, Task<string?>> operation)
        {
            var log = new ProgressLog();
            // Seeded with the command being run, so the log reads like a terminal session and the
            // user can see exactly what was invoked on their behalf.
            log.Append("$ " + commandLabel + "\n");
            using var cts = new CancellationTokenSource();

            var output = new TextBlock
            {
                Text = log.Text,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                IsTextSelectionEnabled = true,
                // Wrapped, not scrolled sideways: git's progress lines are short, but its error
                // text is not, and a message the user has to scroll to read is a message they
                // will not read.
                TextWrapping = TextWrapping.Wrap,
            };
            var scroller = new ScrollViewer
            {
                Content = output,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                // A MinWidth wider than the dialog can actually get (small window, high DPI)
                // pushes the wrapped text past the dialog's clip, cutting long error lines
                // mid-word with no scrollbar to recover them. Keep the floor modest.
                MinWidth = 380,
                MaxWidth = 820,
                Height = 260,
            };
            // Starts indeterminate and switches to a real 0-100 bar as soon as git reports a
            // percentage. TortoiseGit leaves its bar determinate the whole time, but an empty
            // determinate bar during a multi-second SSH handshake (which prints nothing at all)
            // reads as "hung" rather than "connecting".
            var bar = new ProgressBar
            {
                IsIndeterminate = true,
                Minimum = 0,
                Maximum = 100,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            // git's work comes in phases ("Counting objects", "Compressing objects", "Receiving
            // objects", "Resolving deltas"), each running 0-100 in turn. Naming the current one is
            // what stops the bar's resets from looking like a glitch.
            var phase = new TextBlock
            {
                FontSize = 12,
                Opacity = 0.8,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Visibility = Visibility.Collapsed,
            };
            var hint = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontWeight = FontWeights.SemiBold,
                Visibility = Visibility.Collapsed,
                MaxWidth = 820,
            };

            var panel = new StackPanel { Spacing = 10 };
            panel.Children.Add(phase);
            panel.Children.Add(bar);
            panel.Children.Add(hint);
            panel.Children.Add(scroller);

            // Fires for every line git terminates, including the CR-rewritten progress lines that
            // never survive in the log — those are precisely the ones carrying the percentages.
            log.LineCompleted += line =>
            {
                var parsed = ParseProgressLine(line);
                if (parsed == null)
                    return;
                (string label, int percent) = parsed.Value;
                phase.Text = $"{label} — {percent}%";
                phase.Visibility = Visibility.Visible;
                // Like TortoiseGit, only move on a real value: a line without a percentage should
                // leave the bar where it is rather than snap it back to zero.
                if (percent > 0)
                {
                    bar.IsIndeterminate = false;
                    bar.Value = percent;
                }
            };

            var dialog = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = title,
                Content = panel,
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };

            bool running = true;

            // While the command runs, dismissing the dialog means "cancel it" — not "walk away
            // and leave a git process behind". The dialog closes itself once the command actually
            // stops, which for a network command can take a moment.
            dialog.Closing += (_, args) =>
            {
                if (!running)
                    return;
                args.Cancel = true;
                if (!cts.IsCancellationRequested)
                {
                    cts.Cancel();
                    dialog.CloseButtonText = "Cancelling…";
                    log.Append("Cancelling…\n");
                    Render(output, scroller, log);
                }
            };

            // Whether git itself said anything. This is what decides if the failure text needs
            // appending below — see the comment there.
            bool streamedAnything = false;

            // Reported from the git thread; Progress<T> was constructed here, on the UI thread,
            // so the handler runs on the UI thread and may touch these controls directly.
            var progress = new Progress<string>(chunk =>
            {
                streamedAnything = true;
                log.Append(chunk);
                Render(output, scroller, log);
            });

            string? error = null;
            var finished = new TaskCompletionSource<bool>();

            dialog.Opened += async (_, _) =>
            {
                try { error = await operation(progress, cts.Token); }
                catch (Exception ex) { error = ex.Message; }

                running = false;
                bar.IsIndeterminate = false;
                phase.Visibility = Visibility.Collapsed;

                if (cts.IsCancellationRequested)
                {
                    // A cancelled command reports whatever git had managed to say, which is noise
                    // here — the user knows why it stopped.
                    error = null;
                    dialog.Hide();
                }
                else if (error == null)
                {
                    dialog.Hide();
                }
                else
                {
                    // A conflict is not a failure — git did exactly what it was asked and stopped
                    // where it had to. Colouring and titling it like a crash sends the user looking
                    // for something broken instead of for the files to resolve.
                    bool conflict = GitErrorHints.IsConflict(error);

                    // TortoiseGit's PBST_ERROR: the bar stays, full and coloured, rather than
                    // vanishing. A bar that disappears looks like the operation is still starting.
                    bar.Value = 100;
                    bar.Foreground = new SolidColorBrush(
                        conflict ? Microsoft.UI.Colors.Goldenrod : Microsoft.UI.Colors.OrangeRed);

                    string? actionable = GitErrorHints.HintFor(error);
                    if (actionable != null)
                    {
                        hint.Text = actionable;
                        hint.Visibility = Visibility.Visible;
                    }
                    // The ERR payload IS git's merged output, so once anything has been streamed
                    // the log already holds it — appending unconditionally printed the whole
                    // failure twice. It is only new text when nothing was streamed at all: a
                    // backend guard that never spawned git, an exception, or a git that failed
                    // silently and left only OkOrErr's fallback message.
                    if (!streamedAnything)
                    {
                        log.Append("\n" + error.Trim() + "\n");
                        Render(output, scroller, log);
                    }
                    dialog.Title = title + (conflict ? " — conflicts" : " — failed");
                    dialog.CloseButtonText = "Close";
                }
                finished.TrySetResult(true);
            };

            await dialog.ShowAsync();
            await finished.Task;
            return error;
        }

        /// <summary>
        /// The phase name and percentage from a git progress line ("Receiving objects:  45%
        /// (450/1000)" → "Receiving objects", 45), or null when the line carries no progress.
        ///
        /// Deliberately the same shape as TortoiseGit's CProgressDlg::ParserCmdOutput: a line is a
        /// progress line when it has both a colon and a '%', the label is everything before the
        /// colon, and the value is the digit run ending at the '%'. Searching for the colon
        /// *before* the '%' is the one deviation — TortoiseGit's plain ReverseFind would fold a
        /// later colon (a "1.2 MiB | 500 KiB/s" tail) into the label.
        /// </summary>
        private static (string Label, int Percent)? ParseProgressLine(string line)
        {
            int pct = line.IndexOf('%');
            if (pct <= 0)
                return null;
            int colon = line.LastIndexOf(':', pct);
            if (colon <= 0)
                return null;

            int start = pct;
            while (start > 0 && char.IsAsciiDigit(line[start - 1]))
                start--;
            if (start == pct)
                return null; // a '%' with no number in front of it

            return int.TryParse(line.AsSpan(start, pct - start), out int percent)
                ? (line[..colon].Trim(), percent)
                : null;
        }

        private static void Render(TextBlock output, ScrollViewer scroller, ProgressLog log)
        {
            output.Text = log.Text;
            // ScrollableHeight only reflects the new text after a layout pass.
            scroller.UpdateLayout();
            scroller.ChangeView(null, scroller.ScrollableHeight, null, true);
        }

        /// <summary>
        /// Accumulates git's output for display.
        ///
        /// Git draws progress by rewriting one line: "Receiving objects: 12%…\rReceiving objects:
        /// 34%…\r". Appending that verbatim produces hundreds of near-identical lines, so a bare
        /// CR is treated the way a terminal treats it — return to the start of the line and
        /// overwrite — while CRLF stays a single line break. Chunk boundaries can fall between the
        /// CR and the LF, hence the pending-CR flag.
        /// </summary>
        private sealed class ProgressLog
        {
            // Enough to cover a long fetch; a clone of a huge repository would otherwise grow the
            // TextBlock without bound.
            private const int MaxLines = 500;

            private readonly List<string> _lines = new();
            private readonly StringBuilder _current = new();
            private bool _pendingCr;

            /// <summary>Raised for every line git finishes, whether it is kept or immediately
            /// overwritten by the next CR. The overwritten ones are the progress lines, so this is
            /// the only place a percentage can be read from.</summary>
            public event Action<string>? LineCompleted;

            public string Text => _lines.Count == 0
                ? _current.ToString()
                : string.Join("\n", _lines) + "\n" + _current;

            public void Append(string chunk)
            {
                foreach (char c in chunk)
                {
                    if (_pendingCr)
                    {
                        _pendingCr = false;
                        if (c == '\n')
                        {
                            EndLine(keep: true); // CRLF: one line break
                            continue;
                        }
                        EndLine(keep: false); // bare CR: this line is being rewritten
                    }

                    if (c == '\r')
                        _pendingCr = true;
                    else if (c == '\n')
                        EndLine(keep: true);
                    else
                        _current.Append(c);
                }
            }

            private void EndLine(bool keep)
            {
                string line = _current.ToString();
                _current.Clear();
                if (keep)
                {
                    _lines.Add(line);
                    if (_lines.Count > MaxLines)
                        _lines.RemoveRange(0, _lines.Count - MaxLines);
                }
                LineCompleted?.Invoke(line);
            }
        }
    }
}
