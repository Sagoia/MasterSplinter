using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using MasterSplinter.Entrypoint.Interop;
using MasterSplinter.Entrypoint.Models;

namespace MasterSplinter.Entrypoint.Git
{
    // Commit history, the ref list that feeds the sidebar, search and reflog.
    // Mirrors GitBackend.History.cpp on the native side.
    public sealed partial class GitRepository
    {
        // ---- Commit history --------------------------------------------------------------------

        public IReadOnlyList<CommitRow> Log(int order, int maxCount)
            => WithGraph(ReadLog(NativeLogic.GitLog(RootPath, order, maxCount)));

        /// <summary>Lays the branch graph out over a freshly parsed list. Shared by Log and
        /// SearchLog, which produce byte-identical records and so want identical treatment.</summary>
        private static List<CommitRow> WithGraph(List<CommitRow> commits)
        {
            CommitGraph.Assign(commits, new CommitIndex(commits));
            return commits;
        }

        // ---- Reading a packed log ----------------------------------------------------------
        //
        // Record parsing lives in the native core now (Parse/LogParser.{h,cpp}); the layout below
        // mirrors LogParser.h. The two tables ARE the contract, which is why both sides spell the
        // offsets out rather than sharing a generated struct.

        private const int LogOffAuthorTime = 0;
        private const int LogOffCommitTime = 8;
        private const int LogOffAuthorTz = 16;
        private const int LogOffCommitTz = 20;
        private const int LogOffFullHash = 24;
        private const int LogOffShortHash = 32;
        private const int LogOffParents = 40;
        private const int LogOffAuthorName = 48;
        private const int LogOffAuthorEmail = 56;
        private const int LogOffCommitterName = 64;
        private const int LogOffCommitterEmail = 72;
        private const int LogOffBadges = 80;
        private const int LogOffSubject = 88;
        private const int LogOffBody = 96;

        /// <summary>
        /// Materialises packed commit records into rows.
        /// <para>
        /// The graph is NOT assigned here. Lane layout is cross-row by nature — where a commit
        /// sits depends on its children — so it belongs to the whole list, not to one record.
        /// See <see cref="CommitGraph"/>.
        /// </para>
        /// </summary>
        internal static List<CommitRow> ReadLog(PackedBuffer buf)
        {
            int count = buf.RecordCount;
            var commits = new List<CommitRow>(count);
            for (int i = 0; i < count; i++)
            {
                string[] parents = buf.ArrayItems(i, LogOffParents);
                var row = new CommitRow
                {
                    FullHash = buf.Str(i, LogOffFullHash),
                    Hash = buf.Str(i, LogOffShortHash),
                    ParentHashes = parents,
                    Parents = parents.Length == 0 ? "—" : string.Join(", ", parents.Select(Short)),
                    Author = buf.Str(i, LogOffAuthorName),
                    AuthorEmail = buf.Str(i, LogOffAuthorEmail),
                    AuthorDate = FromUnixWithOffset(buf.I64(i, LogOffAuthorTime),
                                                    buf.I32(i, LogOffAuthorTz)),
                    Committer = buf.Str(i, LogOffCommitterName),
                    CommitterEmail = buf.Str(i, LogOffCommitterEmail),
                    CommitDate = FromUnixWithOffset(buf.I64(i, LogOffCommitTime),
                                                    buf.I32(i, LogOffCommitTz)),
                    Message = buf.Str(i, LogOffSubject),
                    Body = buf.Str(i, LogOffBody),
                };
                row.Date = FormatDate(row.AuthorDate);
                row.CommitterDate = FormatDate(row.CommitDate);

                int badges = buf.ArrayCount(i, LogOffBadges);
                for (int b = 0; b < badges; b++)
                {
                    (uint kind, string text) = buf.TaggedItem(i, LogOffBadges, b);
                    row.Badges.Add(new Badge { Kind = (BadgeKind)kind, Text = text });
                }

                commits.Add(row);
            }
            return commits;
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
            => WithGraph(ReadLog(NativeLogic.GitSearchLog(RootPath, SearchModeArg(mode), query,
                                                            pathFilter, order, maxCount, matchCase,
                                                            useRegex, allBranches)));

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
    }
}
