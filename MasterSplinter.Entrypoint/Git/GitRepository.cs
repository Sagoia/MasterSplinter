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
            => ParseCommitRecords(NativeLogic.GitLog(RootPath, order, maxCount));

        /// <summary>The 12-field commit record layout, shared by <see cref="Log"/> and
        /// <see cref="SearchLog"/> — the native side emits one format string for both, so this is
        /// the one place that knows the field order.</summary>
        private static List<CommitRow> ParseCommitRecords(string raw)
        {
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
        private static bool IsUnmerged(char x, char y)
            => (x, y) is ('D', 'D') or ('A', 'U') or ('U', 'D') or ('U', 'A')
                      or ('D', 'U') or ('A', 'A') or ('U', 'U');

        // ---- Working tree status (STATUS-001/002) ------------------------------------------------

        public sealed record WorkTreeStatus(List<ChangedFile> Staged, List<ChangedFile> Unstaged,
                                            List<ChangedFile> Untracked, List<ChangedFile> Conflicted);

        /// <summary>
        /// Snapshot of the working tree: conflicted / staged / unstaged / untracked entries. A file
        /// that is both staged and modified again (XY = "MM") appears once in each of the staged and
        /// unstaged sections; a conflicted file appears once, in its own section (MERGE-003).
        /// </summary>
        public WorkTreeStatus Status()
        {
            var staged = new List<ChangedFile>();
            var unstaged = new List<ChangedFile>();
            var untracked = new List<ChangedFile>();
            var conflicted = new List<ChangedFile>();

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

                if (IsUnmerged(x, y))
                {
                    conflicted.Add(new ChangedFile
                    {
                        Path = path,
                        Status = FileChangeStatus.Conflicted,
                        Area = WorkTreeArea.Conflicted,
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
            return new WorkTreeStatus(staged, unstaged, untracked, conflicted);
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

        // ---- Merge / rebase / cherry-pick / revert (Phase 7) -----------------------------------
        // Each returns null on success, or git's text. A CONFLICT arrives here as an error string
        // even though it is a normal outcome — callers must refresh regardless and re-read
        // <see cref="State"/>, because the repository really did change.

        /// <summary>MERGE-001.</summary>
        public string? Merge(string refName, bool noFastForward, bool noCommit,
                             Func<string, bool>? onProgress)
            => ParseOkErr(NativeLogic.GitMerge(RootPath, refName, noFastForward, noCommit, onProgress));

        /// <summary>REBASE-001. Non-interactive, never auto-stashed.</summary>
        public string? Rebase(string upstream, Func<string, bool>? onProgress)
            => ParseOkErr(NativeLogic.GitRebase(RootPath, upstream, onProgress));

        /// <summary>CHERRY-001/002. <paramref name="shas"/> must already be oldest-first — git
        /// applies them left to right.</summary>
        public string? CherryPick(IEnumerable<string> shas, bool noCommit, Func<string, bool>? onProgress)
            => ParseOkErr(NativeLogic.GitCherryPick(RootPath, JoinPaths(shas), noCommit, onProgress));

        /// <summary>REVERT-001. <paramref name="mainline"/> is 1-based and required for a merge
        /// commit; 0 omits it.</summary>
        public string? Revert(string sha, int mainline, bool noCommit, Func<string, bool>? onProgress)
            => ParseOkErr(NativeLogic.GitRevert(RootPath, sha, mainline, noCommit, onProgress));

        /// <summary>MERGE-002 / REBASE-002. <paramref name="operation"/> and
        /// <paramref name="action"/> are validated by the native allowlist.</summary>
        public string? SequencerAction(string operation, string action, Func<string, bool>? onProgress)
            => ParseOkErr(NativeLogic.GitSequencerAction(RootPath, operation, action, onProgress));

        /// <summary>MERGE-004. Blank <paramref name="tool"/> defers to the repository's own
        /// merge.tool configuration.</summary>
        public string? MergeTool(string path, string tool, Func<string, bool>? onProgress)
            => ParseOkErr(NativeLogic.GitMergeTool(RootPath, path, tool, onProgress));

        /// <summary>What git is in the middle of, if anything (MERGE-003, REBASE-002). Returns
        /// <see cref="RepositoryState.None"/> when nothing is in progress or the state could not
        /// be read — a banner that fails to appear is better than one that lies.</summary>
        public RepositoryState State()
        {
            string[] f = NativeLogic.GitRepositoryState(RootPath).Split(US);
            if (f.Length < 6 || f[0] != "OK")
                return RepositoryState.None;

            RepoOperation op = f[1] switch
            {
                "merging" => RepoOperation.Merging,
                "rebasing" => RepoOperation.Rebasing,
                "cherry-picking" => RepoOperation.CherryPicking,
                "reverting" => RepoOperation.Reverting,
                _ => RepoOperation.None,
            };
            if (op == RepoOperation.None)
                return RepositoryState.None;

            int.TryParse(f[3], out int step);
            int.TryParse(f[4], out int total);
            return new RepositoryState(op, f[2], step, total, f[5]);
        }

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

        // ---- Stash (Phase 8, STASH-001..004) ---------------------------------------------------

        /// <summary>
        /// The stash, newest first (STASH-001). Empty when there are no stashes AND on error —
        /// a repository that has never stashed is the ordinary case, not a failure worth a banner.
        /// </summary>
        /// <remarks>
        /// The selectors are POSITIONAL: dropping or popping renumbers everything below, so callers
        /// must re-read this list after any stash mutation rather than reusing a captured entry.
        /// </remarks>
        public IReadOnlyList<StashEntry> ListStashes()
        {
            string raw = NativeLogic.GitStashList(RootPath);
            var entries = new List<StashEntry>();
            int index = 0;
            foreach (string rec in raw.Split(RS))
            {
                string r = rec.Trim('\n', '\r');
                if (r.Length == 0)
                    continue;
                string[] f = r.Split(US);
                if (f.Length < 6)
                    continue;

                var (branch, message) = SplitStashSubject(f[3]);
                entries.Add(new StashEntry(index++, f[0], f[1], f[2], message, branch,
                                           ParseDate(f[4]), f[5]));
            }
            return entries;
        }

        // git composes a stash's reflog subject as "WIP on <branch>: <sha> <subject>" (or
        // "On <branch>: <text>" when the user supplied a message). Splitting it gives the sidebar a
        // branch to show and a message that is not three-quarters boilerplate. Anything that does
        // not match either shape is kept whole — a custom message is not worth mangling.
        private static readonly Regex StashSubjectRe =
            new(@"^(?:WIP on|On) ([^:]+): (.*)$", RegexOptions.Compiled | RegexOptions.Singleline);

        private static (string Branch, string Message) SplitStashSubject(string subject)
        {
            Match m = StashSubjectRe.Match(subject);
            return m.Success ? (m.Groups[1].Value, m.Groups[2].Value) : ("", subject);
        }

        /// <summary>STASH-001. A blank message lets git compose its own "WIP on &lt;branch&gt;" text.
        /// Returns an error when the tree was clean — git stashes nothing and still exits 0.</summary>
        public string? StashSave(string message, bool includeUntracked, bool keepIndex)
            => ParseOkErr(NativeLogic.GitStashSave(RootPath, NormalizeMessage(message),
                                                   includeUntracked, keepIndex));

        /// <summary>STASH-002. A conflict comes back as git's own text, with the markers written.</summary>
        public string? StashApply(string selector)
            => ParseOkErr(NativeLogic.GitStashApply(RootPath, selector));

        /// <summary>STASH-003. Apply + drop; on conflict git keeps the entry, so nothing is lost.</summary>
        public string? StashPop(string selector)
            => ParseOkErr(NativeLogic.GitStashPop(RootPath, selector));

        /// <summary>STASH-004. Irreversible from the UI — confirm before calling.</summary>
        public string? StashDrop(string selector)
            => ParseOkErr(NativeLogic.GitStashDrop(RootPath, selector));

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
        private static List<BlameLine> ParsePorcelainBlame(string porcelain, string blamedPath)
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

        // ---- Search (Phase 8, SEARCH-001/002) --------------------------------------------------

        private static string SearchModeArg(SearchMode mode) => mode switch
        {
            SearchMode.Author => "author",
            SearchMode.Content => "content",
            SearchMode.Path => "path",
            SearchMode.Hash => "hash",
            _ => "message",
        };

        /// <summary>
        /// Commit search over the FULL history, unlike the in-memory filter over the loaded log
        /// (SEARCH-001, SEARCH-002). Returns rows in the same shape as <see cref="Log"/>, so the
        /// commit list, detail pane and diff viewer all work on the results unchanged.
        /// An empty list means "no matches" — including for a query git rejected.
        /// </summary>
        public IReadOnlyList<CommitRow> SearchLog(SearchMode mode, string query, string pathFilter,
                                                  int order, int maxCount, bool matchCase,
                                                  bool useRegex, bool allBranches)
            => ParseCommitRecords(NativeLogic.GitSearchLog(RootPath, SearchModeArg(mode), query,
                                                            pathFilter, order, maxCount, matchCase,
                                                            useRegex, allBranches));

        /// <summary>
        /// One commit by sha (full or abbreviated), or null if it does not resolve. Used by the
        /// reflog, whose entries routinely name commits no branch reaches any more and which are
        /// therefore absent from the loaded log.
        /// </summary>
        public CommitRow? CommitByHash(string sha)
            => SearchLog(SearchMode.Hash, sha, "", 0, 1, true, false, false).FirstOrDefault();

        // ---- Reflog (Phase 8, REFLOG-001) ------------------------------------------------------

        /// <summary>
        /// Where a ref has been (REFLOG-001); empty <paramref name="refName"/> means HEAD. Empty
        /// when the ref has no reflog at all, which is an ordinary state rather than an error.
        /// </summary>
        public IReadOnlyList<ReflogEntry> Reflog(string refName, int maxCount)
        {
            string raw = NativeLogic.GitReflog(RootPath, refName, maxCount);
            var entries = new List<ReflogEntry>();
            int index = 0;
            foreach (string rec in raw.Split(RS))
            {
                string r = rec.Trim('\n', '\r');
                if (r.Length == 0)
                    continue;
                string[] f = r.Split(US);
                if (f.Length < 7)
                    continue;

                // git packs the operation and its argument into one subject:
                // "checkout: moving from main to feature" -> ("checkout", "moving from ...").
                string subject = f[3];
                int colon = subject.IndexOf(": ", StringComparison.Ordinal);
                string action = colon > 0 ? subject[..colon] : subject;
                string detail = colon > 0 ? subject[(colon + 2)..] : "";

                entries.Add(new ReflogEntry(index++, f[0], f[1], f[2], action, detail, f[6],
                                            ParseDate(f[4]), f[5]));
            }
            return entries;
        }

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
