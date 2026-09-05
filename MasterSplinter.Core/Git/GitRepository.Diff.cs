using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using MasterSplinter.Entrypoint.Interop;
using MasterSplinter.Entrypoint.Models;

namespace MasterSplinter.Entrypoint.Git
{
    // Changed-file lists, diff stats, unified diffs and raw file content.
    // Mirrors GitBackend.Diff.cpp.
    public sealed partial class GitRepository
    {
        // ---- Changed files (single commit or a..b range) ---------------------------------------

        public IReadOnlyList<ChangedFile> ChangedFiles(string sha)
            => ParseNameStatus(NativeLogic.GitCommitFiles(RootPath, sha));

        /// <summary>Files changed between two commits/refs (DIFF-006 / DIFF-007).</summary>
        public IReadOnlyList<ChangedFile> ChangedFilesRange(string a, string b)
            => ParseNameStatus(NativeLogic.GitRangeFiles(RootPath, a, b));

        internal static IReadOnlyList<ChangedFile> ParseNameStatus(string raw)
        {
            var files = new List<ChangedFile>();
            // Line-based "status<TAB>path" (rename/copy is "R100<TAB>old<TAB>new").
            foreach (string line in raw.Split('\n'))
            {
                string l = line.Trim('\r');
                if (l.Length == 0)
                    continue;

                string[] parts = l.Split('\t');
                if (parts.Length < 2)
                    continue;

                string status = parts[0];
                // For rename/copy, diff + show should target the new path (the last field).
                string path = status.Length > 0 && status[0] is 'R' or 'C' && parts.Length >= 3
                    ? parts[2]
                    : parts[1];

                files.Add(new ChangedFile { Path = path, Status = MapStatus(status[0]) });
            }
            return files;
        }

        internal static FileChangeStatus MapStatus(char c) => c switch
        {
            'A' => FileChangeStatus.Added,
            'D' => FileChangeStatus.Deleted,
            'R' => FileChangeStatus.Renamed,
            'C' => FileChangeStatus.Renamed,
            '?' => FileChangeStatus.Untracked,
            'U' => FileChangeStatus.Conflicted,
            _ => FileChangeStatus.Modified, // M, T, ...
        };

        /// <summary>
        /// MERGE-003. The seven porcelain-v1 code pairs that mean "unmerged", per git's own list:
        /// DD (both deleted), AU (added by us), UD (deleted by them), UA (added by them),
        /// DU (deleted by us), AA (both added), UU (both modified).
        ///
        /// This has to be checked BEFORE the staged/unstaged split, because those two halves both
        /// see a non-blank column here — a plain "UU" would otherwise be reported as a staged
        /// modification AND an unstaged one, i.e. the same conflicted file listed twice with no
        /// hint that anything is wrong.
        /// </summary>
        internal static bool IsUnmerged(char x, char y)
            => (x, y) is ('D', 'D') or ('A', 'U') or ('U', 'D') or ('U', 'A')
                      or ('D', 'U') or ('A', 'A') or ('U', 'U');

        // ---- Diff summary stats (DIFF-001) -----------------------------------------------------

        public DiffStat CommitStat(string sha) => ParseShortStat(NativeLogic.GitCommitShortStat(RootPath, sha));
        public DiffStat RangeStat(string a, string b) => ParseShortStat(NativeLogic.GitRangeShortStat(RootPath, a, b));

        // " 3 files changed, 12 insertions(+), 4 deletions(-)" — each clause is optional.
        private static readonly Regex FilesRe = new(@"(\d+)\s+files?\s+changed", RegexOptions.Compiled);
        private static readonly Regex InsertRe = new(@"(\d+)\s+insertions?\(\+\)", RegexOptions.Compiled);
        private static readonly Regex DeleteRe = new(@"(\d+)\s+deletions?\(-\)", RegexOptions.Compiled);

        internal static DiffStat ParseShortStat(string raw)
            => new(MatchInt(FilesRe, raw), MatchInt(InsertRe, raw), MatchInt(DeleteRe, raw));

        private static int MatchInt(Regex re, string s)
        {
            Match m = re.Match(s);
            return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        }

        // ---- Diff for one file (single commit or a..b range) -----------------------------------

        private static readonly Regex HunkRe =
            new(@"^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@", RegexOptions.Compiled);

        // A COMBINED diff (git's `--cc` output, which is what an unmerged path produces) has one
        // extra '@' and one extra "-<start>,<len>" per parent: "@@@ -1,3 -1,3 +1,7 @@@". The last
        // range is still the result side. Matching it is what stops a conflicted file's diff pane
        // from rendering empty.
        private static readonly Regex CombinedHunkRe =
            new(@"^(@{3,}) (?:-(\d+)(?:,\d+)? )+\+(\d+)(?:,\d+)? @{3,}", RegexOptions.Compiled);

        public (List<DiffLine> Lines, bool IsBinary) FileDiff(string sha, string path, WhitespaceMode ws)
            => ParseUnifiedDiff(NativeLogic.GitFileDiff(RootPath, sha, path, WsFlag(ws)));

        public (List<DiffLine> Lines, bool IsBinary) RangeDiff(string a, string b, string path, WhitespaceMode ws)
            => ParseUnifiedDiff(NativeLogic.GitRangeFileDiff(RootPath, a, b, path, WsFlag(ws)));

        /// <summary>Diff for one working-tree file (STATUS-005). The area picks worktree-vs-index,
        /// index-vs-HEAD (staged), or an all-added synthesized diff for untracked files.</summary>
        public (List<DiffLine> Lines, bool IsBinary) WorkTreeDiff(string path, WorkTreeArea area, WhitespaceMode ws)
            => ParseUnifiedDiff(NativeLogic.GitWorkTreeFileDiff(RootPath, path, AreaFlag(area), WsFlag(ws)));

        // The native ABI's area contract (0 = unstaged, 1 = staged, 2 = untracked) is independent
        // of the C# enum's declaration order — map explicitly, like WsFlag.
        internal static int AreaFlag(WorkTreeArea a) => a switch
        {
            WorkTreeArea.Staged => 1,
            WorkTreeArea.Untracked => 2,
            _ => 0,
        };

        internal static int WsFlag(WhitespaceMode m) => m switch
        {
            WhitespaceMode.IgnoreChange => 1,
            WhitespaceMode.IgnoreAll => 2,
            _ => 0,
        };

        internal static (List<DiffLine> Lines, bool IsBinary) ParseUnifiedDiff(string raw)
        {
            // A binary file's patch has no hunks, just a "Binary files ... differ" / binary-patch marker.
            bool isBinary = raw.Contains("Binary files ", StringComparison.Ordinal)
                         || raw.Contains("GIT binary patch", StringComparison.Ordinal);

            var lines = new List<DiffLine>();
            int oldNo = 0, newNo = 0;
            bool inHunk = false;
            // >0 once a combined hunk header has been seen: the number of leading marker columns
            // (one per parent) each body line carries. 0 means an ordinary two-way diff.
            int markerColumns = 0;

            foreach (string line in raw.Split('\n'))
            {
                string l = line.TrimEnd('\r');

                if (l.StartsWith("@@", StringComparison.Ordinal))
                {
                    Match combined = CombinedHunkRe.Match(l);
                    if (combined.Success)
                    {
                        // "@@@" -> 2 parents -> 2 marker columns, "@@@@" -> 3, and so on.
                        markerColumns = combined.Groups[1].Value.Length - 1;
                        // Captures[0] is the first parent's range; either side is as good a base as
                        // the other for the left gutter, and git prints them in parent order.
                        oldNo = int.Parse(combined.Groups[2].Captures[0].Value, CultureInfo.InvariantCulture);
                        newNo = int.Parse(combined.Groups[3].Value, CultureInfo.InvariantCulture);
                    }
                    else
                    {
                        markerColumns = 0;
                        Match m = HunkRe.Match(l);
                        if (m.Success)
                        {
                            oldNo = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                            newNo = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                        }
                    }
                    inHunk = true;
                    lines.Add(new DiffLine { Kind = DiffLineKind.Hunk, Text = l });
                    continue;
                }

                if (!inHunk)
                    continue; // skip the "diff --git / index / --- / +++" file header block

                if (l.StartsWith("\\", StringComparison.Ordinal))
                    continue; // "\ No newline at end of file"

                // git always writes a marker column, so a BLANK context line arrives as " " (and
                // becomes "" only after the marker is stripped). A zero-length line here is
                // therefore never content: it is the empty tail left behind when the diff's
                // final newline is split. Emitting it added a phantom blank row, carrying a
                // line number, to the end of every diff.
                if (l.Length == 0)
                    continue;

                if (markerColumns > 0)
                {
                    lines.Add(ParseCombinedLine(l, markerColumns, ref oldNo, ref newNo));
                    continue;
                }

                if (l.StartsWith("+", StringComparison.Ordinal))
                {
                    lines.Add(new DiffLine { Kind = DiffLineKind.Added, NewNo = newNo.ToString(), Text = l[1..] });
                    newNo++;
                }
                else if (l.StartsWith("-", StringComparison.Ordinal))
                {
                    lines.Add(new DiffLine { Kind = DiffLineKind.Removed, OldNo = oldNo.ToString(), Text = l[1..] });
                    oldNo++;
                }
                else
                {
                    // context line (leading space) or a blank line within the hunk
                    string text = l.StartsWith(" ", StringComparison.Ordinal) ? l[1..] : l;
                    lines.Add(new DiffLine
                    {
                        Kind = DiffLineKind.Context,
                        OldNo = oldNo.ToString(),
                        NewNo = newNo.ToString(),
                        Text = text,
                    });
                    oldNo++;
                    newNo++;
                }
            }
            return (lines, isBinary);
        }

        /// <summary>
        /// One body line of a combined diff. The first <paramref name="markers"/> characters are
        /// one marker per parent — '-' where the line is absent from that parent's side, '+' where
        /// it is new relative to it, ' ' where it is unchanged — and the text starts after them.
        /// A line removed from any parent is not in the merged result, so it only advances the old
        /// counter; everything else lands in the result and advances the new one.
        /// </summary>
        private static DiffLine ParseCombinedLine(string line, int markers, ref int oldNo, ref int newNo)
        {
            string prefix = line.Length >= markers ? line[..markers] : line.PadRight(markers);
            string text = line.Length > markers ? line[markers..] : "";

            if (prefix.Contains('-'))
            {
                var removed = new DiffLine
                {
                    Kind = DiffLineKind.Removed,
                    OldNo = oldNo.ToString(CultureInfo.InvariantCulture),
                    Text = text,
                };
                oldNo++;
                return removed;
            }

            if (prefix.Contains('+'))
            {
                var added = new DiffLine
                {
                    Kind = DiffLineKind.Added,
                    NewNo = newNo.ToString(CultureInfo.InvariantCulture),
                    Text = text,
                };
                newNo++;
                return added;
            }

            var context = new DiffLine
            {
                Kind = DiffLineKind.Context,
                OldNo = oldNo.ToString(CultureInfo.InvariantCulture),
                NewNo = newNo.ToString(CultureInfo.InvariantCulture),
                Text = text,
            };
            oldNo++;
            newNo++;
            return context;
        }

        // ---- File content at a commit ----------------------------------------------------------

        public string FileAt(string sha, string path) => NativeLogic.GitFileAtCommit(RootPath, sha, path);

        /// <summary>Raw bytes of a file at a commit/ref (binary-safe), for image previews (DIFF-005).</summary>
        public byte[] FileBytesAt(string sha, string path) => NativeLogic.GitFileBytesAtCommit(RootPath, sha, path);
    }
}
