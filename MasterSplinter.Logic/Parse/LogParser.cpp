// KEEP PORTABLE: no <windows.h>. Compiled with PrecompiledHeader=NotUsing.

#include "LogParser.h"

#include "BlameParser.h"   // ParseTimezoneMinutes
#include "../Packed/PackedWriter.h"

#include <vector>

namespace ms::parse
{
    namespace
    {
        using packed::ArrayRef;
        using packed::PackedWriter;
        using packed::StringRef;
        using packed::TaggedRef;

        constexpr char kUs = '\x1f';   // field separator inside a record
        constexpr char kNul = '\0';    // record separator (git -z)

        bool IsDigit(char c) { return c >= '0' && c <= '9'; }

        long long ToNumber(std::string_view s)
        {
            bool negative = false;
            std::size_t i = 0;
            if (i < s.size() && (s[i] == '-' || s[i] == '+'))
            {
                negative = s[i] == '-';
                ++i;
            }
            long long value = 0;
            for (; i < s.size() && IsDigit(s[i]); ++i)
            {
                if (value > 4000000000000000000LL)
                    break;
                value = value * 10 + (s[i] - '0');
            }
            return negative ? -value : value;
        }

        std::string_view TrimTrailingNewlines(std::string_view s)
        {
            while (!s.empty() && (s.back() == '\n' || s.back() == '\r'))
                s.remove_suffix(1);
            return s;
        }

        std::string_view Trim(std::string_view s)
        {
            while (!s.empty() && (s.front() == ' ' || s.front() == '\t'))
                s.remove_prefix(1);
            while (!s.empty() && (s.back() == ' ' || s.back() == '\t'))
                s.remove_suffix(1);
            return s;
        }

        bool StartsWith(std::string_view s, std::string_view prefix)
        {
            return s.size() >= prefix.size() && s.substr(0, prefix.size()) == prefix;
        }

        // git's %D: "HEAD -> main, origin/main, tag: v1.0". One token can yield TWO badges.
        void AppendBadges(PackedWriter& w, std::string_view decorations,
                          std::vector<TaggedRef>& out)
        {
            std::size_t pos = 0;
            while (pos <= decorations.size())
            {
                const std::size_t comma = decorations.find(',', pos);
                std::string_view token = Trim(decorations.substr(
                    pos, comma == std::string_view::npos ? std::string_view::npos : comma - pos));
                pos = (comma == std::string_view::npos) ? decorations.size() + 1 : comma + 1;

                if (token.empty())
                    continue;

                if (StartsWith(token, "tag:"))
                {
                    out.push_back({ static_cast<std::uint32_t>(BadgeKind::Tag),
                                    w.AddString(Trim(token.substr(4))) });
                    continue;
                }

                const std::size_t arrow = token.find("->");
                if (arrow != std::string_view::npos)
                {
                    // "HEAD -> main": the HEAD pointer plus the local branch it points at.
                    const std::string_view left = Trim(token.substr(0, arrow));
                    const std::string_view right = Trim(token.substr(arrow + 2));
                    out.push_back({ static_cast<std::uint32_t>(BadgeKind::Head), w.AddString(left) });
                    if (!right.empty())
                        out.push_back({ static_cast<std::uint32_t>(BadgeKind::LocalBranch),
                                        w.AddString(right) });
                    continue;
                }

                if (token == "HEAD")
                {
                    out.push_back({ static_cast<std::uint32_t>(BadgeKind::Head), w.AddString(token) });
                    continue;
                }

                const BadgeKind kind = token.find('/') != std::string_view::npos
                                           ? BadgeKind::RemoteBranch
                                           : BadgeKind::LocalBranch;
                out.push_back({ static_cast<std::uint32_t>(kind), w.AddString(token) });
            }
        }
    }

    void SplitMessage(std::string_view raw, std::string& subject, std::string& body)
    {
        subject.clear();
        body.clear();

        const std::string_view message = TrimTrailingNewlines(raw);
        if (message.empty())
            return;

        // The subject paragraph ends at the first blank line. Handles CRLF as well as LF, since a
        // commit message keeps whatever line endings it was written with.
        std::size_t end = message.size();
        std::size_t bodyStart = message.size();
        for (std::size_t i = 0; i + 1 < message.size(); ++i)
        {
            if (message[i] != '\n')
                continue;
            if (message[i + 1] == '\n')
            {
                end = i;
                bodyStart = i + 2;
                break;
            }
            if (i + 2 < message.size() && message[i + 1] == '\r' && message[i + 2] == '\n')
            {
                end = i;
                bodyStart = i + 3;
                break;
            }
        }

        // git's %s folds the subject paragraph onto one line, replacing newlines with spaces.
        // Reproduced here rather than asked of git, because %s and %b as separate fields are
        // exactly what the delimiter bug fed on.
        subject.reserve(end);
        for (std::size_t i = 0; i < end; ++i)
        {
            const char c = message[i];
            if (c == '\r')
                continue;
            subject.push_back(c == '\n' ? ' ' : c);
        }

        if (bodyStart < message.size())
            body.assign(TrimTrailingNewlines(message.substr(bodyStart)));
    }

    std::string ParseLogRecords(std::string_view raw)
    {
        PackedWriter w(packed::Kind::Log, kLogRecordSize);

        std::vector<std::string_view> fields;
        std::vector<StringRef> parents;
        std::vector<TaggedRef> badges;
        std::string subject, body;

        std::size_t pos = 0;
        while (pos < raw.size())
        {
            const std::size_t nul = raw.find(kNul, pos);
            std::string_view record = raw.substr(
                pos, nul == std::string_view::npos ? std::string_view::npos : nul - pos);
            pos = (nul == std::string_view::npos) ? raw.size() : nul + 1;

            // Defensive: -z separates rather than terminates, so no leading newline is expected.
            while (!record.empty() && (record.front() == '\n' || record.front() == '\r'))
                record.remove_prefix(1);
            if (record.empty())
                continue;

            // Split into exactly kLogFieldFloor fields: the last one is the raw message and keeps
            // every remaining byte, separators included. That bound is what makes a 0x1F inside a
            // commit message harmless rather than a field shift.
            fields.clear();
            std::size_t at = 0;
            while (fields.size() + 1 < kLogFieldFloor)
            {
                const std::size_t us = record.find(kUs, at);
                if (us == std::string_view::npos)
                    break;
                fields.push_back(record.substr(at, us - at));
                at = us + 1;
            }
            if (fields.size() + 1 < kLogFieldFloor)
                continue;   // git's error text, or a truncated record: drop it
            fields.push_back(record.substr(at));

            parents.clear();
            std::string_view parentList = fields[2];
            std::size_t p = 0;
            while (p < parentList.size())
            {
                while (p < parentList.size() && parentList[p] == ' ')
                    ++p;
                const std::size_t space = parentList.find(' ', p);
                const std::string_view one = parentList.substr(
                    p, space == std::string_view::npos ? std::string_view::npos : space - p);
                if (!one.empty())
                    parents.push_back(w.AddString(one));
                if (space == std::string_view::npos)
                    break;
                p = space + 1;
            }

            badges.clear();
            AppendBadges(w, fields[11], badges);

            SplitMessage(fields[12], subject, body);

            const StringRef fullHash = w.AddString(fields[0]);
            const StringRef shortHash = w.AddString(fields[1]);
            const StringRef authorName = w.AddString(fields[3]);
            const StringRef authorEmail = w.AddString(fields[4]);
            const StringRef committerName = w.AddString(fields[7]);
            const StringRef committerEmail = w.AddString(fields[8]);
            const StringRef subjectRef = w.AddString(subject);
            const StringRef bodyRef = w.AddString(body);
            const ArrayRef parentArray = w.AddStringRefs(parents);
            const ArrayRef badgeArray = w.AddTaggedRefs(badges);

            w.BeginRecord();
            w.PutI64(ToNumber(fields[5]));                     // %at
            w.PutI64(ToNumber(fields[9]));                     // %ct
            w.PutI32(ParseTimezoneMinutes(fields[6]));         // %ad with --date=format:%z
            w.PutI32(ParseTimezoneMinutes(fields[10]));        // %cd
            w.PutRef(fullHash);
            w.PutRef(shortHash);
            w.PutArray(parentArray);
            w.PutRef(authorName);
            w.PutRef(authorEmail);
            w.PutRef(committerName);
            w.PutRef(committerEmail);
            w.PutArray(badgeArray);
            w.PutRef(subjectRef);
            w.PutRef(bodyRef);
            w.EndRecord();
        }

        return w.Finish();
    }
}
