using System.Collections.Generic;
using System.Text.RegularExpressions;
using MasterSplinter.Entrypoint.Interop;
using MasterSplinter.Entrypoint.Models;

namespace MasterSplinter.Entrypoint.Git
{
    // The stash. Mirrors GitBackend.Stash.cpp.
    public sealed partial class GitRepository
    {
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

        internal static (string Branch, string Message) SplitStashSubject(string subject)
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
    }
}
