#include "pch.h"

#include <string>

#include "PackedRead.h"
#include "Parse/DiffParser.h"

// Parsers migrated from the host during Phase D. Each suite here replaces an xunit file that was
// deleted in the same commit, so the coverage moved rather than shrank.

using mstest::PackedRead;
namespace pr = ms::parse;

namespace
{
    PackedRead Diff(std::string_view raw) { return PackedRead(pr::ParseUnifiedDiff(raw)); }

    pr::DiffLineKind KindOf(const PackedRead& p, std::uint32_t row)
    {
        return static_cast<pr::DiffLineKind>(p.RecU8(row, pr::kDiffOffKind));
    }

    std::string TextOf(const PackedRead& p, std::uint32_t row)
    {
        return p.RecStr(row, pr::kDiffOffText);
    }

    std::int32_t OldOf(const PackedRead& p, std::uint32_t row)
    {
        return p.RecI32(row, pr::kDiffOffOldNo);
    }

    std::int32_t NewOf(const PackedRead& p, std::uint32_t row)
    {
        return p.RecI32(row, pr::kDiffOffNewNo);
    }

    bool IsBinary(const PackedRead& p) { return (p.Flags() & pr::kDiffFlagBinary) != 0; }

    // -1 is how "this side has no number" travels; the host renders it as an empty gutter.
    constexpr std::int32_t kNone = -1;
}

// ---- Unified diff (was UnifiedDiffParserTests.cs) -----------------------------------------------
//
// The combined "@@@" cases are here because that form shipped broken once: the ordinary hunk
// regex matches none of it, so a conflicted file's diff pane rendered completely empty.

TEST(UnifiedDiff, OrdinaryHunkNumbersBothGutters)
{
    const PackedRead p = Diff("@@ -1,3 +1,4 @@\n ctx\n-gone\n+added\n+more\n");

    EXPECT_EQ(p.Kind(), ms::packed::Kind::Diff);
    EXPECT_FALSE(IsBinary(p));
    ASSERT_EQ(p.Count(), 5u);

    EXPECT_EQ(KindOf(p, 0), pr::DiffLineKind::Hunk);

    EXPECT_EQ(KindOf(p, 1), pr::DiffLineKind::Context);
    EXPECT_EQ(OldOf(p, 1), 1);
    EXPECT_EQ(NewOf(p, 1), 1);

    EXPECT_EQ(KindOf(p, 2), pr::DiffLineKind::Removed);
    EXPECT_EQ(OldOf(p, 2), 2);
    EXPECT_EQ(NewOf(p, 2), kNone);   // a removed line has no new-side number

    EXPECT_EQ(KindOf(p, 3), pr::DiffLineKind::Added);
    EXPECT_EQ(OldOf(p, 3), kNone);
    EXPECT_EQ(NewOf(p, 3), 2);
    EXPECT_EQ(KindOf(p, 4), pr::DiffLineKind::Added);
    EXPECT_EQ(NewOf(p, 4), 3);
}

TEST(UnifiedDiff, HunkHeaderWithoutCountsIsAccepted)
{
    // git omits ",<len>" when the range is a single line.
    const PackedRead p = Diff("@@ -1 +1 @@\n-a\n+b\n");

    EXPECT_EQ(KindOf(p, 1), pr::DiffLineKind::Removed);
    EXPECT_EQ(OldOf(p, 1), 1);
    EXPECT_EQ(KindOf(p, 2), pr::DiffLineKind::Added);
    EXPECT_EQ(NewOf(p, 2), 1);
}

TEST(UnifiedDiff, TheHunkHeaderKeepsItsOwnTextAndHasNoGutterNumbers)
{
    const PackedRead p = Diff("@@ -1,3 +1,4 @@ void f()\n ctx\n");

    EXPECT_EQ(TextOf(p, 0), "@@ -1,3 +1,4 @@ void f()");
    EXPECT_EQ(OldOf(p, 0), kNone);
    EXPECT_EQ(NewOf(p, 0), kNone);
}

TEST(UnifiedDiff, CombinedDiffMarkerColumnsAreDecoded)
{
    // Two parents => "@@@" and two marker columns per body line. "++" is added on both sides,
    // " +" / "+ " added on one, and the text starts after the marker columns.
    const PackedRead p = Diff(
        "@@@ -1,3 -1,3 +1,7 @@@\n"
        "++<<<<<<< HEAD\n"
        " +MAIN\n"
        "+ FEATURE\n"
        "  common\n");

    EXPECT_EQ(KindOf(p, 0), pr::DiffLineKind::Hunk);
    EXPECT_EQ(KindOf(p, 1), pr::DiffLineKind::Added);
    EXPECT_EQ(TextOf(p, 1), "<<<<<<< HEAD");
    EXPECT_EQ(KindOf(p, 2), pr::DiffLineKind::Added);
    EXPECT_EQ(TextOf(p, 2), "MAIN");
    EXPECT_EQ(KindOf(p, 3), pr::DiffLineKind::Added);
    EXPECT_EQ(TextOf(p, 3), "FEATURE");
    EXPECT_EQ(KindOf(p, 4), pr::DiffLineKind::Context);
    EXPECT_EQ(TextOf(p, 4), "common");
}

TEST(UnifiedDiff, CombinedDiffRemovalInAnyColumnCountsAsRemoved)
{
    const PackedRead p = Diff("@@@ -1,2 -1,2 +1,2 @@@\n- gone\n");

    EXPECT_EQ(KindOf(p, 1), pr::DiffLineKind::Removed);
    EXPECT_EQ(TextOf(p, 1), "gone");
}

TEST(UnifiedDiff, ThreeParentCombinedDiffUsesThreeMarkerColumns)
{
    const PackedRead p = Diff("@@@@ -1,2 -1,2 -1,2 +1,2 @@@@\n+++octopus\n");

    EXPECT_EQ(KindOf(p, 1), pr::DiffLineKind::Added);
    EXPECT_EQ(TextOf(p, 1), "octopus");
}

TEST(UnifiedDiff, ACombinedHunkTakesItsOldNumberFromTheFirstParentRange)
{
    // The first "-" range is the base for the left gutter; git prints them in parent order.
    const PackedRead p = Diff("@@@ -7,2 -99,2 +12,2 @@@\n  ctx\n");

    EXPECT_EQ(OldOf(p, 1), 7);
    EXPECT_EQ(NewOf(p, 1), 12);
}

TEST(UnifiedDiff, BinaryMarkersAreDetected)
{
    EXPECT_TRUE(IsBinary(Diff("Binary files a/x.png and b/x.png differ\n")));
    EXPECT_TRUE(IsBinary(Diff("GIT binary patch\ndelta 12\nzzzz\n")));
}

TEST(UnifiedDiff, TextDiffIsNotBinary)
{
    EXPECT_FALSE(IsBinary(Diff("@@ -1 +1 @@\n-a\n+b\n")));
}

TEST(UnifiedDiff, CarriageReturnsAreTrimmedFromLineEnds)
{
    const PackedRead p = Diff("@@ -1 +1 @@\r\n+added\r\n");
    EXPECT_EQ(TextOf(p, 1), "added");
}

TEST(UnifiedDiff, PreambleBeforeTheFirstHunkIsIgnored)
{
    // "diff --git / index / --- / +++" lines must not be mistaken for content -- note that
    // "--- a/f" and "+++ b/f" would otherwise read as a removal and an addition.
    const PackedRead p = Diff(
        "diff --git a/f b/f\nindex 111..222 100644\n--- a/f\n+++ b/f\n@@ -1 +1 @@\n+x\n");

    ASSERT_EQ(p.Count(), 2u);
    EXPECT_EQ(KindOf(p, 0), pr::DiffLineKind::Hunk);
    EXPECT_EQ(TextOf(p, 1), "x");
}

TEST(UnifiedDiff, TheTrailingNewlineDoesNotProduceAPhantomBlankLine)
{
    // Splitting on the patch's final newline leaves an empty tail. Treating it as content added a
    // blank row, carrying a line number, to the end of EVERY diff. git always writes a marker
    // column, so a genuinely blank context line arrives as " " and is never zero-length here.
    const PackedRead p = Diff("@@ -1,2 +1,2 @@\n ctx\n");
    EXPECT_EQ(p.Count(), 2u);
}

TEST(UnifiedDiff, ABlankContextLineIsKeptAsAnEmptyString)
{
    // " " is a blank line that really is in the file: one marker column, no text.
    const PackedRead p = Diff("@@ -1,2 +1,2 @@\n \n a\n");

    ASSERT_EQ(p.Count(), 3u);
    EXPECT_EQ(KindOf(p, 1), pr::DiffLineKind::Context);
    EXPECT_EQ(TextOf(p, 1), "");
    EXPECT_EQ(OldOf(p, 1), 1);
    EXPECT_EQ(TextOf(p, 2), "a");
}

TEST(UnifiedDiff, TheNoNewlineMarkerIsSkipped)
{
    const PackedRead p = Diff("@@ -1 +1 @@\n-a\n\\ No newline at end of file\n+b\n");

    ASSERT_EQ(p.Count(), 3u);
    EXPECT_EQ(KindOf(p, 1), pr::DiffLineKind::Removed);
    EXPECT_EQ(KindOf(p, 2), pr::DiffLineKind::Added);
}

TEST(UnifiedDiff, EmptyInputYieldsNothing)
{
    const PackedRead p = Diff("");
    EXPECT_EQ(p.Count(), 0u);
    EXPECT_FALSE(IsBinary(p));
    EXPECT_FALSE(p.IsError());
}

TEST(UnifiedDiff, LineTextCarryingSeparatorBytesSurvivesIntact)
{
    // The delimited format could not express this: a 0x1F in a diff line shifted every later
    // field. Length prefixes make it just another byte. A source file really can contain one.
    std::string line = "+const char kUs = ";
    line += '\x1f';
    line += "; // and ";
    line += '\x1e';
    const PackedRead p = Diff("@@ -1 +1 @@\n" + line + "\n");

    ASSERT_EQ(p.Count(), 2u);
    EXPECT_EQ(TextOf(p, 1), line.substr(1));
}
