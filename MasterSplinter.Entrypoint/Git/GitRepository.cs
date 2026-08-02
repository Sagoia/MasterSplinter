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
    /// A view of one git repository: reads (log, refs, diffs, status) plus the Phase 4 staging
    /// and commit mutations. Owns parsing of the delimited UTF-8 streams the native core
    /// (<see cref="NativeLogic"/>) produces by shelling out to git.exe. All methods here are
    /// synchronous and may be slow; callers should invoke them off the UI thread.
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

        public sealed record RefList(List<BranchInfo> Branches, List<TagInfo> Tags,
                                     List<RemoteBranchInfo> Remotes);

        // "ahead 2, behind 1" from %(upstream:track,nobracket). Anything unrecognized leaves the
        // counts at 0 — a branch then simply shows no arrows, never a wrong number.
        private static readonly Regex AheadRe = new(@"ahead (\d+)", RegexOptions.Compiled);
        private static readonly Regex BehindRe = new(@"behind (\d+)", RegexOptions.Compiled);

        /// <summary>Local branches, tags and remote-tracking branches in one pass (BR-001, BR-002,
        /// TAG-001). See MasterSplinter.Logic.h for the 8-field record layout.</summary>
        public RefList ListRefs()
        {
            string raw = NativeLogic.GitRefDetails(RootPath);
            var branches = new List<BranchInfo>();
            var tags = new List<TagInfo>();
            var remotes = new List<RemoteBranchInfo>();

            foreach (string rec in raw.Split(RS))
            {
                // git writes a newline after each record's RS terminator.
                string r = rec.Trim('\n', '\r');
                if (r.Length == 0)
                    continue;
                string[] f = r.Split(US);
                if (f.Length < 8)
                    continue;

                string refName = f[0];
                if (refName.StartsWith("refs/heads/", StringComparison.Ordinal))
                {
                    string track = f[5];
                    branches.Add(new BranchInfo(
                        refName,
                        refName["refs/heads/".Length..],
                        f[1],
                        f[4],
                        MatchInt(AheadRe, track),
                        MatchInt(BehindRe, track),
                        track == "gone",
                        f[6].Trim() == "*"));
                }
                else if (refName.StartsWith("refs/tags/", StringComparison.Ordinal))
                {
                    // %(objectname) is the tag OBJECT for an annotated tag; %(*objectname) peels
                    // it to the commit, which is what compare/checkout need.
                    tags.Add(new TagInfo(
                        refName,
                        refName["refs/tags/".Length..],
                        f[2].Length > 0 ? f[2] : f[1],
                        f[3] == "tag"));
                }
                else if (refName.StartsWith("refs/remotes/", StringComparison.Ordinal))
                {
                    // Skip symbolic refs: refs/remotes/origin/HEAD is an alias for another branch
                    // and would otherwise render as a phantom "HEAD" leaf under the remote.
                    if (f[7].Length > 0)
                        continue;
                    string shortName = refName["refs/remotes/".Length..];
                    int slash = shortName.IndexOf('/');
                    if (slash < 0)
                        continue;
                    remotes.Add(new RemoteBranchInfo(refName, shortName[..slash],
                                                     shortName[(slash + 1)..], f[1]));
                }
            }
            return new RefList(branches, tags, remotes);
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
            '?' => FileChangeStatus.Untracked,
            _ => FileChangeStatus.Modified, // M, T, U, ...
        };

        // ---- Working tree status (STATUS-001/002) ------------------------------------------------

        public sealed record WorkTreeStatus(List<ChangedFile> Staged, List<ChangedFile> Unstaged,
                                            List<ChangedFile> Untracked);

        /// <summary>
        /// Snapshot of the working tree: staged / unstaged / untracked entries. A file that is both
        /// staged and modified again (XY = "MM") appears once in each of the two sections.
        /// </summary>
        public WorkTreeStatus Status()
        {
            var staged = new List<ChangedFile>();
            var unstaged = new List<ChangedFile>();
            var untracked = new List<ChangedFile>();

            // porcelain v1 -z records ("XY <path>"), with NUL separators already translated to RS
            // by the native layer. A rename/copy record is followed by one extra record holding the
            // ORIGINAL path (-z puts the new path first).
            string[] records = NativeLogic.GitStatus(RootPath).Split(RS);
            for (int i = 0; i < records.Length; i++)
            {
                string rec = records[i];
                if (rec.Length < 4 || rec[2] != ' ')
                    continue;

                char x = rec[0], y = rec[1];
                string path = rec[3..];
                string oldPath = "";
                if (x is 'R' or 'C' || y is 'R' or 'C')
                {
                    i++;
                    if (i < records.Length)
                        oldPath = records[i];
                }

                if (x == '?' && y == '?')
                {
                    untracked.Add(new ChangedFile
                    {
                        Path = path,
                        Status = FileChangeStatus.Untracked,
                        Area = WorkTreeArea.Untracked,
                        IsWorkingTree = true,
                    });
                    continue;
                }

                if (x is not ' ' and not '?')
                {
                    staged.Add(new ChangedFile
                    {
                        Path = path,
                        OldPath = x is 'R' or 'C' ? oldPath : "",
                        Status = MapStatus(x),
                        Area = WorkTreeArea.Staged,
                        IsWorkingTree = true,
                    });
                }
                if (y is not ' ' and not '?')
                {
                    unstaged.Add(new ChangedFile
                    {
                        Path = path,
                        OldPath = y is 'R' or 'C' ? oldPath : "",
                        Status = MapStatus(y),
                        Area = WorkTreeArea.Unstaged,
                        IsWorkingTree = true,
                    });
                }
            }
            return new WorkTreeStatus(staged, unstaged, untracked);
        }

        // ---- Staging & commit (Phase 4, COMMIT-001..007) ---------------------------------------
        // Each mutation returns null on success, or the git error text for the InfoBar.

        public string? StagePaths(IEnumerable<string> paths)
            => ParseOkErr(NativeLogic.GitStagePaths(RootPath, JoinPaths(paths)));

        public string? StageAll()
            => ParseOkErr(NativeLogic.GitStageAll(RootPath));

        /// <summary>For a staged rename include BOTH the new and the old path.</summary>
        public string? UnstagePaths(IEnumerable<string> paths)
            => ParseOkErr(NativeLogic.GitUnstagePaths(RootPath, JoinPaths(paths)));

        /// <summary>Restores unstaged changes from the index (tracked files only — deleting an
        /// untracked file is a plain filesystem delete, handled by the caller).</summary>
        public string? DiscardPaths(IEnumerable<string> paths)
            => ParseOkErr(NativeLogic.GitDiscardPaths(RootPath, JoinPaths(paths)));

        public string? Commit(string message, bool amend)
            => ParseOkErr(NativeLogic.GitCommit(RootPath, NormalizeMessage(message), amend));

        /// <summary>Multi-line WinUI TextBoxes report their line breaks as a bare CR, which git
        /// would store verbatim — a two-line message would come back as one line with an embedded
        /// control character. Every message headed for git goes through here.</summary>
        private static string NormalizeMessage(string message)
            => message.Replace("\r\n", "\n").Replace('\r', '\n');

        /// <summary>Subject and body of the HEAD commit (amend pre-fill), or null if there is no
        /// commit yet.</summary>
        public (string Subject, string Body)? HeadMessage()
        {
            string[] parts = NativeLogic.GitHeadMessage(RootPath).Split(US);
            if (parts.Length < 2 || parts[0] != "OK")
                return null;
            return (parts[1], parts.Length >= 3 ? parts[2] : "");
        }

        // ---- Branches & tags (Phase 5, BR-001..007 / TAG-001..003) -----------------------------
        // Each mutation returns null on success, or the git error text for the InfoBar.

        /// <summary>BR-003. Not forced: git carries uncommitted changes across when it safely can
        /// and refuses otherwise, and that refusal is what the caller shows.</summary>
        public string? Checkout(string refName, bool detach)
            => ParseOkErr(NativeLogic.GitCheckout(RootPath, refName, detach));

        /// <summary>BR-004. Empty <paramref name="startPoint"/> means HEAD.</summary>
        public string? CreateBranch(string name, string startPoint, bool checkout)
            => ParseOkErr(NativeLogic.GitCreateBranch(RootPath, name, startPoint, checkout));

        /// <summary>BR-005. Callers must try force=false first, so an unmerged branch can only go
        /// after git has refused once and the user has confirmed again.</summary>
        public string? DeleteBranch(string name, bool force)
            => ParseOkErr(NativeLogic.GitDeleteBranch(RootPath, name, force));

        /// <summary>BR-006. Works on the current branch too.</summary>
        public string? RenameBranch(string oldName, string newName)
            => ParseOkErr(NativeLogic.GitRenameBranch(RootPath, oldName, newName));

        /// <summary>TAG-002. Blank message = lightweight tag; otherwise annotated. Empty
        /// <paramref name="commitish"/> means HEAD.</summary>
        public string? CreateTag(string name, string commitish, string message)
            => ParseOkErr(NativeLogic.GitCreateTag(RootPath, name, commitish, NormalizeMessage(message)));

        /// <summary>TAG-003.</summary>
        public string? DeleteTag(string name)
            => ParseOkErr(NativeLogic.GitDeleteTag(RootPath, name));

        /// <summary>BR-007. Commits in <paramref name="b"/> but not <paramref name="a"/> (Ahead)
        /// and vice versa (Behind). (0, 0) when git failed or the output was unexpected.</summary>
        public (int Ahead, int Behind) AheadBehind(string a, string b)
        {
            // "<onlyInA>\t<onlyInB>" — left is what a has that b lacks, i.e. how far b is behind.
            string[] parts = NativeLogic.GitAheadBehind(RootPath, a, b)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !int.TryParse(parts[0], out int left) || !int.TryParse(parts[1], out int right))
                return (0, 0);
            return (right, left);
        }

        // ---- Remotes (Phase 6, REMOTE-001..009) ------------------------------------------------

        /// <summary>REMOTE-001. Parses `git remote -v`, whose two lines per remote
        /// ("&lt;name&gt;\t&lt;url&gt; (fetch)" then "(push)") are folded into one record.</summary>
        public IReadOnlyList<RemoteInfo> ListRemotes()
        {
            // Insertion-ordered so the dialog lists remotes the way git does (origin first).
            var fetchUrls = new Dictionary<string, string>(StringComparer.Ordinal);
            var pushUrls = new Dictionary<string, string>(StringComparer.Ordinal);
            var order = new List<string>();

            foreach (string line in NativeLogic.GitRemotes(RootPath).Split('\n'))
            {
                string l = line.Trim('\r');
                if (l.Length == 0)
                    continue;

                int tab = l.IndexOf('\t');
                if (tab <= 0)
                    continue;
                string name = l[..tab];
                string rest = l[(tab + 1)..];

                // The trailing "(fetch)"/"(push)" marker says which URL this line carries. A URL
                // can itself contain spaces, so split from the END, not the start.
                int space = rest.LastIndexOf(' ');
                string kind = space >= 0 ? rest[(space + 1)..] : "";
                string url = space >= 0 ? rest[..space] : rest;

                if (!fetchUrls.ContainsKey(name) && !pushUrls.ContainsKey(name))
                    order.Add(name);
                if (kind == "(push)")
                    pushUrls[name] = url;
                else
                    fetchUrls[name] = url;
            }

            return order
                .Select(n => new RemoteInfo(n,
                    fetchUrls.TryGetValue(n, out string? f) ? f : "",
                    pushUrls.TryGetValue(n, out string? p) ? p : ""))
                .ToList();
        }

        /// <summary>REMOTE-008. Callers validate the URL before offering to save.</summary>
        public string? SetRemoteUrl(string name, string url, bool pushUrl)
            => ParseOkErr(NativeLogic.GitSetRemoteUrl(RootPath, name, url.Trim(), pushUrl));

        /// <summary>REMOTE-002. <paramref name="onProgress"/> receives git's output as it arrives
        /// (and an empty string as a heartbeat); returning false cancels the command.</summary>
        public string? Fetch(string remote, bool allRemotes, bool prune, bool tags,
                             Func<string, bool>? onProgress)
            => ParseOkErr(NativeLogic.GitFetch(RootPath, remote, allRemotes, prune, tags, onProgress));

        /// <summary>REMOTE-004, fast-forward only. Empty remote/branch uses the branch's own
        /// upstream.</summary>
        public string? Pull(string remote, string branch, Func<string, bool>? onProgress)
            => ParseOkErr(NativeLogic.GitPull(RootPath, remote, branch, onProgress));

        /// <summary>REMOTE-005; <paramref name="setUpstream"/> is what publishes a new branch with
        /// tracking (REMOTE-006).</summary>
        public string? Push(string remote, string branch, bool setUpstream, bool pushTags,
                            Func<string, bool>? onProgress)
            => ParseOkErr(NativeLogic.GitPush(RootPath, remote, branch, setUpstream, pushTags, onProgress));

        private static string JoinPaths(IEnumerable<string> paths) => string.Join(RS, paths);

        // "OK" -> null; "ERR<US>message" -> message; anything else -> a generic failure.
        private static string? ParseOkErr(string raw)
        {
            string[] parts = raw.Split(US, 2);
            if (parts[0] == "OK")
                return null;
            return parts.Length >= 2 && parts[1].Length > 0
                ? parts[1]
                : "The git operation failed (is git installed and on PATH?).";
        }

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

        /// <summary>Diff for one working-tree file (STATUS-005). The area picks worktree-vs-index,
        /// index-vs-HEAD (staged), or an all-added synthesized diff for untracked files.</summary>
        public (List<DiffLine> Lines, bool IsBinary) WorkTreeDiff(string path, WorkTreeArea area, WhitespaceMode ws)
            => ParseUnifiedDiff(NativeLogic.GitWorkTreeFileDiff(RootPath, path, AreaFlag(area), WsFlag(ws)));

        // The native ABI's area contract (0 = unstaged, 1 = staged, 2 = untracked) is independent
        // of the C# enum's declaration order — map explicitly, like WsFlag.
        private static int AreaFlag(WorkTreeArea a) => a switch
        {
            WorkTreeArea.Staged => 1,
            WorkTreeArea.Untracked => 2,
            _ => 0,
        };

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
