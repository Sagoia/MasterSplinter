#include "pch.h"

#include <string>
#include <vector>

#include "PackedRead.h"
#include "Parse/StatusParser.h"

// Changed-file parsing: the name-status half of RecordParserTests.cs and the IsUnmerged tests
// from GitContractTests.cs, both migrated from the host in Phase D.

using mstest::PackedRead;
namespace pr = ms::parse;

namespace
{
    // git -z output: a NUL after every token, including the last.
    std::string Nul(const std::vector<std::string>& tokens)
    {
        std::string out;
        for (const std::string& t : tokens)
        {
            out += t;
            out.push_back('\0');
        }
        return out;
    }

    PackedRead NameStatus(const std::vector<std::string>& tokens)
    {
        return PackedRead(pr::ParseNameStatus(Nul(tokens)));
    }

    PackedRead Porcelain(const std::vector<std::string>& tokens)
    {
        return PackedRead(pr::ParsePorcelainStatus(Nul(tokens)));
    }

    pr::FileChangeStatus StatusOf(const PackedRead& p, std::uint32_t row)
    {
        return static_cast<pr::FileChangeStatus>(p.RecU8(row, pr::kFileOffStatus));
    }

    pr::StatusSection SectionOf(const PackedRead& p, std::uint32_t row)
    {
        return static_cast<pr::StatusSection>(p.RecU8(row, pr::kFileOffSection));
    }

    std::string PathOf(const PackedRead& p, std::uint32_t row)
    {
        return p.RecStr(row, pr::kFileOffPath);
    }
}

// ---- name-status --------------------------------------------------------------------------------

TEST(NameStatus, CodesMapToStatuses)
{
    const PackedRead p = NameStatus({ "A", "added.txt", "M", "modified.txt", "D", "gone.txt" });

    EXPECT_EQ(p.Kind(), ms::packed::Kind::NameStatus);
    ASSERT_EQ(p.Count(), 3u);
    EXPECT_EQ(StatusOf(p, 0), pr::FileChangeStatus::Added);
    EXPECT_EQ(StatusOf(p, 1), pr::FileChangeStatus::Modified);
    EXPECT_EQ(StatusOf(p, 2), pr::FileChangeStatus::Deleted);
}

TEST(NameStatus, RenameTargetsTheNewPathSoDiffsResolve)
{
    // "R100" then old then new -- diff and show must address the NEW path.
    const PackedRead p = NameStatus({ "R100", "old.txt", "new.txt" });

    ASSERT_EQ(p.Count(), 1u);
    EXPECT_EQ(StatusOf(p, 0), pr::FileChangeStatus::Renamed);
    EXPECT_EQ(PathOf(p, 0), "new.txt");
    EXPECT_EQ(p.RecStr(0, pr::kFileOffOldPath), "old.txt");
}

TEST(NameStatus, CopyIsTreatedLikeARename)
{
    const PackedRead p = NameStatus({ "C75", "src.txt", "copy.txt" });

    ASSERT_EQ(p.Count(), 1u);
    EXPECT_EQ(StatusOf(p, 0), pr::FileChangeStatus::Renamed);
    EXPECT_EQ(PathOf(p, 0), "copy.txt");
}

TEST(NameStatus, PathsWithControlCharactersSurviveBecauseOfMinusZ)
{
    // Regression, found by comparing against TortoiseGit (which uses -z everywhere for this
    // reason): the line-based format C-quotes such a path, so the parser used to hand the rest of
    // the app a quoted, backslash-escaped name -- and every later diff or stage then addressed a
    // file that does not exist. core.quotePath=false does NOT prevent it.
    const PackedRead p = NameStatus({ "A", "a\nb.txt" });

    ASSERT_EQ(p.Count(), 1u);
    EXPECT_EQ(PathOf(p, 0), "a\nb.txt");
}

TEST(NameStatus, APathContainingARecordSeparatorIsJustAPath)
{
    // The old pipeline translated NUL to 0x1E for the host, so a path holding 0x1E split the list
    // in two. Reading the NUL stream directly removes the last place that could happen.
    std::string path = "a";
    path += static_cast<char>(0x1e);
    path += "b.txt";
    const PackedRead p = NameStatus({ "M", path });

    ASSERT_EQ(p.Count(), 1u);
    EXPECT_EQ(PathOf(p, 0), path);
}

TEST(NameStatus, ATruncatedTailIsSkipped)
{
    EXPECT_EQ(NameStatus({ "A" }).Count(), 0u);
    EXPECT_EQ(NameStatus({ "R100", "only-one" }).Count(), 0u);
    EXPECT_EQ(PackedRead(pr::ParseNameStatus("")).Count(), 0u);
}

TEST(NameStatus, EntriesAreNotMarkedAsWorkingTree)
{
    // What tells the host to diff against a commit rather than against the worktree/index.
    EXPECT_EQ(NameStatus({ "M", "a.txt" }).RecU8(0, pr::kFileOffIsWorkingTree), 0);
}

// ---- porcelain status ----------------------------------------------------------------------------

TEST(PorcelainStatus, EntriesLandInTheirSections)
{
    const PackedRead p = Porcelain({ "M  staged.txt", " M unstaged.txt", "?? new.txt" });

    EXPECT_EQ(p.Kind(), ms::packed::Kind::Status);
    ASSERT_EQ(p.Count(), 3u);
    EXPECT_EQ(SectionOf(p, 0), pr::StatusSection::Staged);
    EXPECT_EQ(SectionOf(p, 1), pr::StatusSection::Unstaged);
    EXPECT_EQ(SectionOf(p, 2), pr::StatusSection::Untracked);
    EXPECT_EQ(StatusOf(p, 2), pr::FileChangeStatus::Untracked);
}

TEST(PorcelainStatus, AFileStagedAndModifiedAgainAppearsInBothSections)
{
    const PackedRead p = Porcelain({ "MM a.txt" });

    ASSERT_EQ(p.Count(), 2u);
    EXPECT_EQ(SectionOf(p, 0), pr::StatusSection::Staged);
    EXPECT_EQ(SectionOf(p, 1), pr::StatusSection::Unstaged);
    EXPECT_EQ(PathOf(p, 0), "a.txt");
    EXPECT_EQ(PathOf(p, 1), "a.txt");
}

TEST(PorcelainStatus, AllSevenUnmergedPairsAreListedOnceAsConflicted)
{
    // The check has to come BEFORE the staged/unstaged split: both halves see a non-blank column
    // here, so a plain "UU" would otherwise be reported as a staged modification AND an unstaged
    // one -- the same conflicted file listed twice, with no hint that anything is wrong.
    const char* pairs[] = { "DD", "AU", "UD", "UA", "DU", "AA", "UU" };
    for (const char* xy : pairs)
    {
        const PackedRead p = Porcelain({ std::string(xy) + " a.txt" });
        ASSERT_EQ(p.Count(), 1u) << xy;
        EXPECT_EQ(SectionOf(p, 0), pr::StatusSection::Conflicted) << xy;
        EXPECT_EQ(StatusOf(p, 0), pr::FileChangeStatus::Conflicted) << xy;
    }
}

TEST(PorcelainStatus, OrdinaryPairsAreNeverConflicted)
{
    const char* pairs[] = { "M ", " M", "A ", " D", "MM", "R ", "??" };
    for (const char* xy : pairs)
    {
        const PackedRead p = Porcelain({ std::string(xy) + " a.txt" });
        for (std::uint32_t i = 0; i < p.Count(); ++i)
            EXPECT_NE(SectionOf(p, i), pr::StatusSection::Conflicted) << xy;
    }
}

TEST(PorcelainStatus, IsUnmergedMatchesGitsOwnSevenPairs)
{
    EXPECT_TRUE(pr::IsUnmerged('D', 'D'));
    EXPECT_TRUE(pr::IsUnmerged('A', 'U'));
    EXPECT_TRUE(pr::IsUnmerged('U', 'D'));
    EXPECT_TRUE(pr::IsUnmerged('U', 'A'));
    EXPECT_TRUE(pr::IsUnmerged('D', 'U'));
    EXPECT_TRUE(pr::IsUnmerged('A', 'A'));
    EXPECT_TRUE(pr::IsUnmerged('U', 'U'));

    EXPECT_FALSE(pr::IsUnmerged('M', ' '));
    EXPECT_FALSE(pr::IsUnmerged(' ', 'M'));
    EXPECT_FALSE(pr::IsUnmerged('A', ' '));
    EXPECT_FALSE(pr::IsUnmerged(' ', 'D'));
    EXPECT_FALSE(pr::IsUnmerged('M', 'M'));
    EXPECT_FALSE(pr::IsUnmerged('R', ' '));
    EXPECT_FALSE(pr::IsUnmerged('?', '?'));
    EXPECT_FALSE(pr::IsUnmerged('D', 'A'));   // not on the list, despite looking plausible
}

TEST(PorcelainStatus, ARenameCarriesBothPathsWithTheNewOneFirst)
{
    const PackedRead p = Porcelain({ "R  new.txt", "old.txt" });

    ASSERT_EQ(p.Count(), 1u);
    EXPECT_EQ(PathOf(p, 0), "new.txt");
    EXPECT_EQ(p.RecStr(0, pr::kFileOffOldPath), "old.txt");
    EXPECT_EQ(SectionOf(p, 0), pr::StatusSection::Staged);
}

TEST(PorcelainStatus, MalformedRecordsAreSkipped)
{
    EXPECT_EQ(Porcelain({ "xx" }).Count(), 0u);          // too short
    EXPECT_EQ(Porcelain({ "MXa.txt" }).Count(), 0u);     // no space in column 3
    EXPECT_EQ(PackedRead(pr::ParsePorcelainStatus("")).Count(), 0u);
}

TEST(PorcelainStatus, EveryEntryIsMarkedAsWorkingTree)
{
    // What tells the host to diff against the worktree/index rather than against a commit.
    const PackedRead p = Porcelain({ "M  a.txt" });
    ASSERT_EQ(p.Count(), 1u);
    EXPECT_EQ(p.RecU8(0, pr::kFileOffIsWorkingTree), 1);
}

TEST(PorcelainStatus, TheSectionNumberingIsNotTheAreaParameterNumbering)
{
    // A GUARD, not a behaviour test. Two different orderings share the word "area" in this ABI:
    // StatusSection matches the host WorkTreeArea enum (staged first), while the `area` PARAMETER
    // of MsGitWorkTreeFileDiff is 0=unstaged, 1=staged, 2=untracked. Casting one to the other
    // swaps staged and unstaged, which shipped once and was caught only in UI verification.
    EXPECT_EQ(static_cast<int>(pr::StatusSection::Staged), 0);
    EXPECT_EQ(static_cast<int>(pr::StatusSection::Unstaged), 1);
    EXPECT_EQ(static_cast<int>(pr::StatusSection::Untracked), 2);
    EXPECT_EQ(static_cast<int>(pr::StatusSection::Conflicted), 3);
}
