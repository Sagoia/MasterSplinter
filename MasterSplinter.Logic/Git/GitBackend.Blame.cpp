// GitBackend — blame.
//
// One area of GitBackend, split out of the original single 1300-line GitBackend.cpp.
// Same class, same public API — only the file boundary is new. Shared helpers live in
// GitText.h; the git argument builder in GitArgs.h.
//
// KEEP PORTABLE: no <windows.h>. Compiled with PrecompiledHeader=NotUsing.

#include "GitBackend.h"
#include "GitText.h"

#include "../Packed/PackedWriter.h"
#include "../Parse/BlameParser.h"

namespace ms
{
    namespace
    {
        // Blame content lines are raw file bytes. The packed format carries NULs safely now
        // (they are length-prefixed, not NUL-terminated), so this is no longer a transport
        // limit -- it is kept because per-line authorship over binary content is noise, and
        // saying so is more useful than rendering it.
        bool ContainsNul(const std::string& s)
        {
            return s.find('\0') != std::string::npos;
        }
    }

    std::string GitBackend::Blame(const std::string& root, const std::string& rev,
                                  const std::string& path, bool ignoreWhitespace,
                                  const std::string& detectMoves) const
    {
        // Packed exports carry status in the header instead of OK/ERR framing, so every exit
        // below -- success or failure -- is a well-formed buffer the host can read uniformly.
        auto fail = [](std::string message)
        {
            return packed::PackedWriter::Error(packed::Kind::Blame, std::move(message));
        };

        if (root.empty())
            return fail("No repository is open.");
        if (IsBlank(path))
            return fail("No file was provided");
        if (LooksLikeOption(path) || LooksLikeOption(rev))
            return fail("Invalid revision or path");

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
            return fail("Unknown move detection mode: " + detectMoves);

        // --porcelain, not --line-porcelain: the latter repeats every header on every line, which
        // is many times the output for the same information. The per-commit header dedup it implies
        // is unpacked by Parse/BlameParser.
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
            return fail(out.empty() ? std::string("git blame failed") : std::move(out));
        }
        if (ContainsNul(out))
            return fail("This file is binary; blame is not available.");

        return parse::ParsePorcelainBlame(out, path);
    }

}
