// KEEP PORTABLE: no <windows.h>. Compiled with PrecompiledHeader=NotUsing.

#include "RefParser.h"

#include "BlameParser.h"   // ParseTimezoneMinutes
#include "../Packed/PackedWriter.h"

#include <vector>

namespace ms::parse
{
    namespace
    {
        using packed::PackedWriter;
        using packed::StringRef;

        constexpr char kUs = '\x1f';

        bool IsDigit(char c) { return c >= '0' && c <= '9'; }

        long long ToNumber(std::string_view s)
        {
            long long value = 0;
            for (char c : s)
            {
                if (!IsDigit(c))
                    break;
                if (value > 4000000000000000000LL)
                    break;
                value = value * 10 + (c - '0');
            }
            return value;
        }

        std::string_view Trim(std::string_view s)
        {
            while (!s.empty() && (s.front() == ' ' || s.front() == '\t' ||
                                  s.front() == '\n' || s.front() == '\r'))
                s.remove_prefix(1);
            while (!s.empty() && (s.back() == ' ' || s.back() == '\t' ||
                                  s.back() == '\n' || s.back() == '\r'))
                s.remove_suffix(1);
            return s;
        }

        bool StartsWith(std::string_view s, std::string_view prefix)
        {
            return s.size() >= prefix.size() && s.substr(0, prefix.size()) == prefix;
        }

        // Splits one record into exactly `want` fields; the last keeps every remaining byte, so a
        // free-form trailing field cannot shift anything. Returns false when the record is short
        // of the floor, which is what keeps git's error text out of these lists.
        bool SplitFields(std::string_view record, std::size_t want,
                         std::vector<std::string_view>& out)
        {
            out.clear();
            std::size_t at = 0;
            while (out.size() + 1 < want)
            {
                const std::size_t us = record.find(kUs, at);
                if (us == std::string_view::npos)
                    return false;
                out.push_back(record.substr(at, us - at));
                at = us + 1;
            }
            out.push_back(record.substr(at));
            return true;
        }

        // Walks a NUL-separated stream, handing each non-empty record to `body`.
        template <typename Body>
        void ForEachRecord(std::string_view raw, Body body)
        {
            std::size_t pos = 0;
            while (pos < raw.size())
            {
                const std::size_t nul = raw.find('\0', pos);
                std::string_view record = raw.substr(
                    pos, nul == std::string_view::npos ? std::string_view::npos : nul - pos);
                pos = (nul == std::string_view::npos) ? raw.size() : nul + 1;

                record = Trim(record);
                if (!record.empty())
                    body(record);
            }
        }

        // A number following `label` inside `track`, or 0.
        std::int32_t After(std::string_view track, std::string_view label)
        {
            const std::size_t at = track.find(label);
            if (at == std::string_view::npos)
                return 0;
            return static_cast<std::int32_t>(ToNumber(track.substr(at + label.size())));
        }
    }

    int ParseIsoOffsetMinutes(std::string_view iso)
    {
        // The trailing offset of an ISO-8601 timestamp: "...T03:04:05+07:00", or "Z" for UTC.
        //
        // Used instead of --date=format:%z here, and NOT by accident: that option also rewrites
        // %gd, turning the reflog selector "HEAD@{0}" into "HEAD@{+0700}". Verified against git
        // 2.54. The commit log has no %gd, which is why it can use the cheaper form.
        if (iso.empty())
            return 0;
        if (iso.back() == 'Z' || iso.back() == 'z')
            return 0;

        // Scan back for the sign that starts the offset; a date also contains '-' characters, so
        // bound the search to the last few bytes rather than searching the whole string.
        const std::size_t limit = iso.size() > 6 ? iso.size() - 7 : 0;
        for (std::size_t i = iso.size(); i-- > limit;)
        {
            const char c = iso[i];
            if (c != '+' && c != '-')
                continue;

            std::string_view rest = iso.substr(i + 1);
            std::string compact;
            for (char ch : rest)
            {
                if (ch != ':')
                    compact.push_back(ch);
            }
            if (compact.size() != 4)
                return 0;

            const int h = static_cast<int>(ToNumber(std::string_view(compact).substr(0, 2)));
            const int m = static_cast<int>(ToNumber(std::string_view(compact).substr(2, 2)));
            const int total = h * 60 + m;
            return c == '-' ? -total : total;
        }
        return 0;
    }

    void ParseTrack(std::string_view track, std::int32_t& ahead, std::int32_t& behind)
    {
        ahead = After(track, "ahead ");
        behind = After(track, "behind ");
    }

    void SplitStashSubject(std::string_view subject, std::string& branch, std::string& message)
    {
        branch.clear();
        message.assign(subject);

        std::string_view rest;
        if (StartsWith(subject, "WIP on "))
            rest = subject.substr(7);
        else if (StartsWith(subject, "On "))
            rest = subject.substr(3);
        else
            return;

        // The branch runs to the first colon; git's own format guarantees ": " after it.
        const std::size_t colon = rest.find(':');
        if (colon == std::string_view::npos || colon == 0)
            return;
        if (colon + 1 >= rest.size() || rest[colon + 1] != ' ')
            return;

        branch.assign(rest.substr(0, colon));
        message.assign(rest.substr(colon + 2));
    }

    std::string ParseRefDetails(std::string_view nulSeparated)
    {
        PackedWriter w(packed::Kind::Refs, kRefRecordSize);
        std::vector<std::string_view> f;

        ForEachRecord(nulSeparated, [&](std::string_view record)
        {
            if (!SplitFields(record, 8, f))
                return;

            const std::string_view refName = f[0];

            if (StartsWith(refName, "refs/heads/"))
            {
                const std::string_view track = f[5];
                std::int32_t ahead = 0, behind = 0;
                ParseTrack(track, ahead, behind);

                const StringRef refRef = w.AddString(refName);
                const StringRef nameRef = w.AddString(refName.substr(11));
                const StringRef shaRef = w.AddString(f[1]);
                const StringRef upstreamRef = w.AddString(f[4]);

                w.BeginRecord();
                w.PutU8(static_cast<std::uint8_t>(RefKind::Branch));
                w.PutU8(Trim(f[6]) == "*" ? 1 : 0);
                w.PutU8(track == "gone" ? 1 : 0);
                w.PutU8(0);
                w.PutI32(ahead);
                w.PutI32(behind);
                w.PutRef(refRef);
                w.PutRef(nameRef);
                w.PutRef(shaRef);
                w.PutRef(upstreamRef);
                w.PutRef(StringRef{});
                w.EndRecord();
                return;
            }

            if (StartsWith(refName, "refs/tags/"))
            {
                // %(objectname) is the tag OBJECT for an annotated tag; %(*objectname) peels it to
                // the commit, which is what compare and checkout need.
                const StringRef refRef = w.AddString(refName);
                const StringRef nameRef = w.AddString(refName.substr(10));
                const StringRef shaRef = w.AddString(!f[2].empty() ? f[2] : f[1]);

                w.BeginRecord();
                w.PutU8(static_cast<std::uint8_t>(RefKind::Tag));
                w.PutU8(0);
                w.PutU8(0);
                w.PutU8(f[3] == "tag" ? 1 : 0);
                w.PutI32(0);
                w.PutI32(0);
                w.PutRef(refRef);
                w.PutRef(nameRef);
                w.PutRef(shaRef);
                w.PutRef(StringRef{});
                w.PutRef(StringRef{});
                w.EndRecord();
                return;
            }

            if (StartsWith(refName, "refs/remotes/"))
            {
                // Skip symbolic refs: refs/remotes/origin/HEAD is an alias for another branch and
                // would otherwise render as a phantom "HEAD" leaf under the remote.
                if (!f[7].empty())
                    return;

                const std::string_view shortName = refName.substr(13);
                const std::size_t slash = shortName.find('/');
                if (slash == std::string_view::npos)
                    return;

                const StringRef refRef = w.AddString(refName);
                const StringRef nameRef = w.AddString(shortName.substr(slash + 1));
                const StringRef shaRef = w.AddString(f[1]);
                const StringRef remoteRef = w.AddString(shortName.substr(0, slash));

                w.BeginRecord();
                w.PutU8(static_cast<std::uint8_t>(RefKind::RemoteBranch));
                w.PutU8(0);
                w.PutU8(0);
                w.PutU8(0);
                w.PutI32(0);
                w.PutI32(0);
                w.PutRef(refRef);
                w.PutRef(nameRef);
                w.PutRef(shaRef);
                w.PutRef(StringRef{});
                w.PutRef(remoteRef);
                w.EndRecord();
            }
        });

        return w.Finish();
    }

    std::string ParseReflog(std::string_view nulSeparated)
    {
        PackedWriter w(packed::Kind::Reflog, kReflogRecordSize);
        std::vector<std::string_view> f;
        std::int32_t index = 0;

        ForEachRecord(nulSeparated, [&](std::string_view record)
        {
            if (!SplitFields(record, 8, f))
                return;

            // git packs the operation and its argument into one subject:
            // "checkout: moving from main to feature" -> ("checkout", "moving from ...").
            const std::string_view gs = f[7];
            const std::size_t colon = gs.find(": ");
            const std::string_view action = colon != std::string_view::npos ? gs.substr(0, colon) : gs;
            const std::string_view detail = colon != std::string_view::npos ? gs.substr(colon + 2)
                                                                           : std::string_view();

            const StringRef selectorRef = w.AddString(f[0]);
            const StringRef shaRef = w.AddString(f[1]);
            const StringRef shortRef = w.AddString(f[2]);
            const StringRef actionRef = w.AddString(action);
            const StringRef detailRef = w.AddString(detail);
            const StringRef subjectRef = w.AddString(f[6]);
            const StringRef authorRef = w.AddString(f[5]);

            w.BeginRecord();
            w.PutI64(ToNumber(f[3]));                     // %at
            w.PutI32(ParseIsoOffsetMinutes(f[4]));        // the offset from %aI
            w.PutI32(index++);
            w.PutRef(selectorRef);
            w.PutRef(shaRef);
            w.PutRef(shortRef);
            w.PutRef(actionRef);
            w.PutRef(detailRef);
            w.PutRef(subjectRef);
            w.PutRef(authorRef);
            w.EndRecord();
        });

        return w.Finish();
    }

    std::string ParseStashList(std::string_view nulSeparated)
    {
        PackedWriter w(packed::Kind::Stash, kStashRecordSize);
        std::vector<std::string_view> f;
        std::int32_t index = 0;
        std::string branch, message;

        ForEachRecord(nulSeparated, [&](std::string_view record)
        {
            if (!SplitFields(record, 7, f))
                return;

            SplitStashSubject(f[6], branch, message);

            const StringRef selectorRef = w.AddString(f[0]);
            const StringRef shaRef = w.AddString(f[1]);
            const StringRef shortRef = w.AddString(f[2]);
            const StringRef messageRef = w.AddString(message);
            const StringRef branchRef = w.AddString(branch);
            const StringRef authorRef = w.AddString(f[5]);

            w.BeginRecord();
            w.PutI64(ToNumber(f[3]));                     // %at
            w.PutI32(ParseIsoOffsetMinutes(f[4]));        // the offset from %aI
            w.PutI32(index++);
            w.PutRef(selectorRef);
            w.PutRef(shaRef);
            w.PutRef(shortRef);
            w.PutRef(messageRef);
            w.PutRef(branchRef);
            w.PutRef(authorRef);
            w.EndRecord();
        });

        return w.Finish();
    }
}
