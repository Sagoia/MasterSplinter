#include "pch.h"

#include <algorithm>
#include <string>
#include <vector>

#include "PackedRead.h"
#include "Graph/GraphLayout.h"
#include "Parse/LogParser.h"

// Lane layout. The input is pure parent adjacency -- row indices, -1 for a parent outside the
// loaded window -- so none of this needs git, a process runner or a fake.

namespace gl = ms::graph;

namespace
{
    struct Seg
    {
        int x1, y1, x2, y2, color;
        bool operator==(const Seg& o) const
        {
            return x1 == o.x1 && y1 == o.y1 && x2 == o.x2 && y2 == o.y2 && color == o.color;
        }
    };

    struct Row
    {
        int laneCount = 0;
        int dotLane = 0;
        int color = 0;
        int flags = 0;
        std::vector<Seg> segments;

        bool HasSegment(int x1, int y1, int x2, int y2) const
        {
            return std::any_of(segments.begin(), segments.end(), [&](const Seg& s)
            {
                return s.x1 == x1 && s.y1 == y1 && s.x2 == x2 && s.y2 == y2;
            });
        }

        // A lane that runs the full height of the row without touching the dot.
        bool PassesThrough(int lane) const { return HasSegment(lane, 0, lane, 2); }
    };

    // Decodes the display list, mirroring what a renderer does.
    std::vector<Row> Decode(const std::string& blob)
    {
        auto byte = [&](std::size_t i) { return static_cast<int>(static_cast<unsigned char>(blob[i])); };

        std::size_t at = 0;
        const std::uint32_t count = static_cast<std::uint32_t>(byte(0)) |
                                    (static_cast<std::uint32_t>(byte(1)) << 8) |
                                    (static_cast<std::uint32_t>(byte(2)) << 16) |
                                    (static_cast<std::uint32_t>(byte(3)) << 24);
        at = 4;

        std::vector<Row> rows;
        rows.reserve(count);
        for (std::uint32_t r = 0; r < count; ++r)
        {
            Row row;
            row.laneCount = byte(at + 0);
            row.dotLane = byte(at + 1);
            row.color = byte(at + 2);
            row.flags = byte(at + 3);
            const int segCount = byte(at + 4);
            at += gl::kRowHeaderSize;

            for (int s = 0; s < segCount; ++s)
            {
                row.segments.push_back({ byte(at), byte(at + 1), byte(at + 2), byte(at + 3),
                                         byte(at + 4) });
                at += gl::kSegmentSize;
            }
            rows.push_back(row);
        }
        return rows;
    }

    std::vector<Row> Layout(const std::vector<std::vector<std::int32_t>>& adjacency)
    {
        return Decode(gl::BuildDisplayList(adjacency));
    }

    constexpr std::int32_t kOut = -1;   // a parent outside the loaded window
}

// ---- The shapes the layout has to get right ------------------------------------------------------

TEST(GraphLayout, AStraightLineIsOneLane)
{
    // 0 -> 1 -> 2, then a root.
    const std::vector<Row> rows = Layout({ { 1 }, { 2 }, { 3 }, {} });

    ASSERT_EQ(rows.size(), 4u);
    for (const Row& r : rows)
    {
        EXPECT_EQ(r.dotLane, 0);
        EXPECT_EQ(r.laneCount, 1);
    }
    // Every colour is the same: one lane, allocated once, keeps its colour for its whole life.
    EXPECT_EQ(rows[0].color, rows[3].color);

    EXPECT_TRUE(rows[0].HasSegment(0, 1, 0, 2));   // leaves the tip downward
    EXPECT_TRUE(rows[1].HasSegment(0, 0, 0, 1));   // arrives from above
    EXPECT_TRUE(rows[1].HasSegment(0, 1, 0, 2));   // and continues down
    EXPECT_EQ(rows[3].flags & gl::kFlagRoot, gl::kFlagRoot);
    EXPECT_FALSE(rows[3].HasSegment(0, 1, 0, 2));  // a root draws nothing below its dot
}

TEST(GraphLayout, ASecondTipFoldsIntoTheLaneAlreadyHeadingForTheirSharedParent)
{
    //  0  tip A -> 2
    //  1  tip B -> 2
    //  2  the shared parent
    //
    // Lane 0 is already going to row 2 when row 1 is laid out, so row 1 folds into it there and
    // then rather than holding a second lane all the way down. That is what git draws, and
    // without it a repository with a few dozen stale branch tips fans out into a wall of lines.
    const std::vector<Row> rows = Layout({ { 2 }, { 2 }, {} });

    ASSERT_EQ(rows.size(), 3u);
    EXPECT_EQ(rows[0].dotLane, 0);
    EXPECT_EQ(rows[1].dotLane, 1);
    EXPECT_EQ(rows[1].laneCount, 2);

    EXPECT_TRUE(rows[1].PassesThrough(0));         // lane 0 continues on its way to row 2
    EXPECT_TRUE(rows[1].HasSegment(1, 1, 0, 2));   // row 1 folds into it immediately
    EXPECT_FALSE(rows[1].HasSegment(1, 1, 1, 2));  // and does NOT keep a lane of its own

    // By row 2 there is only one line arriving, because the fold already happened.
    EXPECT_EQ(rows[2].dotLane, 0);
    EXPECT_TRUE(rows[2].HasSegment(0, 0, 0, 1));
    EXPECT_EQ(rows[2].laneCount, 1);
}

TEST(GraphLayout, AMergeBranchKeepsItsLaneUntilItsParentAndBothArriveThere)
{
    //  0  merge of 1 and 2
    //  1  -> 2          (the first-parent line)
    //  2  the branch point both reach
    //
    // The fold is deliberately LEFTWARD only: row 1 sits on lane 0 and the merge edge is out on
    // lane 1, so nothing folds and both lines run down to row 2. Pulling the merge lane leftward
    // onto the mainline instead would move the mainline around from row to row. git draws this
    // the same way -- two lines converging at the branch point.
    const std::vector<Row> rows = Layout({ { 1, 2 }, { 2 }, {} });

    ASSERT_EQ(rows.size(), 3u);
    EXPECT_EQ(rows[0].flags & gl::kFlagMerge, gl::kFlagMerge);
    EXPECT_TRUE(rows[0].HasSegment(0, 1, 1, 2)) << "the merge opens a second lane";
    EXPECT_TRUE(rows[1].PassesThrough(1)) << "which stays open past the first-parent row";

    int arriving = 0;
    for (const Seg& s : rows[2].segments)
    {
        if (s.y2 == 1)
            ++arriving;
    }
    EXPECT_EQ(arriving, 2) << "both edges land on the branch point";
    EXPECT_EQ(rows[2].dotLane, 0);
    EXPECT_TRUE(rows[2].HasSegment(1, 0, 0, 1)) << "the merge lane joins in diagonally";
}

TEST(GraphLayout, AMergeForksASecondLaneDownward)
{
    //  0  merge of 1 and 2
    //  1  first parent
    //  2  second parent
    const std::vector<Row> rows = Layout({ { 1, 2 }, { 3 }, { 3 }, {} });

    ASSERT_EQ(rows.size(), 4u);
    EXPECT_EQ(rows[0].flags & gl::kFlagMerge, gl::kFlagMerge);
    EXPECT_EQ(rows[0].dotLane, 0);
    EXPECT_TRUE(rows[0].HasSegment(0, 1, 0, 2));   // first parent continues on the dot's lane
    EXPECT_TRUE(rows[0].HasSegment(0, 1, 1, 2));   // second parent forks right
    EXPECT_GE(rows[0].laneCount, 2);

    EXPECT_EQ(rows[1].dotLane, 0);
    EXPECT_EQ(rows[2].dotLane, 1);
}

TEST(GraphLayout, AnOctopusMergeForksOneLanePerExtraParent)
{
    const std::vector<Row> rows = Layout({ { 1, 2, 3 }, {}, {}, {} });

    ASSERT_EQ(rows.size(), 4u);
    EXPECT_EQ(rows[0].flags & gl::kFlagMerge, gl::kFlagMerge);
    EXPECT_TRUE(rows[0].HasSegment(0, 1, 0, 2));
    EXPECT_TRUE(rows[0].HasSegment(0, 1, 1, 2));
    EXPECT_TRUE(rows[0].HasSegment(0, 1, 2, 2));
    EXPECT_GE(rows[0].laneCount, 3);

    EXPECT_EQ(rows[1].dotLane, 0);
    EXPECT_EQ(rows[2].dotLane, 1);
    EXPECT_EQ(rows[3].dotLane, 2);
}

TEST(GraphLayout, ACrissCrossKeepsBothMergesOnTheirOwnLanes)
{
    //  0  merge of 2 and 3
    //  1  merge of 3 and 2   (the crossing pair)
    //  2, 3  the two shared parents
    //
    // Row 1 is a tip -- nothing in the window points at it -- and rows 2 and 3 already have lanes
    // heading for them, so it takes a third and sends BOTH its edges across. That is the honest
    // shape of a criss-cross.
    const std::vector<Row> rows = Layout({ { 2, 3 }, { 3, 2 }, {}, {} });

    ASSERT_EQ(rows.size(), 4u);
    EXPECT_EQ(rows[0].flags & gl::kFlagMerge, gl::kFlagMerge);
    EXPECT_EQ(rows[1].flags & gl::kFlagMerge, gl::kFlagMerge);
    EXPECT_NE(rows[0].dotLane, rows[1].dotLane) << "the two merges may not share a lane";

    // Each merge sends exactly two lines out of its dot, one per parent.
    auto outgoing = [](const Row& r)
    {
        int n = 0;
        for (const Seg& s : r.segments)
        {
            if (s.y1 == 1 && s.y2 == 2)
                ++n;
        }
        return n;
    };
    EXPECT_EQ(outgoing(rows[0]), 2);
    EXPECT_EQ(outgoing(rows[1]), 2);

    // Row 1 folds both of its edges onto the lanes that already exist rather than opening more.
    for (const Seg& s : rows[1].segments)
    {
        if (s.y1 == 1 && s.y2 == 2)
            EXPECT_LT(s.x2, rows[1].dotLane) << "an edge should fold left, not open a new lane";
    }

    EXPECT_EQ(rows[2].flags & gl::kFlagRoot, gl::kFlagRoot);
    EXPECT_EQ(rows[3].flags & gl::kFlagRoot, gl::kFlagRoot);
}

TEST(GraphLayout, MultipleOrphanRootsEachGetTheirOwnLane)
{
    // Three unrelated roots, as `--all` over a repository with disjoint histories produces.
    const std::vector<Row> rows = Layout({ {}, {}, {} });

    ASSERT_EQ(rows.size(), 3u);
    for (const Row& r : rows)
    {
        EXPECT_EQ(r.flags & gl::kFlagRoot, gl::kFlagRoot);
        EXPECT_TRUE(r.segments.empty()) << "a lone root draws nothing at all";
    }
    // Each root frees its lane immediately, so they all reuse lane 0 rather than marching right.
    EXPECT_EQ(rows[0].dotLane, 0);
    EXPECT_EQ(rows[1].dotLane, 0);
    EXPECT_EQ(rows[2].dotLane, 0);
}

TEST(GraphLayout, DisjointHistoriesFromAllRunSideBySide)
{
    //  0 -> 2   and   1 -> 3, two unrelated branches walked together by --all.
    const std::vector<Row> rows = Layout({ { 2 }, { 3 }, {}, {} });

    ASSERT_EQ(rows.size(), 4u);
    EXPECT_EQ(rows[0].dotLane, 0);
    EXPECT_EQ(rows[1].dotLane, 1);
    EXPECT_TRUE(rows[1].PassesThrough(0));   // the first history keeps its lane while the second starts
    EXPECT_EQ(rows[2].dotLane, 0);
    EXPECT_EQ(rows[3].dotLane, 1);
}

TEST(GraphLayout, ALaneIsReusedAfterItsBranchEnds)
{
    //  0 -> 1 (root)      a short branch that ends
    //  2 -> 3 (root)      a later one that should reuse the freed lane, not lane 1
    const std::vector<Row> rows = Layout({ { 1 }, {}, { 3 }, {} });

    ASSERT_EQ(rows.size(), 4u);
    EXPECT_EQ(rows[1].dotLane, 0);
    EXPECT_EQ(rows[2].dotLane, 0) << "lane 0 was freed at row 1 and must be reused";
    EXPECT_EQ(rows[2].laneCount, 1);
}

TEST(GraphLayout, AParentOutsideTheWindowLeavesTheRowButLandsNowhere)
{
    // The log is capped, so an edge routinely leaves the window. The line still exits the bottom
    // of the row -- the history really does continue -- and the row is flagged as a boundary.
    const std::vector<Row> rows = Layout({ { 1 }, { kOut } });

    ASSERT_EQ(rows.size(), 2u);
    EXPECT_EQ(rows[1].flags & gl::kFlagBoundary, gl::kFlagBoundary);
    EXPECT_FALSE(rows[1].flags & gl::kFlagRoot) << "a truncated parent is not the same as no parent";
    EXPECT_TRUE(rows[1].HasSegment(0, 1, 0, 2));
}

TEST(GraphLayout, TruncationDoesNotDisturbTheRowsAroundIt)
{
    // Same graph twice, once with the last edge truncated. Everything above it must lay out
    // identically -- an edge leaving the window is not allowed to reshuffle lanes.
    const std::vector<Row> whole = Layout({ { 1 }, { 2 }, { 3 }, {} });
    const std::vector<Row> cut = Layout({ { 1 }, { 2 }, { kOut }, {} });

    ASSERT_EQ(whole.size(), cut.size());
    for (std::size_t i = 0; i < 2; ++i)
    {
        EXPECT_EQ(whole[i].dotLane, cut[i].dotLane) << "row " << i;
        EXPECT_EQ(whole[i].laneCount, cut[i].laneCount) << "row " << i;
        EXPECT_EQ(whole[i].color, cut[i].color) << "row " << i;
    }
}

TEST(GraphLayout, AMergeWithOneTruncatedParentIsStillAMerge)
{
    const std::vector<Row> rows = Layout({ { 1, kOut }, {} });

    ASSERT_EQ(rows.size(), 2u);
    EXPECT_EQ(rows[0].flags & gl::kFlagMerge, gl::kFlagMerge);
    EXPECT_EQ(rows[0].flags & gl::kFlagBoundary, gl::kFlagBoundary);
}

// ---- The display list itself ---------------------------------------------------------------------

TEST(GraphLayout, AnEmptyLogProducesOnlyTheCount)
{
    const std::string blob = gl::BuildDisplayList({});
    EXPECT_EQ(blob.size(), 4u);
    EXPECT_TRUE(Decode(blob).empty());
}

TEST(GraphLayout, EverySegmentStaysInsideTheRowsLaneCount)
{
    // What a renderer relies on: laneCount is the column width, so nothing may be drawn past it.
    const std::vector<Row> rows = Layout({ { 1, 2 }, { 3 }, { 3 }, { 4 }, {}, {} });

    for (std::size_t i = 0; i < rows.size(); ++i)
    {
        EXPECT_LT(rows[i].dotLane, rows[i].laneCount) << "row " << i;
        for (const Seg& s : rows[i].segments)
        {
            EXPECT_LT(s.x1, rows[i].laneCount) << "row " << i;
            EXPECT_LT(s.x2, rows[i].laneCount) << "row " << i;
        }
    }
}

TEST(GraphLayout, EveryCoordinateAndColourStaysInRange)
{
    const std::vector<Row> rows = Layout({ { 1, 2, 3 }, { 4 }, { 4 }, { 4 }, { 5 }, {} });

    for (const Row& r : rows)
    {
        EXPECT_LT(r.color, gl::kColorCount);
        for (const Seg& s : r.segments)
        {
            EXPECT_LE(s.y1, gl::kBottom);
            EXPECT_LE(s.y2, gl::kBottom);
            EXPECT_LT(s.color, gl::kColorCount);
        }
    }
}

TEST(GraphLayout, AdjacentLanesGetDifferentColours)
{
    // Colours cycle on allocation, so a fork never draws two neighbouring lines the same colour.
    const std::vector<Row> rows = Layout({ { 2 }, { 2 }, {} });
    ASSERT_EQ(rows.size(), 3u);
    EXPECT_NE(rows[0].color, rows[1].color);
}

// ---- The graph riding along with the log records ------------------------------------------------

TEST(LogGraph, TheDisplayListLandsInTheExtraSectionAndParentsResolveToRows)
{
    // Three commits: a merge of the next two, which then share nothing. Parent hashes have to be
    // resolved to ROW positions here -- that mapping only exists while the records are built.
    const std::string f(1, '\x1f');
    auto record = [&](const std::string& hash, const std::string& parents)
    {
        return hash + f + hash.substr(0, 7) + f + parents + f + "A" + f + "a@a" + f + "0" + f +
               "+0000" + f + "A" + f + "a@a" + f + "0" + f + "+0000" + f + "" + f + "subject";
    };
    std::string stream = record("aaa", "bbb ccc");
    stream.push_back('\0');
    stream += record("bbb", "");
    stream.push_back('\0');
    stream += record("ccc", "");

    const mstest::PackedRead p(ms::parse::ParseLogRecordsWithGraph(stream));
    ASSERT_EQ(p.Count(), 3u);

    const std::string extra = p.Extra();
    ASSERT_FALSE(extra.empty()) << "the display list rides in the extra section";

    const std::vector<Row> rows = Decode(extra);
    ASSERT_EQ(rows.size(), 3u);
    EXPECT_EQ(rows[0].flags & gl::kFlagMerge, gl::kFlagMerge) << "aaa has two parents";
    EXPECT_EQ(rows[1].flags & gl::kFlagRoot, gl::kFlagRoot);
    EXPECT_EQ(rows[2].flags & gl::kFlagRoot, gl::kFlagRoot);
    EXPECT_TRUE(rows[0].HasSegment(0, 1, 0, 2)) << "first parent continues on the dot lane";
    EXPECT_TRUE(rows[0].HasSegment(0, 1, 1, 2)) << "second parent forks";
}

TEST(LogGraph, AParentOutsideTheLoadedWindowIsAMissNotAnError)
{
    // The log is capped, so an edge routinely names a commit that is not in the window.
    const std::string f(1, '\x1f');
    const std::string record =
        std::string("aaa") + f + "aaa" + f + "zzz" + f + "A" + f + "a@a" + f + "0" + f +
        "+0000" + f + "A" + f + "a@a" + f + "0" + f + "+0000" + f + "" + f + "subject";

    const mstest::PackedRead p(ms::parse::ParseLogRecordsWithGraph(record));
    ASSERT_EQ(p.Count(), 1u);

    const std::vector<Row> rows = Decode(p.Extra());
    ASSERT_EQ(rows.size(), 1u);
    EXPECT_EQ(rows[0].flags & gl::kFlagBoundary, gl::kFlagBoundary);
    EXPECT_FALSE(rows[0].flags & gl::kFlagRoot);
}

TEST(LogGraph, PlainParseLogRecordsCarriesNoGraph)
{
    const std::string f(1, '\x1f');
    const std::string record =
        std::string("aaa") + f + "aaa" + f + "" + f + "A" + f + "a@a" + f + "0" + f +
        "+0000" + f + "A" + f + "a@a" + f + "0" + f + "+0000" + f + "" + f + "subject";

    const mstest::PackedRead p(ms::parse::ParseLogRecords(record));
    ASSERT_EQ(p.Count(), 1u);
    EXPECT_TRUE(p.Extra().empty());
}
