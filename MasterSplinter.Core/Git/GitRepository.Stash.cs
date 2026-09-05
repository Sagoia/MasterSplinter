using System.Collections.Generic;
using MasterSplinter.Entrypoint.Interop;
using MasterSplinter.Entrypoint.Models;

namespace MasterSplinter.Entrypoint.Git
{
    // The stash. Mirrors GitBackend.Stash.cpp.
    public sealed partial class GitRepository
    {
        // ---- Stash (Phase 8, STASH-001..004) ---------------------------------------------------

        // Record layout, mirroring Parse/RefParser.h.
        private const int StashOffWhen = 0;
        private const int StashOffTz = 8;
        private const int StashOffIndex = 12;
        private const int StashOffSelector = 16;
        private const int StashOffSha = 24;
        private const int StashOffShortSha = 32;
        private const int StashOffMessage = 40;
        private const int StashOffBranch = 48;
        private const int StashOffAuthor = 56;

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
            // The subject arrives already split into branch + message by Parse/RefParser.cpp.
            PackedBuffer buf = NativeLogic.GitStashList(RootPath);
            var entries = new List<StashEntry>(buf.RecordCount);
            for (int i = 0; i < buf.RecordCount; i++)
            {
                entries.Add(new StashEntry(
                    buf.I32(i, StashOffIndex),
                    buf.Str(i, StashOffSelector),
                    buf.Str(i, StashOffSha),
                    buf.Str(i, StashOffShortSha),
                    buf.Str(i, StashOffMessage),
                    buf.Str(i, StashOffBranch),
                    FromUnixWithOffset(buf.I64(i, StashOffWhen), buf.I32(i, StashOffTz)),
                    buf.Str(i, StashOffAuthor)));
            }
            return entries;
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
    }
}
