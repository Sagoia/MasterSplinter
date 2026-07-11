using System;
using System.Diagnostics;

namespace MasterSplinter.Entrypoint.Infrastructure
{
    /// <summary>Launches external tools for working-copy files (STATUS-006/007).</summary>
    public static class EditorLauncher
    {
        /// <summary>
        /// Opens a file in the configured editor. The command template may contain "{path}"
        /// (replaced with the absolute path); when the template is blank the file opens with its
        /// shell association. Returns an error message, or null on success.
        /// </summary>
        public static string? OpenInEditor(string commandTemplate, string absPath)
        {
            try
            {
                string template = (commandTemplate ?? "").Trim();
                if (template.Length == 0)
                {
                    Process.Start(new ProcessStartInfo(absPath) { UseShellExecute = true });
                    return null;
                }

                string command = template.Contains("{path}", StringComparison.OrdinalIgnoreCase)
                    ? template.Replace("{path}", absPath, StringComparison.OrdinalIgnoreCase)
                    : $"{template} \"{absPath}\"";

                var (exe, args) = SplitCommand(command);
                if (exe.Length == 0)
                    return "The editor command is empty.";
                // UseShellExecute: launching e.g. "notepad.exe" via bare CreateProcess from a
                // packaged app hits the App Execution Alias shim and dies windowless; ShellExecute
                // resolves aliases and store-app activation correctly.
                Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true });
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        /// <summary>Opens a File Explorer window with the file selected (STATUS-007).</summary>
        public static string? RevealInExplorer(string absPath)
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{absPath}\"")
                {
                    UseShellExecute = false,
                });
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        // First token (respecting a leading quoted exe path) is the executable; the rest is args.
        private static (string Exe, string Args) SplitCommand(string command)
        {
            string c = command.Trim();
            if (c.StartsWith('"'))
            {
                int close = c.IndexOf('"', 1);
                if (close > 0)
                    return (c[1..close], c[(close + 1)..].Trim());
            }
            int space = c.IndexOf(' ');
            return space < 0 ? (c, "") : (c[..space], c[(space + 1)..].Trim());
        }
    }
}
