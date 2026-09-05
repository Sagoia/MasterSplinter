#pragma once
// The commit-record format, shared by Log and SearchLog.
//
// SHARED ON PURPOSE: the host parses both positionally with a field-count floor, so if the two
// ever drifted, search results would silently mis-map into the wrong columns. Keeping one
// definition is what makes that impossible rather than merely unlikely.
//
// KEEP PORTABLE: no <windows.h>.

namespace ms
{
    // The 12-field commit record: full %H, short %h, parents %P, author name/email/ISO date,
    // committer name/email/ISO date, ref decorations %D (description badges), subject %s,
    // body %b. Records end with RS.
    //
    // NOTE: log --pretty uses "%xNN" hex escapes. for-each-ref uses "%xx" instead — see
    // RefDetails, where writing %x1f would emit the literal text.
    inline constexpr const char* kLogFormat =
        "--pretty=format:%H%x1f%h%x1f%P%x1f%an%x1f%ae%x1f%aI%x1f%cn%x1f%ce%x1f%cI%x1f%D%x1f%s%x1f%b%x1e";

    // order: 0 = date, 1 = topo, 2 = reverse-date, 3 = author-date. `reverse` receives whether
    // --reverse must be appended (mode 2 is date order, walked backwards).
    inline const char* LogOrderFlag(int order, bool& reverse)
    {
        reverse = false;
        switch (order)
        {
        case 1: return "--topo-order";
        case 2: reverse = true; return "--date-order"; // "Reverse Date Order"
        case 3: return "--author-date-order";
        default: return "--date-order";
        }
    }
}
