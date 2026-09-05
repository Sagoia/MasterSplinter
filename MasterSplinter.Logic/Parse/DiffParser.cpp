// KEEP PORTABLE: no <windows.h>. Compiled with PrecompiledHeader=NotUsing.

#include "DiffParser.h"

#include "../Packed/PackedWriter.h"

#include <algorithm>

namespace ms::parse
{
    namespace
    {
        using packed::PackedWriter;
        using packed::StringRef;

        constexpr std::int32_t kNoNumber = -1;

        bool IsDigit(char c) { return c >= '0' && c <= '9'; }

        // Reads a run of digits starting at `i`, advancing it. Returns false when there is none,
        // which is how a malformed hunk header leaves the previous counters untouched.
        bool ReadNumber(std::string_view s, std::size_t& i, std::int32_t& out)
        {
            if (i >= s.size() || !IsDigit(s[i]))
                return false;

            long long value = 0;
            while (i < s.size() && IsDigit(s[i]))
            {
                // Saturate rather than overflow. A line number this large cannot come from a real
                // repository, and wrapping would put a negative number in the gutter.
                if (value < 1000000000LL)
                    value = value * 10 + (s[i] - '0');
                ++i;
            }
            out = static_cast<std::int32_t>(value);
            return true;
        }

        // A hunk header is "@@ -a,b +c,d @@", or the combined "@@@ -a,b -e,f +c,d @@@" that git
        // emits for a conflicted path. Both forms yield the same two numbers: the FIRST range
        // after a '-' is the old-side start, and the range after '+' is the result side.
        //
        // Hand-parsed rather than run through <regex>: the grammar is this small, and the two
        // regexes it replaces were the only reason the host pulled in RegexOptions.Compiled.
        struct HunkHeader
        {
            int markerColumns = 0;   // >0 for a combined diff: one marker per parent
            std::int32_t oldNo = 0;
            std::int32_t newNo = 0;
            bool parsed = false;     // false leaves the running counters alone
        };

        HunkHeader ParseHunkHeader(std::string_view l)
        {
            HunkHeader h;

            std::size_t ats = 0;
            while (ats < l.size() && l[ats] == '@')
                ++ats;

            bool haveOld = false, haveNew = false;
            for (std::size_t i = ats; i < l.size(); ++i)
            {
                if (!haveOld && l[i] == '-')
                {
                    std::size_t at = i + 1;
                    if (ReadNumber(l, at, h.oldNo))
                        haveOld = true;
                }
                else if (l[i] == '+')
                {
                    std::size_t at = i + 1;
                    if (ReadNumber(l, at, h.newNo))
                    {
                        haveNew = true;
                        break;  // the result-side range is the last one; nothing after it matters
                    }
                }
            }

            h.parsed = haveOld && haveNew;
            // "@@@" -> 2 parents -> 2 marker columns, "@@@@" -> 3, and so on. Only treat it as
            // combined once the ranges actually parsed, so a malformed line cannot silently eat
            // the first characters of every body line as marker columns.
            if (h.parsed && ats >= 3)
                h.markerColumns = static_cast<int>(ats) - 1;
            return h;
        }
    }

    std::string ParseUnifiedDiff(std::string_view raw)
    {
        PackedWriter w(packed::Kind::Diff, kDiffRecordSize);

        // A binary file's patch has no hunks, just a marker line.
        const bool isBinary = raw.find("Binary files ") != std::string_view::npos ||
                              raw.find("GIT binary patch") != std::string_view::npos;
        if (isBinary)
            w.SetFlags(kDiffFlagBinary);

        auto emit = [&](DiffLineKind kind, std::int32_t oldNo, std::int32_t newNo,
                        std::string_view text)
        {
            const StringRef ref = w.AddString(text);
            w.BeginRecord();
            w.PutU8(static_cast<std::uint8_t>(kind));
            w.PutU8(0);
            w.PutU16(0);
            w.PutI32(oldNo);
            w.PutI32(newNo);
            w.PutRef(ref);
            w.EndRecord();
        };

        std::int32_t oldNo = 0, newNo = 0;
        bool inHunk = false;
        int markerColumns = 0;

        std::size_t pos = 0;
        while (pos <= raw.size())
        {
            const std::size_t nl = raw.find('\n', pos);
            std::string_view l = raw.substr(pos, nl == std::string_view::npos ? std::string_view::npos
                                                                              : nl - pos);
            pos = (nl == std::string_view::npos) ? raw.size() + 1 : nl + 1;

            while (!l.empty() && l.back() == '\r')
                l.remove_suffix(1);

            if (l.size() >= 2 && l[0] == '@' && l[1] == '@')
            {
                const HunkHeader h = ParseHunkHeader(l);
                markerColumns = h.markerColumns;
                if (h.parsed)
                {
                    oldNo = h.oldNo;
                    newNo = h.newNo;
                }
                inHunk = true;
                emit(DiffLineKind::Hunk, kNoNumber, kNoNumber, l);
                continue;
            }

            if (!inHunk)
                continue;  // the "diff --git / index / --- / +++" file header block

            if (!l.empty() && l[0] == '\\')
                continue;  // "\ No newline at end of file"

            // git always writes a marker column, so a BLANK context line arrives as " " and
            // becomes "" only after the marker is stripped. A zero-length line here is therefore
            // never content: it is the empty tail left behind by the patch's final newline.
            // Emitting it added a phantom blank row, carrying a line number, to every diff.
            if (l.empty())
                continue;

            if (markerColumns > 0)
            {
                // One marker per parent: '-' where the line is absent from that parent, '+' where
                // it is new relative to it, ' ' where unchanged. A line removed from ANY parent is
                // not in the merged result, so it only advances the old counter.
                const std::size_t markers = static_cast<std::size_t>(markerColumns);
                const std::string_view prefix = l.substr(0, std::min(markers, l.size()));
                const std::string_view text = l.size() > markers ? l.substr(markers)
                                                                 : std::string_view();

                if (prefix.find('-') != std::string_view::npos)
                    emit(DiffLineKind::Removed, oldNo++, kNoNumber, text);
                else if (prefix.find('+') != std::string_view::npos)
                    emit(DiffLineKind::Added, kNoNumber, newNo++, text);
                else
                    emit(DiffLineKind::Context, oldNo++, newNo++, text);
                continue;
            }

            if (l[0] == '+')
            {
                emit(DiffLineKind::Added, kNoNumber, newNo++, l.substr(1));
            }
            else if (l[0] == '-')
            {
                emit(DiffLineKind::Removed, oldNo++, kNoNumber, l.substr(1));
            }
            else
            {
                // Context line (leading space), or a line inside the hunk with no marker at all.
                const std::string_view text = l[0] == ' ' ? l.substr(1) : l;
                emit(DiffLineKind::Context, oldNo++, newNo++, text);
            }
        }

        return w.Finish();
    }
}
