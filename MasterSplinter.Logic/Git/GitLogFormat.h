#pragma once
// The commit-record format, shared by Log and SearchLog.
//
// SHARED ON PURPOSE: the host parses both positionally with a field-count floor, so if the two
// ever drifted, search results would silently mis-map into the wrong columns. Keeping one
// definition is what makes that impossible rather than merely unlikely.
//
// KEEP PORTABLE: no <windows.h>.

#include "GitArgs.h"

namespace ms
{
    // The 13-field commit record: full %H, short %h, parents %P, author name/email, author
    // unix time %at and author tz offset %ad, committer name/email, committer time %ct and tz
    // %cd, ref decorations %D, and the RAW message %B.
    //
    // TWO deliberate choices here, both about the same bug. A commit message can legitimately
    // contain 0x1E or 0x1F, and the old format could not survive either: records ended with
    // %x1e, so a 0x1E in a body split one record in two (the fragment then dropped by the
    // field-count floor), and the message was split across %s and %b, so a 0x1F in a subject
    // shifted every field after it -- truncating the subject and losing the body. Both were
    // reproduced against git 2.54.
    //
    //   1. Records are separated by NUL (-z), the one byte git guarantees is absent from commit
    //      data, instead of by a byte the payload can contain.
    //   2. The message is ONE trailing %B field instead of %s + %b, so a bounded split leaves
    //      every remaining byte -- separators included -- inside it. Parse/LogParser reproduces
    //      what %s and %b emitted by splitting the message itself.
    //
    // Dates travel as unix seconds plus a numeric offset rather than as ISO text, which costs two
    // fewer heap strings per commit and leaves DateTimeOffset construction to the host.
    //
    // NOTE: log --pretty uses "%xNN" hex escapes. for-each-ref uses "%xx" instead -- see
    // RefDetails, where writing %x1f would emit the literal text.
    inline constexpr const char* kLogFormat =
        "--pretty=format:%H%x1f%h%x1f%P%x1f%an%x1f%ae%x1f%at%x1f%ad%x1f%cn%x1f%ce%x1f%ct%x1f%cd%x1f%D%x1f%B";

    // Makes %ad and %cd emit ONLY the numeric timezone offset ("+0700"); the timestamps
    // themselves come from %at and %ct.
    inline constexpr const char* kLogDateFormat = "--date=format:%z";

    // The three flags that must travel together. Bundled for the same reason RunPathList bundles
    // -z with its NUL translation: splitting them up is how they drift apart. Dropping -z alone
    // silently reintroduces the record desync, and dropping the date format turns every commit
    // timestamp into 1970.
    inline GitArgs& AddLogRecordFlags(GitArgs& args)
    {
        return args.Add("-z").Add(kLogDateFormat).Add(kLogFormat);
    }

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
