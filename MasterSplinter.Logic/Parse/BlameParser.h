#pragma once

// git blame --porcelain parsing: git's output in, a packed buffer out.
//
// Moved out of the host (GitRepository.Blame.cs) during Phase D. Like DiffParser this is a free
// function over a string -- it touches neither git nor the process runner, so it is directly
// gtest-able with no fake.
//
// KEEP PORTABLE: no <windows.h>. Compiled with PrecompiledHeader=NotUsing.

#include <cstdint>
#include <string>
#include <string_view>

namespace ms::parse
{
    // Record layout for one blamed line. 72 bytes; the i64 sits at 8 so it stays 8-aligned.
    //
    //    0  i32  origLine
    //    4  i32  finalLine
    //    8  i64  authorTime    unix seconds, 0 when unknown
    //   16  i32  authorTz      offset in MINUTES, signed
    //   20  u8   isGroupStart  1 when this line starts a new commit's run
    //   21  u8   (reserved)
    //   22  u16  (reserved)
    //   24  {off,len} sha
    //   32  {off,len} author
    //   40  {off,len} authorEmail
    //   48  {off,len} summary
    //   56  {off,len} sourcePath
    //   64  {off,len} text
    //
    // The timestamp travels as seconds + offset rather than as a formatted string: building a
    // DateTimeOffset (and clamping a corrupt one) is a .NET concern, so it stays on the host.
    inline constexpr std::uint32_t kBlameRecordSize = 72;
    inline constexpr std::uint32_t kBlameOffOrigLine = 0;
    inline constexpr std::uint32_t kBlameOffFinalLine = 4;
    inline constexpr std::uint32_t kBlameOffAuthorTime = 8;
    inline constexpr std::uint32_t kBlameOffAuthorTz = 16;
    inline constexpr std::uint32_t kBlameOffIsGroupStart = 20;
    inline constexpr std::uint32_t kBlameOffSha = 24;
    inline constexpr std::uint32_t kBlameOffAuthor = 32;
    inline constexpr std::uint32_t kBlameOffEmail = 40;
    inline constexpr std::uint32_t kBlameOffSummary = 48;
    inline constexpr std::uint32_t kBlameOffSourcePath = 56;
    inline constexpr std::uint32_t kBlameOffText = 64;

    // Parses --porcelain output into a packed Kind::Blame buffer.
    //
    // blamedPath is the fallback source path: without -M/-C every line came from that file, and
    // the source path is what picks the host's syntax highlighter.
    std::string ParsePorcelainBlame(std::string_view porcelain, std::string_view blamedPath);

    // "+0200" / "-0730" as signed minutes. Anything unrecognised is 0 (UTC), which shifts the
    // displayed time but never loses the date the way a hard failure would lose the whole file.
    int ParseTimezoneMinutes(std::string_view tz);
}
