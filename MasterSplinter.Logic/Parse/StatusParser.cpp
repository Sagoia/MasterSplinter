// KEEP PORTABLE: no <windows.h>. Compiled with PrecompiledHeader=NotUsing.

#include "StatusParser.h"

#include "../Packed/PackedWriter.h"

#include <vector>

namespace ms::parse
{
    namespace
    {
        using packed::PackedWriter;
        using packed::StringRef;

        // Splits git's -z output. NUL is a separator, and git terminates the last token with one
        // too, so a trailing empty token is normal and dropped.
        std::vector<std::string_view> SplitNul(std::string_view s)
        {
            std::vector<std::string_view> out;
            std::size_t pos = 0;
            while (pos < s.size())
            {
                const std::size_t nul = s.find('\0', pos);
                if (nul == std::string_view::npos)
                {
                    out.push_back(s.substr(pos));
                    break;
                }
                out.push_back(s.substr(pos, nul - pos));
                pos = nul + 1;
            }
            return out;
        }

        void Emit(PackedWriter& w, FileChangeStatus status, StatusSection section,
                  bool isWorkingTree, std::string_view path, std::string_view oldPath)
        {
            const StringRef pathRef = w.AddString(path);
            const StringRef oldRef = w.AddString(oldPath);
            w.BeginRecord();
            w.PutU8(static_cast<std::uint8_t>(status));
            w.PutU8(static_cast<std::uint8_t>(section));
            w.PutU8(isWorkingTree ? 1 : 0);
            w.PutU8(0);
            w.PutRef(pathRef);
            w.PutRef(oldRef);
            w.EndRecord();
        }
    }

    FileChangeStatus MapStatus(char c)
    {
        switch (c)
        {
        case 'A': return FileChangeStatus::Added;
        case 'D': return FileChangeStatus::Deleted;
        case 'R': return FileChangeStatus::Renamed;
        case 'C': return FileChangeStatus::Renamed;
        case '?': return FileChangeStatus::Untracked;
        case 'U': return FileChangeStatus::Conflicted;
        default:  return FileChangeStatus::Modified;   // M, T, ...
        }
    }

    bool IsUnmerged(char x, char y)
    {
        return (x == 'D' && y == 'D') ||
               (x == 'A' && y == 'U') ||
               (x == 'U' && y == 'D') ||
               (x == 'U' && y == 'A') ||
               (x == 'D' && y == 'U') ||
               (x == 'A' && y == 'A') ||
               (x == 'U' && y == 'U');
    }

    std::string ParseNameStatus(std::string_view nulSeparated)
    {
        PackedWriter w(packed::Kind::NameStatus, kFileRecordSize);

        // Records are NOT fixed width: a token is a status, followed by ONE path -- or by TWO
        // (old then new) when the status starts with R or C. So walk the stream; there is no row
        // separator to split on.
        const std::vector<std::string_view> t = SplitNul(nulSeparated);
        for (std::size_t i = 0; i < t.size(); ++i)
        {
            const std::string_view status = t[i];
            if (status.empty())
                continue;

            const bool pair = status[0] == 'R' || status[0] == 'C';
            const std::size_t extra = pair ? 2 : 1;
            if (i + extra >= t.size())
                break;   // truncated tail

            // For rename/copy, diff and show must target the NEW path (the last field).
            Emit(w, MapStatus(status[0]), StatusSection::Staged, false,
                 t[i + extra], pair ? t[i + 1] : std::string_view());
            i += extra;
        }

        return w.Finish();
    }

    std::string ParsePorcelainStatus(std::string_view nulSeparated)
    {
        PackedWriter w(packed::Kind::Status, kFileRecordSize);

        const std::vector<std::string_view> records = SplitNul(nulSeparated);
        for (std::size_t i = 0; i < records.size(); ++i)
        {
            const std::string_view rec = records[i];
            if (rec.size() < 4 || rec[2] != ' ')
                continue;

            const char x = rec[0];
            const char y = rec[1];
            const std::string_view path = rec.substr(3);
            std::string_view oldPath;

            // -z puts the NEW path in the "XY <path>" token and the original in the token after.
            if (x == 'R' || x == 'C' || y == 'R' || y == 'C')
            {
                ++i;
                if (i < records.size())
                    oldPath = records[i];
            }

            if (x == '?' && y == '?')
            {
                Emit(w, FileChangeStatus::Untracked, StatusSection::Untracked, true, path, {});
                continue;
            }

            // BEFORE the staged/unstaged split: both halves see a non-blank column for an
            // unmerged pair, so a plain "UU" would otherwise be reported as a staged modification
            // AND an unstaged one -- the same conflicted file listed twice, with no hint that
            // anything is wrong.
            if (IsUnmerged(x, y))
            {
                Emit(w, FileChangeStatus::Conflicted, StatusSection::Conflicted, true, path, {});
                continue;
            }

            if (x != ' ' && x != '?')
            {
                Emit(w, MapStatus(x), StatusSection::Staged, true, path,
                     (x == 'R' || x == 'C') ? oldPath : std::string_view());
            }
            if (y != ' ' && y != '?')
            {
                Emit(w, MapStatus(y), StatusSection::Unstaged, true, path,
                     (y == 'R' || y == 'C') ? oldPath : std::string_view());
            }
        }

        return w.Finish();
    }
}
