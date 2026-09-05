#pragma once

// Unified-diff parsing: git's patch text in, a packed buffer out (see Packed/PackedFormat.h).
//
// This moved out of the host (GitRepository.Diff.cs) so a macOS UI gets it for free and so the
// line text crosses the ABI length-prefixed rather than delimited. It is a free function over a
// string rather than a GitBackend method because it touches neither git nor the process runner --
// which is also what makes it directly gtest-able with no fake.
//
// KEEP PORTABLE: no <windows.h>. Compiled with PrecompiledHeader=NotUsing.

#include <cstdint>
#include <string>
#include <string_view>

namespace ms::parse
{
    // Kind values in the packed record's first byte. These MUST match the host's
    // Models/Diff.cs DiffLineKind declaration order -- the value travels as an integer.
    enum class DiffLineKind : std::uint8_t
    {
        Context = 0,
        Added = 1,
        Removed = 2,
        Hunk = 3,
    };

    // Header flag bits for a Kind::Diff buffer.
    inline constexpr std::uint16_t kDiffFlagBinary = 0x0001;

    // Record layout for one diff line. 20 bytes, every field 4-aligned.
    //
    //    0  u8   kind        DiffLineKind
    //    1  u8   (reserved)
    //    2  u16  (reserved)
    //    4  i32  oldNo       -1 when the line has no old-side number
    //    8  i32  newNo       -1 when the line has no new-side number
    //   12  u32  textOff     heap-relative
    //   16  u32  textLen
    //
    // Line numbers travel as integers rather than as strings: the host formats them for display,
    // so a 50k-line diff allocates no strings for its gutters at all.
    inline constexpr std::uint32_t kDiffRecordSize = 20;
    inline constexpr std::uint32_t kDiffOffKind = 0;
    inline constexpr std::uint32_t kDiffOffOldNo = 4;
    inline constexpr std::uint32_t kDiffOffNewNo = 8;
    inline constexpr std::uint32_t kDiffOffText = 12;

    // Parses git's unified (or combined "@@@") patch text into a packed Kind::Diff buffer.
    // Never fails: unparseable input yields an empty record set, which the host renders as an
    // empty diff pane rather than an error.
    std::string ParseUnifiedDiff(std::string_view raw);
}
