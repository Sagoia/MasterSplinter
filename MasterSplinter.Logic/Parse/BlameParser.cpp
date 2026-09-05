// KEEP PORTABLE: no <windows.h>. Compiled with PrecompiledHeader=NotUsing.

#include "BlameParser.h"

#include "../Packed/PackedWriter.h"

#include <unordered_map>

namespace ms::parse
{
    namespace
    {
        using packed::PackedWriter;
        using packed::StringRef;

        bool IsDigit(char c) { return c >= '0' && c <= '9'; }

        // Permissive on purpose: git's own numbers always parse, and anything else should cost one
        // line's gutter rather than the whole file.
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

        // The commit headers git emits only on a commit's FIRST group.
        struct CommitInfo
        {
            std::string author;
            std::string email;
            std::string summary;
            long long authorTime = 0;
            int authorTz = 0;
        };

        std::string_view TrimAngleBrackets(std::string_view s)
        {
            while (!s.empty() && (s.front() == '<' || s.front() == '>'))
                s.remove_prefix(1);
            while (!s.empty() && (s.back() == '<' || s.back() == '>'))
                s.remove_suffix(1);
            return s;
        }
    }

    int ParseTimezoneMinutes(std::string_view tz)
    {
        if (tz.size() != 5 || (tz[0] != '+' && tz[0] != '-'))
            return 0;
        for (std::size_t i = 1; i < 5; ++i)
        {
            if (!IsDigit(tz[i]))
                return 0;
        }
        const int h = static_cast<int>(ToNumber(tz.substr(1, 2)));
        const int m = static_cast<int>(ToNumber(tz.substr(3, 2)));
        const int total = h * 60 + m;
        return tz[0] == '-' ? -total : total;
    }

    std::string ParsePorcelainBlame(std::string_view porcelain, std::string_view blamedPath)
    {
        PackedWriter w(packed::Kind::Blame, kBlameRecordSize);

        // Interned once: every line repeats its sha and most repeat the same author and path, so
        // handing the same StringRef back keeps the heap proportional to commits, not to lines.
        std::unordered_map<std::string, CommitInfo> known;
        std::unordered_map<std::string, std::string> filenames;
        std::unordered_map<std::string, StringRef> interned;

        auto intern = [&](std::string_view s) -> StringRef
        {
            if (s.empty())
                return StringRef{};
            const std::string key(s);
            auto it = interned.find(key);
            if (it != interned.end())
                return it->second;
            const StringRef ref = w.AddString(s);
            interned.emplace(key, ref);
            return ref;
        };

        std::string sha;
        std::string previousSha;
        std::int32_t origLine = 0, finalLine = 0;
        std::string author, email, summary, filename;
        long long authorTime = 0;
        int authorTz = 0;
        bool headerPending = false;   // we have a sha and are reading its key/value headers

        std::size_t pos = 0;
        while (pos <= porcelain.size())
        {
            const std::size_t nl = porcelain.find('\n', pos);
            std::string_view line = porcelain.substr(
                pos, nl == std::string_view::npos ? std::string_view::npos : nl - pos);
            pos = (nl == std::string_view::npos) ? porcelain.size() + 1 : nl + 1;

            while (!line.empty() && line.back() == '\r')
                line.remove_suffix(1);
            if (line.empty())
                continue;

            if (line[0] == '\t')
            {
                // A content line closes the group's header block.
                if (headerPending && !author.empty())
                    known[sha] = CommitInfo{ author, email, summary, authorTime, authorTz };
                if (headerPending && !filename.empty())
                    filenames[sha] = filename;

                const CommitInfo* info = nullptr;
                if (auto it = known.find(sha); it != known.end())
                    info = &it->second;

                std::string_view source = blamedPath;
                if (auto it = filenames.find(sha); it != filenames.end())
                    source = it->second;

                w.BeginRecord();
                w.PutI32(origLine);
                w.PutI32(finalLine);
                w.PutI64(info ? info->authorTime : 0);
                w.PutI32(info ? info->authorTz : 0);
                w.PutU8(sha != previousSha ? 1 : 0);
                w.PutU8(0);
                w.PutU16(0);
                w.PutRef(intern(sha));
                w.PutRef(info ? intern(info->author) : StringRef{});
                w.PutRef(info ? intern(info->email) : StringRef{});
                w.PutRef(info ? intern(info->summary) : StringRef{});
                w.PutRef(intern(source));
                // NOT interned: line text is unique per line, so a cache would only cost memory.
                w.PutRef(w.AddString(line.substr(1)));
                w.EndRecord();

                previousSha = sha;
                headerPending = false;
                continue;
            }

            if (!headerPending)
            {
                // Group header: "<sha> <origLine> <finalLine> [<groupSize>]".
                std::size_t a = line.find(' ');
                if (a == std::string_view::npos)
                    continue;
                std::size_t b = line.find(' ', a + 1);
                if (b == std::string_view::npos)
                    continue;
                const std::size_t c = line.find(' ', b + 1);

                sha.assign(line.substr(0, a));
                origLine = static_cast<std::int32_t>(ToNumber(line.substr(a + 1, b - a - 1)));
                finalLine = static_cast<std::int32_t>(ToNumber(
                    line.substr(b + 1, c == std::string_view::npos ? std::string_view::npos : c - b - 1)));

                author.clear();
                email.clear();
                summary.clear();
                filename.clear();
                authorTime = 0;
                authorTz = 0;
                headerPending = true;
                continue;
            }

            const std::size_t space = line.find(' ');
            const std::string_view key = space == std::string_view::npos ? line : line.substr(0, space);
            const std::string_view value = space == std::string_view::npos ? std::string_view()
                                                                          : line.substr(space + 1);
            if (key == "author")
                author.assign(value);
            else if (key == "author-mail")
                email.assign(TrimAngleBrackets(value));
            else if (key == "author-time")
                authorTime = ToNumber(value);
            else if (key == "author-tz")
                authorTz = ParseTimezoneMinutes(value);
            else if (key == "summary")
                summary.assign(value);
            else if (key == "filename")
                filename.assign(value);
        }

        return w.Finish();
    }
}
