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

        /// <summary>
        /// The commit-graph display list for the most recent <see cref="Log"/> call, as the
        /// renderer consumes it. Empty until the first load.
        /// <para>
        /// Kept on the repository rather than threaded through the view model because the two have
        /// the same lifetime: a refresh replaces this instance wholesale, so the graph and the rows
        /// it describes cannot fall out of step.
        /// </para>
        /// </summary>
        public byte[] GraphDisplayList { get; private set; } = Array.Empty<byte>();

        public IReadOnlyList<CommitRow> Log(int order, int maxCount)
        {
            // The graph rides in the same buffer as the records, laid out natively while their
            // parents were still resolvable to row positions.
            PackedBuffer buf = NativeLogic.GitLogGraph(RootPath, order, maxCount);
            List<CommitRow> commits = ReadLog(buf);
            GraphDisplayList = buf.Extra.ToArray();
            CommitGraph.Assign(commits, buf.Extra);
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

        // Record layout, mirroring Parse/RefParser.h.
        private const int RefOffKind = 0;
        private const int RefOffIsCurrent = 1;
        private const int RefOffUpstreamGone = 2;
        private const int RefOffIsAnnotated = 3;
        private const int RefOffAhead = 4;
        private const int RefOffBehind = 8;
        private const int RefOffRefName = 12;
        private const int RefOffName = 20;
        private const int RefOffSha = 28;
        private const int RefOffUpstream = 36;
        private const int RefOffRemote = 44;

        private const byte RefKindBranch = 0;
        private const byte RefKindTag = 1;
        private const byte RefKindRemoteBranch = 2;

        /// <summary>Local branches, tags and remote-tracking branches in one pass (BR-001, BR-002,
        /// TAG-001). Parsing lives in Parse/RefParser.cpp; each record is tagged with its kind, so
        /// this is bucketing rather than prefix-testing.</summary>
        public RefList ListRefs()
        {
            PackedBuffer buf = NativeLogic.GitRefDetails(RootPath);
            var branches = new List<BranchInfo>();
            var tags = new List<TagInfo>();
            var remotes = new List<RemoteBranchInfo>();

            for (int i = 0; i < buf.RecordCount; i++)
            {
                switch (buf.U8(i, RefOffKind))
                {
                    case RefKindBranch:
                        branches.Add(new BranchInfo(
                            buf.Str(i, RefOffRefName),
                            buf.Str(i, RefOffName),
                            buf.Str(i, RefOffSha),
                            buf.Str(i, RefOffUpstream),
                            buf.I32(i, RefOffAhead),
                            buf.I32(i, RefOffBehind),
                            buf.U8(i, RefOffUpstreamGone) != 0,
                            buf.U8(i, RefOffIsCurrent) != 0));
                        break;

                    case RefKindTag:
                        tags.Add(new TagInfo(
                            buf.Str(i, RefOffRefName),
                            buf.Str(i, RefOffName),
                            buf.Str(i, RefOffSha),
                            buf.U8(i, RefOffIsAnnotated) != 0));
                        break;

                    case RefKindRemoteBranch:
                        remotes.Add(new RemoteBranchInfo(
                            buf.Str(i, RefOffRefName),
                            buf.Str(i, RefOffRemote),
                            buf.Str(i, RefOffName),
                            buf.Str(i, RefOffSha)));
                        break;
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
        /// <remarks>
        /// Deliberately no graph column. Search results are a filtered subset, so almost every
        /// parent lies outside them; lanes drawn between them would describe a history that is not
        /// the one being shown. The placeholder used to draw one blue lane per row here, which was
        /// equally meaningless and looked authoritative.
        /// </remarks>
        public IReadOnlyList<CommitRow> SearchLog(SearchMode mode, string query, string pathFilter,
                                                  int order, int maxCount, bool matchCase,
                                                  bool useRegex, bool allBranches)
            => ReadLog(NativeLogic.GitSearchLog(RootPath, SearchModeArg(mode), query, pathFilter,
                                               order, maxCount, matchCase, useRegex, allBranches));

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
        // Record layout, mirroring Parse/RefParser.h.
        private const int ReflogOffWhen = 0;
        private const int ReflogOffTz = 8;
        private const int ReflogOffIndex = 12;
        private const int ReflogOffSelector = 16;
        private const int ReflogOffSha = 24;
        private const int ReflogOffShortSha = 32;
        private const int ReflogOffAction = 40;
        private const int ReflogOffDetail = 48;
        private const int ReflogOffSubject = 56;
        private const int ReflogOffAuthor = 64;

        public IReadOnlyList<ReflogEntry> Reflog(string refName, int maxCount)
        {
            // The reflog subject arrives already split into action + detail.
            PackedBuffer buf = NativeLogic.GitReflog(RootPath, refName, maxCount);
            var entries = new List<ReflogEntry>(buf.RecordCount);
            for (int i = 0; i < buf.RecordCount; i++)
            {
                entries.Add(new ReflogEntry(
                    buf.I32(i, ReflogOffIndex),
                    buf.Str(i, ReflogOffSelector),
                    buf.Str(i, ReflogOffSha),
                    buf.Str(i, ReflogOffShortSha),
                    buf.Str(i, ReflogOffAction),
                    buf.Str(i, ReflogOffDetail),
                    buf.Str(i, ReflogOffSubject),
                    FromUnixWithOffset(buf.I64(i, ReflogOffWhen), buf.I32(i, ReflogOffTz)),
                    buf.Str(i, ReflogOffAuthor)));
            }
            return entries;
        }
    }
}
