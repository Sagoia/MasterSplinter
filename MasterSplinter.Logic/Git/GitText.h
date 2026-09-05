#pragma once
// GitText — the small text helpers shared by every GitBackend area file.
//
// These were an anonymous namespace inside a single 1300-line GitBackend.cpp. Splitting that file
// by area (GitBackend.History.cpp, GitBackend.Refs.cpp, ...) meant the helpers had to become
// shared, so they live here as `inline` free functions: one definition, no ODR trouble, and no
// extra translation unit.
//
// KEEP PORTABLE: no <windows.h>.

#include <algorithm>
#include <string>
#include <vector>

namespace ms
{
    // Field separator inside a record is 0x1F; records are separated by 0x1E (emitted directly by
    // git's --pretty=format). These bytes never occur in normal commit text.
    inline constexpr char US = '\x1f';

    inline void TrimTrailingNewlines(std::string& s)
    {
        while (!s.empty() && (s.back() == '\n' || s.back() == '\r'))
            s.pop_back();
    }

    inline std::string Err(std::string message)
    {
        return std::string("ERR") + US + std::move(message);
    }

    // Map a finished git command to the "OK" / "ERR<US>message" contract.
    inline std::string OkOrErr(std::string out, int code, const char* fallback)
    {
        if (code == 0)
            return "OK";
        TrimTrailingNewlines(out);
        return Err(out.empty() ? std::string(fallback) : std::move(out));
    }

    // The one wording for a missing root, so it stays identical across every write op.
    inline std::string NoRoot()
    {
        return Err("No repository root was provided");
    }

    // Translate git's -z NUL separators to RS (0x1E).
    //
    // WHY -z AT ALL: in the line-based formats a path containing a quote, a backslash or a
    // control character is C-quoted, and core.quotePath=false does NOT turn that off - it only
    // stops non-ASCII from being escaped. -z is the only way to get the real bytes.
    //
    // The managed marshaller stops at the first NUL, so the payload cannot travel as NULs; RS
    // is the record separator the C# side already splits on.
    inline bool IsBlank(const std::string& s)
    {
        return s.find_first_not_of(" \t\r\n") == std::string::npos;
    }

    inline bool Contains(const std::vector<std::string>& haystack, const std::string& needle)
    {
        return std::find(haystack.begin(), haystack.end(), needle) != haystack.end();
    }

    // A leading '-' would be read as an option rather than a revision/path. git's
    // --end-of-options needs 2.24, so refuse instead of relying on it.
    inline bool LooksLikeOption(const std::string& s)
    {
        return !s.empty() && s[0] == '-';
    }
}
