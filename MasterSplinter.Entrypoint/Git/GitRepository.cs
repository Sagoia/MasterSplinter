using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MasterSplinter.Entrypoint.Interop;
using MasterSplinter.Entrypoint.Models;

namespace MasterSplinter.Entrypoint.Git
{
    /// <summary>
    /// A read-only view of one git repository. Owns parsing of the delimited UTF-8 streams the
    /// native core (<see cref="NativeLogic"/>) produces by shelling out to git.exe. All methods
    /// here are synchronous and may be slow; callers should invoke them off the UI thread.
    /// </summary>
    public sealed class GitRepository
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

        // ---- Commit history --------------------------------------------------------------------

        public IReadOnlyList<CommitRow> Log(int order, int maxCount)
        {
            string raw = NativeLogic.GitLog(RootPath, order, maxCount);
            var commits = new List<CommitRow>();
            foreach (string rec in raw.Split(RS))
            {
                string record = rec.TrimStart('\n', '\r');
                if (record.Length == 0)
                    continue;

                string[] f = record.Split(US);
                if (f.Length < 12)
                    continue;

                var parents = f[2].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var row = new CommitRow
                {
                    FullHash = f[0],
                    Hash = f[1],
                    ParentHashes = parents,
                    Parents = parents.Length == 0
                        ? "—"
                        : string.Join(", ", parents.Select(Short)),
                    Author = f[3],
                    AuthorEmail = f[4],
                    AuthorDate = ParseDate(f[5]),
                    Committer = f[6],
                    CommitterEmail = f[7],
                    CommitDate = ParseDate(f[8]),
                    Message = f[10],
                    Body = f[11].TrimEnd('\n', '\r'),
                };
                row.Date = FormatDate(row.AuthorDate);
                row.CommitterDate = FormatDate(row.CommitDate);
                foreach (var badge in ParseDecorations(f[9]))
                    row.Badges.Add(badge);
                row.Graph = SimpleGraph();
                commits.Add(row);
            }
            return commits;
        }

        /// <summary>Phase 1 graph: a single lane with a dot — connects vertically across rows.</summary>
        private static GraphRow SimpleGraph()
            => new GraphBuilder(1).V(0, GraphColor.Blue).Dot(0, GraphColor.Blue).Done();

        private static IEnumerable<Badge> ParseDecorations(string decorations)
        {
            if (string.IsNullOrWhiteSpace(decorations))
                yield break;

            foreach (string rawToken in decorations.Split(','))
            {
                string token = rawToken.Trim();
                if (token.Length == 0)
                    continue;

                if (token.StartsWith("tag:", StringComparison.Ordinal))
                {
                    yield return new Badge { Kind = BadgeKind.Tag, Text = token["tag:".Length..].Trim() };
                }
                else if (token.Contains("->"))
                {
                    // "HEAD -> main": HEAD pointer plus the local branch it points at.
                    int arrow = token.IndexOf("->", StringComparison.Ordinal);
                    string left = token[..arrow].Trim();
                    string right = token[(arrow + 2)..].Trim();
                    yield return new Badge { Kind = BadgeKind.Head, Text = left };
                    if (right.Length > 0)
                        yield return new Badge { Kind = BadgeKind.LocalBranch, Text = right };
                }
                else if (token == "HEAD")
                {
                    yield return new Badge { Kind = BadgeKind.Head, Text = "HEAD" };
                }
                else if (token.Contains('/'))
                {
                    yield return new Badge { Kind = BadgeKind.RemoteBranch, Text = token };
                }
                else
                {
                    yield return new Badge { Kind = BadgeKind.LocalBranch, Text = token };
                }
            }
        }

        // ---- Refs (sidebar) --------------------------------------------------------------------

        public sealed record Refs(List<string> Branches, List<string> Tags, List<string> Remotes);

        public Refs ListRefs()
        {
            string raw = NativeLogic.GitRefs(RootPath);
            var branches = new List<string>();
            var tags = new List<string>();
            var remotes = new List<string>();
            foreach (string rec in raw.Split('\n'))
            {
                string r = rec.Trim('\n', '\r', ' ');
                if (r.Length == 0)
                    continue;
                if (r.StartsWith("refs/heads/", StringComparison.Ordinal))
                    branches.Add(r["refs/heads/".Length..]);
                else if (r.StartsWith("refs/tags/", StringComparison.Ordinal))
                    tags.Add(r["refs/tags/".Length..]);
                else if (r.StartsWith("refs/remotes/", StringComparison.Ordinal))
                    remotes.Add(r["refs/remotes/".Length..]);
            }
            return new Refs(branches, tags, remotes);
        }

        // ---- Changed files (single commit or a..b range) ---------------------------------------

        public IReadOnlyList<ChangedFile> ChangedFiles(string sha)
            => ParseNameStatus(NativeLogic.GitCommitFiles(RootPath, sha));

        /// <summary>Files changed between two commits/refs (DIFF-006 / DIFF-007).</summary>
        public IReadOnlyList<ChangedFile> ChangedFilesRange(string a, string b)
            => ParseNameStatus(NativeLogic.GitRangeFiles(RootPath, a, b));

        private static IReadOnlyList<ChangedFile> ParseNameStatus(string raw)
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

        private static FileChangeStatus MapStatus(char c) => c switch
        {
            'A' => FileChangeStatus.Added,
            'D' => FileChangeStatus.Deleted,
            'R' => FileChangeStatus.Renamed,
            'C' => FileChangeStatus.Renamed,
            _ => FileChangeStatus.Modified, // M, T, U, ...
        };

        // ---- Diff summary stats (DIFF-001) -----------------------------------------------------

        public DiffStat CommitStat(string sha) => ParseShortStat(NativeLogic.GitCommitShortStat(RootPath, sha));
        public DiffStat RangeStat(string a, string b) => ParseShortStat(NativeLogic.GitRangeShortStat(RootPath, a, b));

        // " 3 files changed, 12 insertions(+), 4 deletions(-)" — each clause is optional.
        private static readonly Regex FilesRe = new(@"(\d+)\s+files?\s+changed", RegexOptions.Compiled);
        private static readonly Regex InsertRe = new(@"(\d+)\s+insertions?\(\+\)", RegexOptions.Compiled);
        private static readonly Regex DeleteRe = new(@"(\d+)\s+deletions?\(-\)", RegexOptions.Compiled);

        private static DiffStat ParseShortStat(string raw)
            => new(MatchInt(FilesRe, raw), MatchInt(InsertRe, raw), MatchInt(DeleteRe, raw));

        private static int MatchInt(Regex re, string s)
        {
            Match m = re.Match(s);
            return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        }

        // ---- Diff for one file (single commit or a..b range) -----------------------------------

        private static readonly Regex HunkRe =
            new(@"^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@", RegexOptions.Compiled);

        public (List<DiffLine> Lines, bool IsBinary) FileDiff(string sha, string path, WhitespaceMode ws)
            => ParseUnifiedDiff(NativeLogic.GitFileDiff(RootPath, sha, path, WsFlag(ws)));

        public (List<DiffLine> Lines, bool IsBinary) RangeDiff(string a, string b, string path, WhitespaceMode ws)
            => ParseUnifiedDiff(NativeLogic.GitRangeFileDiff(RootPath, a, b, path, WsFlag(ws)));

        private static int WsFlag(WhitespaceMode m) => m switch
        {
            WhitespaceMode.IgnoreChange => 1,
            WhitespaceMode.IgnoreAll => 2,
            _ => 0,
        };

        private static (List<DiffLine> Lines, bool IsBinary) ParseUnifiedDiff(string raw)
        {
            // A binary file's patch has no hunks, just a "Binary files ... differ" / binary-patch marker.
            bool isBinary = raw.Contains("Binary files ", StringComparison.Ordinal)
                         || raw.Contains("GIT binary patch", StringComparison.Ordinal);

            var lines = new List<DiffLine>();
            int oldNo = 0, newNo = 0;
            bool inHunk = false;

            foreach (string line in raw.Split('\n'))
            {
                string l = line.TrimEnd('\r');

                if (l.StartsWith("@@", StringComparison.Ordinal))
                {
                    Match m = HunkRe.Match(l);
                    if (m.Success)
                    {
                        oldNo = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                        newNo = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                    }
                    inHunk = true;
                    lines.Add(new DiffLine { Kind = DiffLineKind.Hunk, Text = l });
                    continue;
                }

                if (!inHunk)
                    continue; // skip the "diff --git / index / --- / +++" file header block

                if (l.StartsWith("\\", StringComparison.Ordinal))
                    continue; // "\ No newline at end of file"

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

        // ---- File content at a commit ----------------------------------------------------------

        public string FileAt(string sha, string path) => NativeLogic.GitFileAtCommit(RootPath, sha, path);

        /// <summary>Raw bytes of a file at a commit/ref (binary-safe), for image previews (DIFF-005).</summary>
        public byte[] FileBytesAt(string sha, string path) => NativeLogic.GitFileBytesAtCommit(RootPath, sha, path);

        // ---- Helpers ---------------------------------------------------------------------------

        private static string Short(string hash) => hash.Length > 7 ? hash[..7] : hash;

        private static DateTimeOffset ParseDate(string iso)
            => DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture,
                   DateTimeStyles.AssumeUniversal, out var d)
                ? d
                : DateTimeOffset.MinValue;

        private static string FormatDate(DateTimeOffset d)
            => d == DateTimeOffset.MinValue
                ? ""
                : d.ToLocalTime().ToString("d MMM yyyy H:mm", CultureInfo.CurrentCulture);
    }
}
