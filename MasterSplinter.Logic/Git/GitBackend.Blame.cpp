// GitBackend — blame.
//
// One area of GitBackend, split out of the original single 1300-line GitBackend.cpp.
// Same class, same public API — only the file boundary is new. Shared helpers live in
// GitText.h; the git argument builder in GitArgs.h.
//
// KEEP PORTABLE: no <windows.h>. Compiled with PrecompiledHeader=NotUsing.

#include "GitBackend.h"
#include "GitText.h"

namespace ms
{
    namespace
    {
        // Blame content lines are raw file bytes. A NUL would truncate the whole payload at the
        // managed marshaller, so a binary file has to be refused rather than silently half-shown.
        bool ContainsNul(const std::string& s)
        {
            return s.find('\0') != std::string::npos;
        }
    }

    std::string GitBackend::Blame(const std::string& root, const std::string& rev,
                                  const std::string& path, bool ignoreWhitespace,
                                  const std::string& detectMoves) const
    {
        if (root.empty())
            return NoRoot();
        if (IsBlank(path))
            return Err("No file was provided");
        if (LooksLikeOption(path) || LooksLikeOption(rev))
            return Err("Invalid revision or path");

        // Named modes rather than an int ladder, for the reason MsGitSequencerAction spells out.
        // The flags mirror TortoiseGit's detect-moved-or-copied setting.
        std::vector<std::string> moveFlags;
        if (detectMoves.empty() || detectMoves == "none")
            ; // no flags
        else if (detectMoves == "file")
            moveFlags = { "-M" };                 // moved within the same file
        else if (detectMoves == "commit")
            moveFlags = { "-C" };                 // copied from files modified in the same commit
        else if (detectMoves == "any")
            moveFlags = { "-C", "-C" };           // ...and from any file in the commit that created it
        else
            return Err("Unknown move detection mode: " + detectMoves);

        // --porcelain, not --line-porcelain: the latter repeats every header on every line, which
        // is many times the output for the same information. The per-commit header dedup it implies
        // is unpacked by the host's parser.
        GitArgs args;
        args.QuotePathOff()
            .Add({ "blame", "--porcelain" })
            .AddIf(ignoreWhitespace, "-w")
            .AddAll(moveFlags)
            .Add(rev.empty() ? "HEAD" : rev)
            .Path(path);

        int code;
        std::string out = RunGitC(root, args.Take(), code);
        if (code != 0)
        {
            TrimTrailingNewlines(out);
            return Err(out.empty() ? std::string("git blame failed") : std::move(out));
        }
        if (ContainsNul(out))
            return Err("This file is binary; blame is not available.");

        // OK/ERR-framed even though this is a read: "path not in that revision" is a routine,
        // actionable failure and the message is worth keeping. The host splits on the FIRST US
        // only, so 0x1F bytes inside file content stay intact.
        return std::string("OK") + US + out;
    }

}
