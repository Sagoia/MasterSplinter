using System;
using System.Globalization;
using System.IO;
using MasterSplinter.Entrypoint.Interop;
using MasterSplinter.Entrypoint.Models;

namespace MasterSplinter.Entrypoint.Git
{
    /// <summary>
    /// A view of one git repository: reads (log, refs, diffs, status) plus the Phase 4 staging
    /// and commit mutations. Owns parsing of the delimited UTF-8 streams the native core
    /// (<see cref="NativeLogic"/>) produces by shelling out to git.exe. All methods here are
    /// synchronous and may be slow; callers should invoke them off the UI thread.
    /// </summary>
    public sealed partial class GitRepository
    {
        private const char US = '\x1f'; // field separator
        private const char RS = '\x1e'; // record separator

        public string RootPath { get; }
        public string Name { get; }
        public string Branch { get; }

        private GitRepository(string rootPath, string name, string branch)
        {
            RootPath = rootPath;
            Name = name;
            Branch = branch;
        }

        public RepositoryInfo ToInfo() => new() { Name = Name, Branch = Branch, RootPath = RootPath };

        /// <summary>Validate and open a repository. Returns null and sets <paramref name="error"/> on failure.</summary>
        public static GitRepository? Open(string path, out string? error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(path))
            {
                error = "No folder was provided.";
                return null;
            }

            string result = NativeLogic.GitOpenRepository(path);
            string[] parts = result.Split(US);
            if (parts.Length >= 2 && parts[0] == "ERR")
            {
                error = parts[1];
                return null;
            }
            if (parts.Length < 3 || parts[0] != "OK")
            {
                error = "Could not read the repository (is git installed and on PATH?).";
                return null;
            }

            string root = parts[1];
            string branch = parts[2];
            string name = SafeLeafName(root);
            return new GitRepository(root, name, branch);
        }

        private static string SafeLeafName(string root)
        {
            try
            {
                string trimmed = root.TrimEnd('/', '\\');
                string leaf = Path.GetFileName(trimmed);
                return string.IsNullOrEmpty(leaf) ? trimmed : leaf;
            }
            catch { return root; }
        }

        // ---- Helpers ---------------------------------------------------------------------------

        internal static string Short(string hash) => hash.Length > 7 ? hash[..7] : hash;

        /// <summary>
        /// Builds a timestamp from the unix seconds + signed minute offset the packed log and
        /// blame records carry. Lives here rather than in either area file because both use it —
        /// see the split trap in docs/refactoring.md.
        /// </summary>
        private static DateTimeOffset FromUnixWithOffset(long unixSeconds, int offsetMinutes)
        {
            if (unixSeconds <= 0)
                return DateTimeOffset.MinValue;
            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(unixSeconds)
                                     .ToOffset(TimeSpan.FromMinutes(offsetMinutes));
            }
            catch (ArgumentOutOfRangeException)
            {
                // A corrupt timestamp or a >14h offset must cost one row's date, not the whole list.
                return DateTimeOffset.MinValue;
            }
        }

        private static string FormatDate(DateTimeOffset d)
            => d == DateTimeOffset.MinValue
                ? ""
                : d.ToLocalTime().ToString("d MMM yyyy H:mm", CultureInfo.CurrentCulture);
    }
}
