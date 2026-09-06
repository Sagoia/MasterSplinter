#pragma once

// Commit log record parsing: git's delimited log stream in, a packed buffer out.
//
// Moved out of the host (GitRepository.History.cs) during Phase D, alongside the decoration
// parser. Serves both `git log` and `git log --grep` -- the two produce byte-identical records,
// which is why one parser and one format string cover both.
//
// KEEP PORTABLE: no <windows.h>. Compiled with PrecompiledHeader=NotUsing.

#include <cstdint>
#include <string>
#include <string_view>

namespace ms::parse
{
    // Badge kinds, tagging each decoration. These MUST match the host's Models/Commits.cs
    // BadgeKind declaration order -- the value travels as an integer.
    enum class BadgeKind : std::uint32_t
    {
        LocalBranch = 0,
        RemoteBranch = 1,
        Tag = 2,
        Head = 3,
    };

    // Record layout for one commit. 104 bytes; the two i64s sit at 0 and 8 so they stay 8-aligned.
    //
    //    0  i64  authorTime     unix seconds
    //    8  i64  commitTime     unix seconds
    //   16  i32  authorTz       offset in MINUTES, signed
    //   20  i32  commitTz
    //   24  {off,len}   fullHash
    //   32  {off,len}   shortHash
    //   40  {off,count} parents      array of {off,len}, 8 bytes each
    //   48  {off,len}   authorName
    //   56  {off,len}   authorEmail
    //   64  {off,len}   committerName
    //   72  {off,len}   committerEmail
    //   80  {off,count} badges       array of {tag,off,len}, 12 bytes each
    //   88  {off,len}   subject
    //   96  {off,len}   body
    inline constexpr std::uint32_t kLogRecordSize = 104;
    inline constexpr std::uint32_t kLogOffAuthorTime = 0;
    inline constexpr std::uint32_t kLogOffCommitTime = 8;
    inline constexpr std::uint32_t kLogOffAuthorTz = 16;
    inline constexpr std::uint32_t kLogOffCommitTz = 20;
    inline constexpr std::uint32_t kLogOffFullHash = 24;
    inline constexpr std::uint32_t kLogOffShortHash = 32;
    inline constexpr std::uint32_t kLogOffParents = 40;
    inline constexpr std::uint32_t kLogOffAuthorName = 48;
    inline constexpr std::uint32_t kLogOffAuthorEmail = 56;
    inline constexpr std::uint32_t kLogOffCommitterName = 64;
    inline constexpr std::uint32_t kLogOffCommitterEmail = 72;
    inline constexpr std::uint32_t kLogOffBadges = 80;
    inline constexpr std::uint32_t kLogOffSubject = 88;
    inline constexpr std::uint32_t kLogOffBody = 96;

    // Records shorter than this many fields are dropped. It is what keeps git's error text off
    // the commit list, which matters because Log deliberately ignores git's exit code.
    inline constexpr std::size_t kLogFieldFloor = 13;

    // Parses the stream MsGitLog / MsGitSearchLog produce into a packed Kind::Log buffer.
    //
    // Records are separated by NUL (git's -z), NOT by 0x1E, and the message arrives as a single
    // trailing %B field rather than as %s + %b. Both changes exist for the same reason: a commit
    // message can legitimately contain 0x1E or 0x1F, and under the old format either byte
    // desynced the stream -- a 0x1E split one record in two and a 0x1F shifted every later field.
    // NUL is the one byte git guarantees is absent from commit data, and with the message last
    // there is nothing after it to shift.
    std::string ParseLogRecords(std::string_view raw);

    // The same records, plus the commit-graph display list in the buffer's EXTRA section
    // (Graph/GraphLayout.h describes its bytes).
    //
    // One export, one git spawn, one allocation: the layout needs each commit's parents resolved
    // to ROW positions, and the only place that mapping exists for free is right here, while the
    // records are being built. Running it as a separate pass would mean either a second walk of
    // the log -- which could disagree with the first if a ref moved in between -- or handing the
    // host a job it would have to hand straight back.
    // oldestFirst must match git's --reverse; records remain in the order git returned them.
    std::string ParseLogRecordsWithGraph(std::string_view raw, bool oldestFirst = false);

    // Splits git's raw message (%B) into the subject and body the host displays, reproducing what
    // %s and %b used to emit: the subject is the first paragraph with its newlines folded to
    // spaces, the body is everything after the blank line that ends it.
    void SplitMessage(std::string_view raw, std::string& subject, std::string& body);
}
