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
            => ReadFiles(NativeLogic.GitCommitFiles(RootPath, sha));

        /// <summary>Files changed between two commits/refs (DIFF-006 / DIFF-007).</summary>
        public IReadOnlyList<ChangedFile> ChangedFilesRange(string a, string b)
            => ReadFiles(NativeLogic.GitRangeFiles(RootPath, a, b));

        /// <summary>
        /// Materialises a packed changed-file list. Shared by the commit, range and working-tree
        /// reads: one record layout serves all three (see Parse/StatusParser.h).
        /// </summary>
        internal static List<ChangedFile> ReadFiles(PackedBuffer buf)
        {
            int count = buf.RecordCount;
            var files = new List<ChangedFile>(count);
            for (int i = 0; i < count; i++)
            {
                files.Add(new ChangedFile
                {
                    Path = buf.Str(i, FileOffPath),
                    OldPath = buf.Str(i, FileOffOldPath),
                    Status = (FileChangeStatus)buf.U8(i, FileOffStatus),
                    Area = (WorkTreeArea)buf.U8(i, FileOffSection),
                    IsWorkingTree = buf.U8(i, FileOffIsWorkingTree) != 0,
                });
            }
            return files;
        }

        // Record layout, mirroring Parse/StatusParser.h.
        private const int FileOffStatus = 0;
        private const int FileOffSection = 1;
        private const int FileOffIsWorkingTree = 2;
        private const int FileOffPath = 4;
        private const int FileOffOldPath = 12;

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

        public (List<DiffLine> Lines, bool IsBinary) FileDiff(string sha, string path, WhitespaceMode ws)
            => ReadDiff(NativeLogic.GitFileDiff(RootPath, sha, path, WsFlag(ws)));

        public (List<DiffLine> Lines, bool IsBinary) RangeDiff(string a, string b, string path, WhitespaceMode ws)
            => ReadDiff(NativeLogic.GitRangeFileDiff(RootPath, a, b, path, WsFlag(ws)));

        /// <summary>Diff for one working-tree file (STATUS-005). The area picks worktree-vs-index,
        /// index-vs-HEAD (staged), or an all-added synthesized diff for untracked files.</summary>
        public (List<DiffLine> Lines, bool IsBinary) WorkTreeDiff(string path, WorkTreeArea area, WhitespaceMode ws)
            => ReadDiff(NativeLogic.GitWorkTreeFileDiff(RootPath, path, AreaFlag(area), WsFlag(ws)));

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

        // ---- Reading a packed diff -------------------------------------------------------------
        //
        // Parsing itself lives in the native core now (Parse/DiffParser.{h,cpp}), so what is left
        // here is unpacking. The layout below mirrors DiffParser.h; the two tables ARE the
        // contract, which is why both sides spell the offsets out rather than sharing a struct.

        private const int DiffOffKind = 0;
        private const int DiffOffOldNo = 4;
        private const int DiffOffNewNo = 8;
        private const int DiffOffText = 12;
        private const ushort DiffFlagBinary = 0x0001;

        /// <summary>
        /// Materialises a packed diff into display lines.
        /// <para>
        /// Line numbers arrive as integers with -1 meaning "this side has no number", so the
        /// gutters cost no string allocation until this point. A malformed buffer reads as zero
        /// records, which renders an empty diff pane -- the same thing unparseable patch text
        /// always did.
        /// </para>
        /// </summary>
        internal static (List<DiffLine> Lines, bool IsBinary) ReadDiff(PackedBuffer buf)
        {
            int count = buf.RecordCount;
            var lines = new List<DiffLine>(count);
            for (int i = 0; i < count; i++)
            {
                int oldNo = buf.I32(i, DiffOffOldNo);
                int newNo = buf.I32(i, DiffOffNewNo);
                lines.Add(new DiffLine
                {
                    Kind = (DiffLineKind)buf.U8(i, DiffOffKind),
                    OldNo = oldNo < 0 ? "" : oldNo.ToString(CultureInfo.InvariantCulture),
                    NewNo = newNo < 0 ? "" : newNo.ToString(CultureInfo.InvariantCulture),
                    Text = buf.Str(i, DiffOffText),
                });
            }
            return (lines, (buf.Flags & DiffFlagBinary) != 0);
        }

        // ---- File content at a commit ----------------------------------------------------------

        public string FileAt(string sha, string path) => NativeLogic.GitFileAtCommit(RootPath, sha, path);

        /// <summary>Raw bytes of a file at a commit/ref (binary-safe), for image previews (DIFF-005).</summary>
        public byte[] FileBytesAt(string sha, string path) => NativeLogic.GitFileBytesAtCommit(RootPath, sha, path);
    }
}
