#pragma once

// Commit-graph lane layout: parent adjacency in, a display list out.
//
// Pure logic. No git, no process runner, no OS, no strings -- which is what makes it directly
// gtest-able and what will let a macOS renderer consume the identical bytes.
//
// The model is the standard incremental active-lane walk: visit rows from children to parents,
// find or allocate the lane each commit sits on, route its parents (continue / fork / join), and
// release lanes as branches end. It needs nothing but "which rows are this row's parents", which
// is why the input is plain integers.
//
// > Reference, do not copy. TortoiseGit/src/TortoiseProc/lanes.{h,cpp} is the qgit-derived Lanes
// > class and is GPLv2+. It was read for the shape of the state machine; nothing was copied, and
// > this implementation is deliberately much smaller (five lane operations, not 26 states).
//
// KEEP PORTABLE: no <windows.h>. Compiled with PrecompiledHeader=NotUsing.

#include <cstdint>
#include <string>
#include <vector>

namespace ms::graph
{
    // Per-row flags in the display list.
    inline constexpr std::uint8_t kFlagMerge = 0x01;      // more than one parent
    inline constexpr std::uint8_t kFlagRoot = 0x02;       // no parents at all
    inline constexpr std::uint8_t kFlagBoundary = 0x04;   // a parent lies outside the loaded window

    // Colour indices cycle through this many values; the host maps them to its own palette.
    inline constexpr std::uint8_t kColorCount = 6;

    // Hard caps. A lane index and a segment count each travel as one byte, and a repository that
    // needs more than 255 concurrent lanes is not going to be read off a screen anyway.
    inline constexpr std::size_t kMaxLanes = 255;
    inline constexpr std::size_t kMaxSegments = 255;

    // The display list. Every value is one byte except the leading count, so a renderer walks it
    // as a raw span with no per-row allocation:
    //
    //   u32 rowCount
    //   per row:  u8 laneCount · u8 dotLane · u8 colorIndex · u8 flags · u8 segCount
    //             then segCount x { u8 x1, u8 y1, u8 x2, u8 y2, u8 colorIndex }
    //
    // X is a lane index. Y is in HALF-ROW units: 0 = top edge, 1 = centre (where the dot sits),
    // 2 = bottom edge. Integers rather than fractions so the whole list stays byte-sized; the
    // renderer scales by rowHeight/2.
    inline constexpr std::uint32_t kSegmentSize = 5;
    inline constexpr std::uint32_t kRowHeaderSize = 5;

    inline constexpr std::uint8_t kTop = 0;
    inline constexpr std::uint8_t kCentre = 1;
    inline constexpr std::uint8_t kBottom = 2;

    // adjacency[i] holds the ROW INDICES of row i's parents, in git's own parent order, with -1
    // for a parent outside the loaded window. -1 is ordinary, not an error: the log is capped, so
    // an edge routinely leaves the window -- which is exactly what CommitIndex.PositionOfHash
    // returns for a miss, and what the host's tests already pin.
    // oldestFirst keeps adjacency in display order, but walks it backwards so children still
    // claim lanes before their parents. Output rows and segment Y coordinates follow the display.
    std::string BuildDisplayList(const std::vector<std::vector<std::int32_t>>& adjacency,
                                 bool oldestFirst = false);
}
