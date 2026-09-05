#include "pch.h"

#include <string>

#include "PackedRead.h"
#include "Parse/BlameParser.h"
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
// The combined three-at cases are here because that form shipped broken once: the ordinary hunk
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
    // Two parents => a three-at header and two marker columns per body line. "++" is added on both sides,
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

// ---- Porcelain blame (was PorcelainBlameParserTests.cs) ----------------------------------------
//
// The sample is real `git blame --porcelain` output, kept verbatim because its whole point is the
// third group: it repeats the FIRST group's sha and carries no header lines at all. git emits the
// author/summary block only on a commit's first group, so without per-sha caching most lines
// would render with a blank author -- a correctness requirement, not an optimisation.

namespace
{
    const char* kShaA = "7f3f6795392cb18d48d799d944894d635a5cd95f";
    const char* kShaB = "fefe296b524832304e6ab15c0d21dfba5bb93b86";

    std::string BlameSample()
    {
        return std::string(kShaA) + " 1 1 1\n"
            "author Alice\n"
            "author-mail <a@a>\n"
            "author-time 1787394695\n"
            "author-tz +0700\n"
            "committer Alice\n"
            "summary first commit\n"
            "boundary\n"
            "filename f.txt\n"
            "\tone\n"
            + kShaB + " 2 2 1\n"
            "author Bob\n"
            "author-mail <b@b>\n"
            "author-time 1787394695\n"
            "author-tz +0700\n"
            "committer Bob\n"
            "summary second commit\n"
            "previous " + kShaA + " f.txt\n"
            "filename f.txt\n"
            "\tINSERTED\n"
            + kShaA + " 2 3 2\n"
            "\ttwo\n"
            + kShaA + " 3 4\n"
            "\tthree\n";
    }

    PackedRead Blame(std::string_view porcelain, std::string_view path = "f.txt")
    {
        return PackedRead(pr::ParsePorcelainBlame(porcelain, path));
    }
}

TEST(PorcelainBlame, EveryContentLineBecomesOneRecord)
{
    const PackedRead p = Blame(BlameSample());

    EXPECT_EQ(p.Kind(), ms::packed::Kind::Blame);
    ASSERT_EQ(p.Count(), 4u);
    EXPECT_EQ(p.RecStr(0, pr::kBlameOffText), "one");
    EXPECT_EQ(p.RecStr(1, pr::kBlameOffText), "INSERTED");
    EXPECT_EQ(p.RecStr(2, pr::kBlameOffText), "two");
    EXPECT_EQ(p.RecStr(3, pr::kBlameOffText), "three");
}

TEST(PorcelainBlame, HeadersAreCachedAndReusedForLaterGroupsOfTheSameCommit)
{
    const PackedRead p = Blame(BlameSample());

    // Records 2 and 3 belong to ShaA, whose header block appeared only on the FIRST group.
    EXPECT_EQ(p.RecStr(2, pr::kBlameOffSha), kShaA);
    EXPECT_EQ(p.RecStr(2, pr::kBlameOffAuthor), "Alice");
    EXPECT_EQ(p.RecStr(2, pr::kBlameOffEmail), "a@a");
    EXPECT_EQ(p.RecStr(2, pr::kBlameOffSummary), "first commit");

    EXPECT_EQ(p.RecStr(3, pr::kBlameOffSha), kShaA);
    EXPECT_EQ(p.RecStr(3, pr::kBlameOffAuthor), "Alice");
    EXPECT_EQ(p.RecStr(3, pr::kBlameOffSummary), "first commit");
}

TEST(PorcelainBlame, EachCommitKeepsItsOwnAuthor)
{
    const PackedRead p = Blame(BlameSample());
    EXPECT_EQ(p.RecStr(0, pr::kBlameOffAuthor), "Alice");
    EXPECT_EQ(p.RecStr(1, pr::kBlameOffAuthor), "Bob");
    EXPECT_EQ(p.RecStr(1, pr::kBlameOffSummary), "second commit");
}

TEST(PorcelainBlame, FinalAndOriginalLineNumbersAreTracked)
{
    const PackedRead p = Blame(BlameSample());

    EXPECT_EQ(p.RecI32(0, pr::kBlameOffFinalLine), 1);
    EXPECT_EQ(p.RecI32(1, pr::kBlameOffFinalLine), 2);
    EXPECT_EQ(p.RecI32(2, pr::kBlameOffFinalLine), 3);
    EXPECT_EQ(p.RecI32(3, pr::kBlameOffFinalLine), 4);
    // "two" moved from original line 2 to final line 3 when INSERTED was added above it.
    EXPECT_EQ(p.RecI32(2, pr::kBlameOffOrigLine), 2);
}

TEST(PorcelainBlame, AuthorTimeAndTimezoneTravelAsSecondsAndMinutes)
{
    const PackedRead p = Blame(BlameSample());
    EXPECT_EQ(p.RecI64(0, pr::kBlameOffAuthorTime), 1787394695LL);
    EXPECT_EQ(p.RecI32(0, pr::kBlameOffAuthorTz), 7 * 60);
}

TEST(PorcelainBlame, GroupStartsAreFlagged)
{
    const PackedRead p = Blame(BlameSample());

    // Every line starts a new group except the last, which continues ShaA's second group.
    EXPECT_EQ(p.RecU8(0, pr::kBlameOffIsGroupStart), 1);
    EXPECT_EQ(p.RecU8(1, pr::kBlameOffIsGroupStart), 1);
    EXPECT_EQ(p.RecU8(2, pr::kBlameOffIsGroupStart), 1);
    EXPECT_EQ(p.RecU8(3, pr::kBlameOffIsGroupStart), 0);
}

TEST(PorcelainBlame, TheFilenameHeaderPicksTheSourcePath)
{
    const PackedRead p = Blame(BlameSample());
    EXPECT_EQ(p.RecStr(0, pr::kBlameOffSourcePath), "f.txt");
}

TEST(PorcelainBlame, WithoutAFilenameHeaderTheBlamedPathIsUsed)
{
    // Without -M/-C every line came from the blamed file, and the source path is what picks the
    // host's syntax highlighter -- so a missing header must not leave it empty.
    const PackedRead p = Blame(std::string(kShaA) + " 1 1 1\nauthor Alice\n\tx\n", "src/main.cpp");

    ASSERT_EQ(p.Count(), 1u);
    EXPECT_EQ(p.RecStr(0, pr::kBlameOffSourcePath), "src/main.cpp");
}

TEST(PorcelainBlame, AMalformedGroupHeaderIsSkippedRatherThanDerailingTheFile)
{
    const PackedRead p = Blame(std::string("garbage\n") + kShaA + " 1 1 1\nauthor Alice\n\tx\n");

    ASSERT_EQ(p.Count(), 1u);
    EXPECT_EQ(p.RecStr(0, pr::kBlameOffText), "x");
}

TEST(PorcelainBlame, AnUnparseableTimezoneMeansUtcRatherThanLosingTheLine)
{
    const PackedRead p = Blame(std::string(kShaA) +
        " 1 1 1\nauthor Alice\nauthor-time 100\nauthor-tz nonsense\n\tx\n");

    ASSERT_EQ(p.Count(), 1u);
    EXPECT_EQ(p.RecI64(0, pr::kBlameOffAuthorTime), 100LL);
    EXPECT_EQ(p.RecI32(0, pr::kBlameOffAuthorTz), 0);
}

TEST(PorcelainBlame, NegativeTimezonesAreSigned)
{
    EXPECT_EQ(pr::ParseTimezoneMinutes("-0730"), -(7 * 60 + 30));
    EXPECT_EQ(pr::ParseTimezoneMinutes("+0200"), 2 * 60);
    EXPECT_EQ(pr::ParseTimezoneMinutes(""), 0);
    EXPECT_EQ(pr::ParseTimezoneMinutes("+07"), 0);
    EXPECT_EQ(pr::ParseTimezoneMinutes("x0700"), 0);
}

TEST(PorcelainBlame, ContentCarryingSeparatorBytesSurvivesIntact)
{
    // Blame content lines are raw file bytes. Under the delimited format a 0x1F here shifted the
    // whole payload; length prefixes make it just another byte.
    std::string content = "a";
    content += char(0x1f);
    content += "b";
    content += char(0x1e);
    content += "c";
    const PackedRead p = Blame(std::string(kShaA) + " 1 1 1\nauthor Alice\n\t" + content + "\n");

    ASSERT_EQ(p.Count(), 1u);
    EXPECT_EQ(p.RecStr(0, pr::kBlameOffText), content);
}

TEST(PorcelainBlame, EmptyInputProducesNoRecords)
{
    const PackedRead p = Blame("");
    EXPECT_EQ(p.Count(), 0u);
    EXPECT_FALSE(p.IsError());
}
