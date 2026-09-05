#pragma once

// Refs, reflog and stash: git's delimited walks in, packed buffers out.
//
// The last three host parsers to move in Phase D. All three streams are NUL-separated (-z) for
// the same reason the commit log is: a branch name cannot contain 0x1F, but a reflog subject and
// a stash message are free-form text that can.
//
// KEEP PORTABLE: no <windows.h>. Compiled with PrecompiledHeader=NotUsing.

#include <cstdint>
#include <string>
#include <string_view>

namespace ms::parse
{
    // ---- Refs (the sidebar) ---------------------------------------------------------------------

    // Which kind of ref a record describes. The host buckets on this rather than re-testing the
    // refs/heads, refs/tags and refs/remotes prefixes itself.
    enum class RefKind : std::uint8_t
    {
        Branch = 0,
        Tag = 1,
        RemoteBranch = 2,
    };

    // Record layout for one ref. 52 bytes.
    //
    //    0  u8  kind           RefKind
    //    1  u8  isCurrent      branches only: %(HEAD) was "*"
    //    2  u8  upstreamGone   branches only: the track field said "gone"
    //    3  u8  isAnnotated    tags only: %(objecttype) was "tag"
    //    4  i32 ahead          branches only
    //    8  i32 behind
    //   12  {off,len} refName  the full "refs/..." name
    //   20  {off,len} name     short name: the branch/tag name, or the part below the remote
    //   28  {off,len} sha      for a tag this is the PEELED commit, which is what compare needs
    //   36  {off,len} upstream branches only
    //   44  {off,len} remote   remote-tracking only
    inline constexpr std::uint32_t kRefRecordSize = 52;
    inline constexpr std::uint32_t kRefOffKind = 0;
    inline constexpr std::uint32_t kRefOffIsCurrent = 1;
    inline constexpr std::uint32_t kRefOffUpstreamGone = 2;
    inline constexpr std::uint32_t kRefOffIsAnnotated = 3;
    inline constexpr std::uint32_t kRefOffAhead = 4;
    inline constexpr std::uint32_t kRefOffBehind = 8;
    inline constexpr std::uint32_t kRefOffRefName = 12;
    inline constexpr std::uint32_t kRefOffName = 20;
    inline constexpr std::uint32_t kRefOffSha = 28;
    inline constexpr std::uint32_t kRefOffUpstream = 36;
    inline constexpr std::uint32_t kRefOffRemote = 44;

    // git for-each-ref, 8 fields per record. Yields Kind::Refs.
    std::string ParseRefDetails(std::string_view nulSeparated);

    // ---- Reflog ----------------------------------------------------------------------------------

    // Record layout for one reflog entry. 72 bytes.
    //
    //    0  i64 when            unix seconds
    //    8  i32 tz              offset in MINUTES, signed
    //   12  i32 index           position in the walk, 0-based
    //   16  {off,len} selector  "HEAD@{3}"
    //   24  {off,len} sha
    //   32  {off,len} shortSha
    //   40  {off,len} action    the part of %gs before ": "
    //   48  {off,len} detail    the part after it, empty when there is none
    //   56  {off,len} subject   the commit subject %s
    //   64  {off,len} author
    inline constexpr std::uint32_t kReflogRecordSize = 72;
    inline constexpr std::uint32_t kReflogOffWhen = 0;
    inline constexpr std::uint32_t kReflogOffTz = 8;
    inline constexpr std::uint32_t kReflogOffIndex = 12;
    inline constexpr std::uint32_t kReflogOffSelector = 16;
    inline constexpr std::uint32_t kReflogOffSha = 24;
    inline constexpr std::uint32_t kReflogOffShortSha = 32;
    inline constexpr std::uint32_t kReflogOffAction = 40;
    inline constexpr std::uint32_t kReflogOffDetail = 48;
    inline constexpr std::uint32_t kReflogOffSubject = 56;
    inline constexpr std::uint32_t kReflogOffAuthor = 64;

    // git reflog show, 7 fields per record. Yields Kind::Reflog.
    std::string ParseReflog(std::string_view nulSeparated);

    // ---- Stash -----------------------------------------------------------------------------------

    // Record layout for one stash entry. 64 bytes.
    //
    //    0  i64 when
    //    8  i32 tz
    //   12  i32 index
    //   16  {off,len} selector  "stash@{0}" -- POSITIONAL, invalid after the next drop or pop
    //   24  {off,len} sha
    //   32  {off,len} shortSha
    //   40  {off,len} message   the subject with git's "WIP on <branch>: " prefix removed
    //   48  {off,len} branch    the branch that prefix named, empty when it did not match
    //   56  {off,len} author
    inline constexpr std::uint32_t kStashRecordSize = 64;
    inline constexpr std::uint32_t kStashOffWhen = 0;
    inline constexpr std::uint32_t kStashOffTz = 8;
    inline constexpr std::uint32_t kStashOffIndex = 12;
    inline constexpr std::uint32_t kStashOffSelector = 16;
    inline constexpr std::uint32_t kStashOffSha = 24;
    inline constexpr std::uint32_t kStashOffShortSha = 32;
    inline constexpr std::uint32_t kStashOffMessage = 40;
    inline constexpr std::uint32_t kStashOffBranch = 48;
    inline constexpr std::uint32_t kStashOffAuthor = 56;

    // git stash list, 6 fields per record. Yields Kind::Stash.
    std::string ParseStashList(std::string_view nulSeparated);

    // git composes a stash's reflog subject as "WIP on <branch>: <sha> <subject>", or
    // "On <branch>: <text>" when the user supplied a message. Splitting it gives the sidebar a
    // branch to show and a message that is not three-quarters boilerplate. Anything matching
    // neither shape is kept whole -- a custom message is not worth mangling.
    void SplitStashSubject(std::string_view subject, std::string& branch, std::string& message);

    // "ahead 2, behind 1" from %(upstream:track,nobracket). Anything unrecognised leaves both at
    // 0, so a branch simply shows no arrows rather than a wrong number.
    void ParseTrack(std::string_view track, std::int32_t& ahead, std::int32_t& behind);

    // The trailing offset of an ISO-8601 timestamp ("...+07:00", or "Z"), in signed minutes.
    //
    // Reflog and stash take their offset this way rather than through --date=format:%z, because
    // that option ALSO rewrites %gd -- the reflog selector "HEAD@{0}" comes back as
    // "HEAD@{+0700}". Verified against git 2.54. The commit log has no %gd and uses the cheaper
    // form.
    int ParseIsoOffsetMinutes(std::string_view iso);
}
