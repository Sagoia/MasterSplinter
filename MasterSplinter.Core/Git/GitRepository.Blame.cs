using System;
using System.Collections.Generic;
using MasterSplinter.Entrypoint.Interop;
using MasterSplinter.Entrypoint.Models;

namespace MasterSplinter.Entrypoint.Git
{
    // Blame. Mirrors GitBackend.Blame.cpp; the porcelain parser itself now lives natively in
    // Parse/BlameParser.{h,cpp}, so what remains here is unpacking.
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

        // Record layout, mirroring Parse/BlameParser.h. The two tables ARE the contract, which is
        // why both sides spell the offsets out rather than sharing a generated struct.
        private const int BlameOffOrigLine = 0;
        private const int BlameOffFinalLine = 4;
        private const int BlameOffAuthorTime = 8;
        private const int BlameOffAuthorTz = 16;
        private const int BlameOffIsGroupStart = 20;
        private const int BlameOffSha = 24;
        private const int BlameOffAuthor = 32;
        private const int BlameOffEmail = 40;
        private const int BlameOffSummary = 48;
        private const int BlameOffSourcePath = 56;
        private const int BlameOffText = 64;

        /// <summary>
        /// Per-line authorship for one file (BLAME-001). <paramref name="error"/> is null on
        /// success and carries git's own message otherwise ("no such path in HEAD", "binary file").
        /// </summary>
        public IReadOnlyList<BlameLine> Blame(string rev, string path, bool ignoreWhitespace,
                                              BlameMoveDetection moves, out string? error)
        {
            PackedBuffer buf = NativeLogic.GitBlame(RootPath, rev, path, ignoreWhitespace,
                                                    MoveDetectionArg(moves));
            if (buf.IsError)
            {
                // The message travels inside the buffer header now rather than as OK/ERR framing,
                // so there is no separator to split on and no way for file content to be mistaken
                // for a status word.
                error = buf.ErrorMessage.Length > 0 ? buf.ErrorMessage : "Could not blame this file.";
                return Array.Empty<BlameLine>();
            }

            error = null;
            return ReadBlame(buf);
        }

        /// <summary>
        /// Materialises a packed blame into display lines.
        /// <para>
        /// The timestamp arrives as unix seconds plus a signed minute offset rather than as text:
        /// building a <see cref="DateTimeOffset"/> — and clamping a corrupt one — is a .NET
        /// concern, so it stays here.
        /// </para>
        /// </summary>
        internal static List<BlameLine> ReadBlame(PackedBuffer buf)
        {
            int count = buf.RecordCount;
            var lines = new List<BlameLine>(count);
            for (int i = 0; i < count; i++)
            {
                string sha = buf.Str(i, BlameOffSha);
                lines.Add(new BlameLine(
                    sha,
                    Short(sha),
                    buf.Str(i, BlameOffAuthor),
                    buf.Str(i, BlameOffEmail),
                    FromUnixWithOffset(buf.I64(i, BlameOffAuthorTime), buf.I32(i, BlameOffAuthorTz)),
                    buf.Str(i, BlameOffSummary),
                    buf.Str(i, BlameOffSourcePath),
                    buf.I32(i, BlameOffOrigLine),
                    buf.I32(i, BlameOffFinalLine),
                    buf.Str(i, BlameOffText),
                    buf.U8(i, BlameOffIsGroupStart) != 0));
            }
            return lines;
        }

    }
}
