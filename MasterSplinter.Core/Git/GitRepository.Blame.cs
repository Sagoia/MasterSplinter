using System;
using System.Collections.Generic;
using System.Globalization;
using MasterSplinter.Entrypoint.Interop;
using MasterSplinter.Entrypoint.Models;

namespace MasterSplinter.Entrypoint.Git
{
    // Blame, including the porcelain parser. Mirrors GitBackend.Blame.cpp.
    public sealed partial class GitRepository
    {
        // ---- Blame (Phase 8, BLAME-001) --------------------------------------------------------

        private static string MoveDetectionArg(BlameMoveDetection moves) => moves switch
        {
            BlameMoveDetection.WithinFile => "file",
            BlameMoveDetection.AcrossFiles => "commit",
            BlameMoveDetection.Aggressive => "any",
            _ => "none",
        };

        /// <summary>
        /// Per-line authorship for one file (BLAME-001). <paramref name="error"/> is null on
        /// success and carries git's own message otherwise ("no such path in HEAD", "binary file").
        /// </summary>
        public IReadOnlyList<BlameLine> Blame(string rev, string path, bool ignoreWhitespace,
                                              BlameMoveDetection moves, out string? error)
        {
            // Split on the FIRST separator only: the payload is file content and may well contain
            // 0x1F bytes of its own.
            string[] parts = NativeLogic
                .GitBlame(RootPath, rev, path, ignoreWhitespace, MoveDetectionArg(moves))
                .Split(US, 2);
            if (parts[0] != "OK")
            {
                error = parts.Length >= 2 && parts[1].Length > 0
                    ? parts[1]
                    : "Could not blame this file.";
                return Array.Empty<BlameLine>();
            }
            error = null;
            return ParsePorcelainBlame(parts.Length >= 2 ? parts[1] : "", path);
        }

        /// <summary>
        /// git blame --porcelain: a header line "&lt;sha&gt; &lt;origLine&gt; &lt;finalLine&gt;
        /// [&lt;groupSize&gt;]", then key/value headers, then the content line prefixed with a TAB.
        /// </summary>
        /// <remarks>
        /// The commit headers (author, summary, filename) appear only on a commit's FIRST group;
        /// every later group for the same commit carries the sha alone. Caching them per sha is
        /// therefore not an optimization — without it, most lines would render with a blank author.
        /// </remarks>
        internal static List<BlameLine> ParsePorcelainBlame(string porcelain, string blamedPath)
        {
            var lines = new List<BlameLine>();
            var known = new Dictionary<string, (string Author, string Email, DateTimeOffset When,
                                                string Summary)>(StringComparer.Ordinal);
            var filenames = new Dictionary<string, string>(StringComparer.Ordinal);

            string sha = "";
            int origLine = 0, finalLine = 0;
            string author = "", email = "", summary = "", filename = "";
            long authorTime = 0;
            int authorTzMinutes = 0;
            bool headerPending = false;     // we have a sha and are reading its key/value headers
            string previousSha = "";

            foreach (string raw in porcelain.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (line.Length == 0)
                    continue;

                if (line[0] == '\t')
                {
                    // The content line closes the group's header block.
                    if (headerPending && author.Length > 0)
                        known[sha] = (author, email,
                                      FromUnixWithOffset(authorTime, authorTzMinutes), summary);
                    if (headerPending && filename.Length > 0)
                        filenames[sha] = filename;

                    (string Author, string Email, DateTimeOffset When, string Summary) info =
                        known.TryGetValue(sha, out var cached)
                            ? cached
                            : ("", "", DateTimeOffset.MinValue, "");

                    lines.Add(new BlameLine(
                        sha,
                        Short(sha),
                        info.Author,
                        info.Email,
                        info.When,
                        info.Summary,
                        // Falls back to the blamed file: without -M/-C every line came from it,
                        // and the source path is what picks the syntax highlighter.
                        filenames.TryGetValue(sha, out string? f) ? f : blamedPath,
                        origLine,
                        finalLine,
                        line[1..],
                        sha != previousSha));

                    previousSha = sha;
                    headerPending = false;
                    continue;
                }

                if (!headerPending)
                {
                    // Header line: "<sha> <origLine> <finalLine> [<groupSize>]".
                    string[] head = line.Split(' ');
                    if (head.Length < 3)
                        continue;
                    sha = head[0];
                    int.TryParse(head[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out origLine);
                    int.TryParse(head[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out finalLine);
                    author = email = summary = filename = "";
                    authorTime = 0;
                    authorTzMinutes = 0;
                    headerPending = true;
                    continue;
                }

                int space = line.IndexOf(' ');
                string key = space < 0 ? line : line[..space];
                string value = space < 0 ? "" : line[(space + 1)..];
                switch (key)
                {
                    case "author": author = value; break;
                    case "author-mail": email = value.Trim('<', '>'); break;
                    case "author-time":
                        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out authorTime);
                        break;
                    case "author-tz": authorTzMinutes = ParseTimezoneMinutes(value); break;
                    case "summary": summary = value; break;
                    case "filename": filename = value; break;
                }
            }
            return lines;
        }

        // "+0200" / "-0730" as minutes. Anything unrecognized means UTC, which shifts the displayed
        // time but never loses the date the way a throw would lose the whole file.
        private static int ParseTimezoneMinutes(string tz)
        {
            if (tz.Length != 5 || (tz[0] != '+' && tz[0] != '-'))
                return 0;
            if (!int.TryParse(tz.AsSpan(1, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out int h) ||
                !int.TryParse(tz.AsSpan(3, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out int m))
                return 0;
            int total = h * 60 + m;
            return tz[0] == '-' ? -total : total;
        }

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
                // A corrupt author-time or a >14h offset must cost one line's timestamp, not the file.
                return DateTimeOffset.MinValue;
            }
        }
    }
}
