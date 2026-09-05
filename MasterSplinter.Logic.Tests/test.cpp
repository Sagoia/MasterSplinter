#include "pch.h"

#include <chrono>
#include <filesystem>
#include <fstream>
#include <memory>
#include <string>
#include <vector>

#include "Git/GitBackend.h"
#include "FakeProcessRunner.h"

// Unit tests for the portable git command builder (the Bridge abstraction). Every test injects a
// FakeProcessRunner into GitBackend, so nothing here spawns git.exe or touches a repository — the
// tests assert (a) the exact git command GitBackend builds and (b) how it interprets the runner's
// (scripted) output. Fully deterministic; see cpp-cross-platform-patterns for the design.

namespace
{
    using Args = std::vector<std::string>;

    // 0x1F unit separator (the field separator GitBackend uses in OpenRepository's OK/ERR reply).
    const std::string US = std::string(1, '\x1f');

    // 0x1E record separator (used by the for-each-ref payloads below).
    const std::string RS = std::string(1, '\x1e');

    // Must match the pretty-format string in GitBackend::Log exactly.
    const std::string FMT =
        "--pretty=format:%H%x1f%h%x1f%P%x1f%an%x1f%ae%x1f%aI%x1f%cn%x1f%ce%x1f%cI%x1f%D%x1f%s%x1f%b%x1e";

    // Must match the format string in GitBackend::RefDetails exactly. Pinned here because
    // for-each-ref uses "%xx" hex escapes, not log --pretty's "%xNN" — writing %x1f would emit
    // the literal text "%x1f" and silently empty the sidebar.
    const std::string REF_FMT =
        "--format=%(refname)%1f%(objectname)%1f%(*objectname)%1f%(objecttype)%1f"
        "%(upstream:short)%1f%(upstream:track,nobracket)%1f%(HEAD)%1f%(symref)%1e";

    // Phase 8. `stash list` and `reflog show` are both log-family walks, so these use log --pretty's
    // "%xNN" escapes — NOT for-each-ref's "%xx" (see REF_FMT above).
    const std::string STASH_FMT = "--format=%gd%x1f%H%x1f%h%x1f%gs%x1f%aI%x1f%an%x1e";
    const std::string REFLOG_FMT = "--format=%gd%x1f%H%x1f%h%x1f%gs%x1f%aI%x1f%an%x1f%s%x1e";

    // A GitBackend wired to a FakeProcessRunner we retain a (non-owning) pointer to, so a test can
    // script responses and then inspect the recorded calls.
    struct Harness
    {
        mstest::FakeProcessRunner* fake = nullptr;
        std::unique_ptr<ms::GitBackend> backend;
    };

    Harness MakeHarness()
    {
        auto fake = std::make_unique<mstest::FakeProcessRunner>();
        Harness h;
        h.fake = fake.get();
        h.backend = std::make_unique<ms::GitBackend>(std::move(fake));
        return h;
    }

    // True if any arg in call `i` starts with `prefix` (used to assert -n<N> presence/absence).
    bool AnyArgStartsWith(const mstest::FakeProcessRunner& f, size_t i, const std::string& prefix)
    {
        for (const auto& a : f.ArgsOf(i))
            if (a.rfind(prefix, 0) == 0)
                return true;
        return false;
    }
}

// ---- RunGitC seam ------------------------------------------------------------------------------

TEST(RunGitC, PrependsDashCRootAndRunsGit)
{
    auto h = MakeHarness();
    h.fake->SetResponse("true", 0);
    h.backend->IsRepository("C:/repo");

    ASSERT_EQ(h.fake->CallCount(), 1u);
    EXPECT_EQ(h.fake->calls[0].executable, "git");
    // First two args are always -C <root>.
    EXPECT_EQ(h.fake->calls[0].args[0], "-C");
    EXPECT_EQ(h.fake->calls[0].args[1], "C:/repo");
}

// ---- IsRepository ------------------------------------------------------------------------------

TEST(IsRepository, TrueWhenGitPrintsTrue)
{
    auto h = MakeHarness();
    h.fake->SetResponse("true\n", 0);
    EXPECT_TRUE(h.backend->IsRepository("root"));
    EXPECT_EQ(h.fake->ArgsOf(0), (Args{ "-C", "root", "rev-parse", "--is-inside-work-tree" }));
}

TEST(IsRepository, FalseWhenOutputIsNotTrue)
{
    auto h = MakeHarness();
    h.fake->SetResponse("false\n", 0);
    EXPECT_FALSE(h.backend->IsRepository("root"));
}

TEST(IsRepository, FalseWhenExitNonZero)
{
    auto h = MakeHarness();
    h.fake->SetResponse("true", 128);
    EXPECT_FALSE(h.backend->IsRepository("root"));
}

TEST(IsRepository, EmptyPathReturnsFalseWithoutCallingGit)
{
    auto h = MakeHarness();
    EXPECT_FALSE(h.backend->IsRepository(""));
    EXPECT_EQ(h.fake->CallCount(), 0u);
}

// ---- OpenRepository ----------------------------------------------------------------------------

TEST(OpenRepository, EmptyPathReturnsErrWithoutCallingGit)
{
    auto h = MakeHarness();
    EXPECT_EQ(h.backend->OpenRepository(""), "ERR" + US + "No folder was provided");
    EXPECT_EQ(h.fake->CallCount(), 0u);
}

TEST(OpenRepository, SuccessReturnsOkTopAndBranch)
{
    auto h = MakeHarness();
    h.fake->AddResponse("C:/repo/top\n", 0); // rev-parse --show-toplevel
    h.fake->AddResponse("main\n", 0);        // rev-parse --abbrev-ref HEAD

    EXPECT_EQ(h.backend->OpenRepository("path"), "OK" + US + "C:/repo/top" + US + "main");
    ASSERT_EQ(h.fake->CallCount(), 2u);
    EXPECT_EQ(h.fake->ArgsOf(0), (Args{ "-C", "path", "rev-parse", "--show-toplevel" }));
    EXPECT_EQ(h.fake->ArgsOf(1), (Args{ "-C", "path", "rev-parse", "--abbrev-ref", "HEAD" }));
}

TEST(OpenRepository, NotARepoReturnsErr)
{
    auto h = MakeHarness();
    h.fake->AddResponse("", 128); // show-toplevel fails
    EXPECT_EQ(h.backend->OpenRepository("path"),
              "ERR" + US + "The selected folder is not a Git repository");
}

TEST(OpenRepository, DetachedHeadShowsShortHash)
{
    auto h = MakeHarness();
    h.fake->AddResponse("top\n", 0);      // toplevel
    h.fake->AddResponse("HEAD\n", 0);     // abbrev-ref -> detached
    h.fake->AddResponse("abc1234\n", 0);  // rev-parse --short HEAD

    EXPECT_EQ(h.backend->OpenRepository("path"), "OK" + US + "top" + US + "(detached abc1234)");
    ASSERT_EQ(h.fake->CallCount(), 3u);
    EXPECT_EQ(h.fake->ArgsOf(2), (Args{ "-C", "path", "rev-parse", "--short", "HEAD" }));
}

TEST(OpenRepository, EmptyBranchMeansNoCommitsYet)
{
    auto h = MakeHarness();
    h.fake->AddResponse("top\n", 0); // toplevel
    h.fake->AddResponse("\n", 0);    // abbrev-ref empty
    EXPECT_EQ(h.backend->OpenRepository("path"), "OK" + US + "top" + US + "(no commits yet)");
}

TEST(OpenRepository, UnbornBranchFallsBackToSymbolicRef)
{
    // A fresh `git init` has no commits, so rev-parse exits 128 and prints "ambiguous argument"
    // to stderr. Without the fallback that text would be displayed as the branch name.
    auto h = MakeHarness();
    h.fake->AddResponse("top\n", 0);
    h.fake->AddResponse("fatal: ambiguous argument 'HEAD': unknown revision or path not in the "
                        "working tree.\nHEAD\n", 128);
    h.fake->AddResponse("main\n", 0); // symbolic-ref --short -q HEAD

    EXPECT_EQ(h.backend->OpenRepository("path"), "OK" + US + "top" + US + "main (no commits yet)");
    ASSERT_EQ(h.fake->CallCount(), 3u);
    EXPECT_EQ(h.fake->ArgsOf(2), (Args{ "-C", "path", "symbolic-ref", "--short", "-q", "HEAD" }));
}

TEST(OpenRepository, UnbornBranchWithNoSymbolicRefStillReadable)
{
    auto h = MakeHarness();
    h.fake->AddResponse("top\n", 0);
    h.fake->AddResponse("fatal: ambiguous argument 'HEAD'\n", 128);
    h.fake->AddResponse("", 1); // symbolic-ref also fails
    EXPECT_EQ(h.backend->OpenRepository("path"), "OK" + US + "top" + US + "(no commits yet)");
}

// ---- Log ---------------------------------------------------------------------------------------

TEST(Log, DateOrderWithLimitBuildsExactArgs)
{
    auto h = MakeHarness();
    h.fake->SetResponse("", 0);
    h.backend->Log("root", 0, 100);
    EXPECT_EQ(h.fake->ArgsOf(0),
              (Args{ "-C", "root", "log", "--all", "--parents", "--date-order", "-n100", FMT }));
}

TEST(Log, TopoOrderHasNoReverse)
{
    auto h = MakeHarness();
    h.fake->SetResponse("", 0);
    h.backend->Log("root", 1, 0);
    EXPECT_TRUE(h.fake->ArgsContain(0, "--topo-order"));
    EXPECT_FALSE(h.fake->ArgsContain(0, "--reverse"));
}

TEST(Log, ReverseDateOrderAddsReverse)
{
    auto h = MakeHarness();
    h.fake->SetResponse("", 0);
    h.backend->Log("root", 2, 0);
    EXPECT_TRUE(h.fake->ArgsContain(0, "--date-order"));
    EXPECT_TRUE(h.fake->ArgsContain(0, "--reverse"));
}

TEST(Log, AuthorDateOrder)
{
    auto h = MakeHarness();
    h.fake->SetResponse("", 0);
    h.backend->Log("root", 3, 0);
    EXPECT_TRUE(h.fake->ArgsContain(0, "--author-date-order"));
}

TEST(Log, MaxCountZeroOmitsLimit)
{
    auto h = MakeHarness();
    h.fake->SetResponse("", 0);
    h.backend->Log("root", 0, 0);
    EXPECT_FALSE(AnyArgStartsWith(*h.fake, 0, "-n"));
    // Format string is always the final argument.
    EXPECT_EQ(h.fake->ArgsOf(0).back(), FMT);
}

TEST(Log, EmptyRootReturnsEmptyWithoutCallingGit)
{
    auto h = MakeHarness();
    EXPECT_EQ(h.backend->Log("", 0, 100), "");
    EXPECT_EQ(h.fake->CallCount(), 0u);
}

// ---- RefDetails (BR-001/BR-002/TAG-001) --------------------------------------------------------

TEST(RefDetails, BuildsForEachRefArgs)
{
    auto h = MakeHarness();
    h.fake->SetResponse("", 0);
    h.backend->RefDetails("root");
    EXPECT_EQ(h.fake->ArgsOf(0),
              (Args{ "-C", "root", "for-each-ref", "--sort=refname", REF_FMT,
                     "refs/heads", "refs/tags", "refs/remotes" }));
}

TEST(RefDetails, FormatUsesForEachRefHexEscapesNotPrettyFormatOnes)
{
    // Regression guard for the %1f-vs-%x1f trap: %x1f is what `log --pretty` wants and would be
    // emitted literally by for-each-ref.
    EXPECT_NE(REF_FMT.find("%1f"), std::string::npos);
    EXPECT_EQ(REF_FMT.find("%x1f"), std::string::npos);
}

TEST(RefDetails, EmptyRootReturnsEmptyWithoutCallingGit)
{
    auto h = MakeHarness();
    EXPECT_EQ(h.backend->RefDetails(""), "");
    EXPECT_EQ(h.fake->CallCount(), 0u);
}

TEST(RefDetails, ReturnsRawOutputUnmodified)
{
    // Two records: a branch with an upstream that is ahead+behind and checked out, and a tag with
    // every optional field empty. The 8-field shape must survive byte-for-byte for the C# parser.
    auto h = MakeHarness();
    const std::string payload =
        "refs/heads/main" + US + "aaa" + US + "" + US + "commit" + US +
        "origin/main" + US + "ahead 2, behind 1" + US + "*" + US + "" + RS + "\n" +
        "refs/tags/v1" + US + "bbb" + US + "" + US + "commit" + US +
        "" + US + "" + US + " " + US + "" + RS + "\n";
    h.fake->SetResponse(payload, 0);
    EXPECT_EQ(h.backend->RefDetails("root"), payload);
}

TEST(RefDetails, RecordsNoStdin)
{
    auto h = MakeHarness();
    h.fake->SetResponse("", 0);
    h.backend->RefDetails("root");
    EXPECT_FALSE(h.fake->InputOf(0).has_value());
}

// ---- Changed files -----------------------------------------------------------------------------

TEST(CommitFiles, BuildsNameStatusArgs)
{
    auto h = MakeHarness();
    h.fake->SetResponse("M\tfile.txt\n", 0);
    h.backend->CommitFiles("root", "deadbeef");
    EXPECT_EQ(h.fake->ArgsOf(0),
              (Args{ "-C", "root", "-c", "core.quotePath=false", "diff-tree", "--no-commit-id",
                     "-r", "-M", "--root", "--first-parent", "--name-status",
                     "--diff-merges=first-parent", "deadbeef", "-z" }));
}

// Merge handling is one decision shared by three commands: the file list, the stat line and the
// per-file diff must all describe the SAME diff. `-m` looks right and is not (it emits a section
// per parent), so pin the flag on all three rather than each one separately.
TEST(MergeDiffs, AllThreeCommandsUseFirstParentDiffMerges)
{
    auto h = MakeHarness();
    h.fake->SetResponse("", 0);
    h.backend->CommitFiles("root", "merge");
    h.backend->CommitShortStat("root", "merge");
    h.backend->FileDiff("root", "merge", "f.txt", 0);
    for (size_t i = 0; i < 3; ++i)
    {
        EXPECT_TRUE(h.fake->ArgsContain(i, "--diff-merges=first-parent")) << "call " << i;
        EXPECT_FALSE(h.fake->ArgsContain(i, "-m")) << "call " << i;
    }
}

// -z is not optional for the path-listing commands, so RunPathList appends it and no caller can
// forget. Without it a path holding a quote/backslash/control char arrives C-quoted and the host
// addresses a file that does not exist.
TEST(PathLists, AlwaysAskGitForNulSeparatedOutput)
{
    auto h = MakeHarness();
    h.fake->SetResponse("", 0);
    h.backend->CommitFiles("root", "sha");
    h.backend->RangeFiles("root", "a", "b");
    EXPECT_TRUE(h.fake->ArgsContain(0, "-z"));
    EXPECT_TRUE(h.fake->ArgsContain(1, "-z"));
}

// The payload cannot travel as NULs (the managed marshaller stops at the first one), so the
// separators are rewritten to RS. The embedded newline must survive untouched - that is the
// whole point of -z.
TEST(PathLists, NulSeparatorsBecomeRecordSeparators)
{
    auto h = MakeHarness();
    h.fake->SetResponse(std::string("A\000a\nb.txt\000", 10), 0);

    EXPECT_EQ(h.backend->CommitFiles("root", "sha"), std::string("A\036a\nb.txt\036", 10));
}
TEST(CommitFiles, EmptyShaReturnsEmptyWithoutCallingGit)
{
    auto h = MakeHarness();
    EXPECT_EQ(h.backend->CommitFiles("root", ""), "");
    EXPECT_EQ(h.fake->CallCount(), 0u);
}

TEST(CommitShortStat, BuildsShortStatArgs)
{
    auto h = MakeHarness();
    h.fake->SetResponse(" 1 file changed, 2 insertions(+)\n", 0);
    h.backend->CommitShortStat("root", "sha");
    EXPECT_EQ(h.fake->ArgsOf(0),
              (Args{ "-C", "root", "diff-tree", "--shortstat", "-M", "--first-parent", "--root",
                     "--no-commit-id", "--diff-merges=first-parent", "sha" }));
}

// ---- Diffs (whitespace flag mapping) -----------------------------------------------------------

TEST(FileDiff, NoWhitespaceFlagAndArgOrder)
{
    auto h = MakeHarness();
    h.fake->SetResponse("@@ -1 +1 @@\n", 0);
    h.backend->FileDiff("root", "sha", "path/to/file.cpp", 0);
    EXPECT_EQ(h.fake->ArgsOf(0),
              (Args{ "-C", "root", "diff-tree", "-p", "-M", "--first-parent", "--root", "--no-commit-id",
                     "--no-color", "--diff-merges=first-parent", "sha", "--",
                     "path/to/file.cpp" }));
}

TEST(FileDiff, IgnoreSpaceChangeFlag)
{
    auto h = MakeHarness();
    h.fake->SetResponse("", 0);
    h.backend->FileDiff("root", "sha", "f", 1);
    EXPECT_TRUE(h.fake->ArgsContain(0, "--ignore-space-change"));
    EXPECT_FALSE(h.fake->ArgsContain(0, "--ignore-all-space"));
}

TEST(FileDiff, IgnoreAllSpaceFlag)
{
    auto h = MakeHarness();
    h.fake->SetResponse("", 0);
    h.backend->FileDiff("root", "sha", "f", 2);
    EXPECT_TRUE(h.fake->ArgsContain(0, "--ignore-all-space"));
    EXPECT_FALSE(h.fake->ArgsContain(0, "--ignore-space-change"));
}

TEST(FileDiff, EmptyPathReturnsEmptyWithoutCallingGit)
{
    auto h = MakeHarness();
    EXPECT_EQ(h.backend->FileDiff("root", "sha", "", 0), "");
    EXPECT_EQ(h.fake->CallCount(), 0u);
}

// ---- File content at a commit ------------------------------------------------------------------

TEST(FileAtCommit, ReturnsContentOnSuccess)
{
    auto h = MakeHarness();
    h.fake->SetResponse("file body", 0);
    EXPECT_EQ(h.backend->FileAtCommit("root", "sha", "a.txt"), "file body");
    EXPECT_EQ(h.fake->ArgsOf(0), (Args{ "-C", "root", "show", "sha:a.txt" }));
}

TEST(FileAtCommit, ReturnsEmptyOnError)
{
    auto h = MakeHarness();
    h.fake->SetResponse("fatal: bad object", 128);
    EXPECT_EQ(h.backend->FileAtCommit("root", "sha", "a.txt"), "");
}

// ---- Compare two commits / refs ----------------------------------------------------------------

TEST(RangeFiles, BuildsDiffNameStatusWithAThenB)
{
    auto h = MakeHarness();
    h.fake->SetResponse("A\tnew.txt\n", 0);
    h.backend->RangeFiles("root", "v1", "v2");
    EXPECT_EQ(h.fake->ArgsOf(0),
              (Args{ "-C", "root", "-c", "core.quotePath=false", "diff", "--name-status",
                     "-M", "v1", "v2", "-z" }));
}

TEST(RangeShortStat, BuildsArgs)
{
    auto h = MakeHarness();
    h.fake->SetResponse("", 0);
    h.backend->RangeShortStat("root", "a", "b");
    EXPECT_EQ(h.fake->ArgsOf(0), (Args{ "-C", "root", "diff", "--shortstat", "-M", "a", "b" }));
}

TEST(RangeFileDiff, WhitespaceFlagAndArgOrder)
{
    auto h = MakeHarness();
    h.fake->SetResponse("", 0);
    h.backend->RangeFileDiff("root", "a", "b", "file.cs", 2);
    EXPECT_EQ(h.fake->ArgsOf(0),
              (Args{ "-C", "root", "diff", "-M", "--no-color", "--ignore-all-space",
                     "a", "b", "--", "file.cs" }));
}

// ---- Binary bytes (binary-safety regression guard) ---------------------------------------------

TEST(FileBytesAt, NulloptOnError)
{
    auto h = MakeHarness();
    h.fake->SetResponse("", 128);
    EXPECT_FALSE(h.backend->FileBytesAt("root", "sha", "img.png").has_value());
}

TEST(FileBytesAt, PreservesEmbeddedNulBytes)
{
    auto h = MakeHarness();
    const std::string payload("\x89PNG\x00\x0A\x1A", 7); // NUL at index 4
    h.fake->SetResponse(payload, 0);

    auto result = h.backend->FileBytesAt("root", "sha", "img.png");
    ASSERT_TRUE(result.has_value());
    EXPECT_EQ(result->size(), 7u);
    EXPECT_EQ(*result, payload);
}

// ---- Working tree status (Phase 3) ---------------------------------------------------------------

TEST(Status, BuildsPorcelainArgs)
{
    auto h = MakeHarness();
    h.fake->SetResponse("", 0);
    h.backend->Status("root");
    EXPECT_EQ(h.fake->ArgsOf(0),
              (Args{ "-C", "root", "-c", "core.quotePath=false",
                     "status", "--porcelain=v1", "-z", "--untracked-files=all" }));
}

TEST(Status, TranslatesNulsToRecordSeparators)
{
    auto h = MakeHarness();
    const std::string raw("M  a.txt\0?? b.txt\0", 18);
    h.fake->SetResponse(raw, 0);
    EXPECT_EQ(h.backend->Status("root"), "M  a.txt\x1e?? b.txt\x1e");
}

TEST(Status, RenameKeepsBothPathRecords)
{
    auto h = MakeHarness();
    // -z rename record: "R  new\0orig\0" — new path first, original second.
    const std::string raw("R  new.txt\0old.txt\0", 19);
    h.fake->SetResponse(raw, 0);
    EXPECT_EQ(h.backend->Status("root"), "R  new.txt\x1eold.txt\x1e");
}

TEST(Status, EmptyOnGitError)
{
    auto h = MakeHarness();
    h.fake->SetResponse("fatal: not a git repository", 128);
    EXPECT_EQ(h.backend->Status("root"), "");
}

TEST(Status, EmptyRootReturnsEmptyWithoutCallingGit)
{
    auto h = MakeHarness();
    EXPECT_EQ(h.backend->Status(""), "");
    EXPECT_EQ(h.fake->CallCount(), 0u);
}

// ---- Working tree file diffs (Phase 3) -----------------------------------------------------------

TEST(WorkTreeFileDiff, UnstagedArgs)
{
    auto h = MakeHarness();
    h.fake->SetResponse("", 0);
    h.backend->WorkTreeFileDiff("root", "f.cs", 0, 0);
    EXPECT_EQ(h.fake->ArgsOf(0),
              (Args{ "-C", "root", "diff", "--no-color", "--", "f.cs" }));
}

TEST(WorkTreeFileDiff, StagedAddsCached)
{
    auto h = MakeHarness();
    h.fake->SetResponse("", 0);
    h.backend->WorkTreeFileDiff("root", "f.cs", 1, 0);
    EXPECT_EQ(h.fake->ArgsOf(0),
              (Args{ "-C", "root", "diff", "--cached", "--no-color",
                     "--", "f.cs" }));
}

TEST(WorkTreeFileDiff, UntrackedUsesNoIndexAgainstDevNull)
{
    auto h = MakeHarness();
    h.fake->SetResponse("", 0);
    h.backend->WorkTreeFileDiff("root", "new file.txt", 2, 0);
    EXPECT_EQ(h.fake->ArgsOf(0),
              (Args{ "-C", "root", "diff", "--no-color", "--no-index",
                     "--", "/dev/null", "new file.txt" }));
}

TEST(WorkTreeFileDiff, WhitespaceFlags)
{
    auto h = MakeHarness();
    h.fake->AddResponse("", 0);
    h.fake->AddResponse("", 0);
    h.backend->WorkTreeFileDiff("root", "f.cs", 0, 1);
    h.backend->WorkTreeFileDiff("root", "f.cs", 0, 2);
    EXPECT_TRUE(h.fake->ArgsContain(0, "--ignore-space-change"));
    EXPECT_TRUE(h.fake->ArgsContain(1, "--ignore-all-space"));
}

TEST(WorkTreeFileDiff, UntrackedReturnsOutputOnExitCode1)
{
    // diff --no-index exits 1 when the files differ — that is the success case here.
    auto h = MakeHarness();
    h.fake->SetResponse("diff --git a/dev/null b/n.txt\n+new line\n", 1);
    EXPECT_EQ(h.backend->WorkTreeFileDiff("root", "n.txt", 2, 0),
              "diff --git a/dev/null b/n.txt\n+new line\n");
}

TEST(WorkTreeFileDiff, EmptyPathReturnsEmptyWithoutCallingGit)
{
    auto h = MakeHarness();
    EXPECT_EQ(h.backend->WorkTreeFileDiff("root", "", 0, 0), "");
    EXPECT_EQ(h.fake->CallCount(), 0u);
}

TEST(WorkTreeFileDiff, InvalidAreaReturnsEmptyWithoutCallingGit)
{
    auto h = MakeHarness();
    EXPECT_EQ(h.backend->WorkTreeFileDiff("root", "f.cs", 3, 0), "");
    EXPECT_EQ(h.backend->WorkTreeFileDiff("root", "f.cs", -1, 0), "");
    EXPECT_EQ(h.fake->CallCount(), 0u);
}

// ---- Stdin contract (Phase 4) ------------------------------------------------------------------

TEST(StdinContract, ReadOpsRecordNoInput)
{
    auto h = MakeHarness();
    h.fake->SetResponse("", 0);
    h.backend->Status("root");
    ASSERT_EQ(h.fake->CallCount(), 1u);
    EXPECT_FALSE(h.fake->InputOf(0).has_value());
}

// ---- StagePaths (COMMIT-001) -------------------------------------------------------------------

TEST(StagePaths, BuildsAddArgs)
{
    auto h = MakeHarness();
    h.fake->SetResponse("", 0);
    EXPECT_EQ(h.backend->StagePaths("root", { "a.txt" }), "OK");
    EXPECT_EQ(h.fake->ArgsOf(0), (Args{ "-C", "root", "add", "-A", "--", "a.txt" }));
    EXPECT_FALSE(h.fake->InputOf(0).has_value());
}

TEST(StagePaths, BatchesMultiplePaths)
{
    auto h = MakeHarness();
    h.fake->SetResponse("", 0);
    EXPECT_EQ(h.backend->StagePaths("root", { "a.txt", "dir/b.txt" }), "OK");
    EXPECT_EQ(h.fake->ArgsOf(0), (Args{ "-C", "root", "add", "-A", "--", "a.txt", "dir/b.txt" }));
}

TEST(StagePaths, EmptyPathsReturnsErrWithoutCallingGit)
{
    auto h = MakeHarness();
    EXPECT_EQ(h.backend->StagePaths("root", {}), "ERR" + US + "No paths were provided");
    EXPECT_EQ(h.fake->CallCount(), 0u);
}

TEST(StagePaths, EmptyRootReturnsErrWithoutCallingGit)
{
    auto h = MakeHarness();
    EXPECT_EQ(h.backend->StagePaths("", { "a.txt" }), "ERR" + US + "No repository root was provided");
    EXPECT_EQ(h.fake->CallCount(), 0u);
}

TEST(StagePaths, ErrIncludesGitOutputOnNonzeroExit)
{
    auto h = MakeHarness();
    h.fake->SetResponse("fatal: pathspec 'nope' did not match any files\n", 128);
    EXPECT_EQ(h.backend->StagePaths("root", { "nope" }),
              "ERR" + US + "fatal: pathspec 'nope' did not match any files");
}

// ---- StageAll (COMMIT-003) ---------------------------------------------------------------------

TEST(StageAll, BuildsAddAllArgs)
{
    auto h = MakeHarness();
    h.fake->SetResponse("", 0);
    EXPECT_EQ(h.backend->StageAll("root"), "OK");
    EXPECT_EQ(h.fake->ArgsOf(0), (Args{ "-C", "root", "add", "-A" }));
}

TEST(StageAll, ErrOnFailure)
{
    auto h = MakeHarness();
    h.fake->SetResponse("error: unable to write index\n", 1);
    EXPECT_EQ(h.backend->StageAll("root"), "ERR" + US + "error: unable to write index");
}

// ---- UnstagePaths (COMMIT-002) -----------------------------------------------------------------

TEST(UnstagePaths, ProbesHeadThenUsesRestoreStaged)
{
    auto h = MakeHarness();
    h.fake->AddResponse("deadbeef\n", 0); // rev-parse HEAD succeeds -> HEAD exists
    h.fake->AddResponse("", 0);
    EXPECT_EQ(h.backend->UnstagePaths("root", { "a.txt" }), "OK");
    ASSERT_EQ(h.fake->CallCount(), 2u);
    EXPECT_EQ(h.fake->ArgsOf(0), (Args{ "-C", "root", "rev-parse", "--verify", "--quiet", "HEAD" }));
    EXPECT_EQ(h.fake->ArgsOf(1), (Args{ "-C", "root", "restore", "--staged", "--", "a.txt" }));
}

TEST(UnstagePaths, UsesRmCachedWhenHeadUnborn)
{
    auto h = MakeHarness();
    h.fake->AddResponse("", 1); // rev-parse HEAD fails -> unborn branch (fresh repo)
    h.fake->AddResponse("", 0);
    EXPECT_EQ(h.backend->UnstagePaths("root", { "a.txt" }), "OK");
    ASSERT_EQ(h.fake->CallCount(), 2u);
    EXPECT_EQ(h.fake->ArgsOf(1), (Args{ "-C", "root", "rm", "-r", "--cached", "-q", "--", "a.txt" }));
}

TEST(UnstagePaths, PassesBothPathsOfARename)
{
    auto h = MakeHarness();
    h.fake->AddResponse("deadbeef\n", 0);
    h.fake->AddResponse("", 0);
    EXPECT_EQ(h.backend->UnstagePaths("root", { "new.txt", "old.txt" }), "OK");
    EXPECT_EQ(h.fake->ArgsOf(1), (Args{ "-C", "root", "restore", "--staged", "--", "new.txt", "old.txt" }));
}

TEST(UnstagePaths, EmptyPathsReturnsErrWithoutCallingGit)
{
    auto h = MakeHarness();
    EXPECT_EQ(h.backend->UnstagePaths("root", {}), "ERR" + US + "No paths were provided");
    EXPECT_EQ(h.fake->CallCount(), 0u);
}

// ---- DiscardPaths (COMMIT-004) -----------------------------------------------------------------

TEST(DiscardPaths, BuildsRestoreArgs)
{
    auto h = MakeHarness();
    h.fake->SetResponse("", 0);
    EXPECT_EQ(h.backend->DiscardPaths("root", { "a.txt" }), "OK");
    EXPECT_EQ(h.fake->ArgsOf(0), (Args{ "-C", "root", "restore", "--", "a.txt" }));
}

TEST(DiscardPaths, EmptyPathsReturnsErrWithoutCallingGit)
{
    auto h = MakeHarness();
    EXPECT_EQ(h.backend->DiscardPaths("root", {}), "ERR" + US + "No paths were provided");
    EXPECT_EQ(h.fake->CallCount(), 0u);
}

TEST(DiscardPaths, ErrOnFailure)
{
    auto h = MakeHarness();
    h.fake->SetResponse("error: pathspec 'x' did not match\n", 1);
    EXPECT_EQ(h.backend->DiscardPaths("root", { "x" }),
              "ERR" + US + "error: pathspec 'x' did not match");
}

// ---- Commit (COMMIT-005/006/007) ---------------------------------------------------------------

TEST(Commit, FeedsMessageViaStdinWithFDash)
{
    auto h = MakeHarness();
    h.fake->SetResponse("[master abc1234] subject\n", 0);
    EXPECT_EQ(h.backend->Commit("root", "subject\n\nbody line 1\nbody line 2", false), "OK");
    ASSERT_EQ(h.fake->CallCount(), 1u);
    EXPECT_EQ(h.fake->ArgsOf(0), (Args{ "-C", "root", "commit", "--cleanup=strip", "-F", "-" }));
    ASSERT_TRUE(h.fake->InputOf(0).has_value());
    EXPECT_EQ(*h.fake->InputOf(0), "subject\n\nbody line 1\nbody line 2");
}

TEST(Commit, AmendAddsFlag)
{
    auto h = MakeHarness();
    h.fake->SetResponse("", 0);
    EXPECT_EQ(h.backend->Commit("root", "msg", true), "OK");
    EXPECT_EQ(h.fake->ArgsOf(0), (Args{ "-C", "root", "commit", "--amend", "--cleanup=strip", "-F", "-" }));
}

TEST(Commit, EmptyMessageReturnsErrWithoutCallingGit)
{
    auto h = MakeHarness();
    EXPECT_EQ(h.backend->Commit("root", "", false), "ERR" + US + "Commit message is empty");
    EXPECT_EQ(h.fake->CallCount(), 0u);
}

TEST(Commit, WhitespaceOnlyMessageReturnsErrWithoutCallingGit)
{
    auto h = MakeHarness();
    EXPECT_EQ(h.backend->Commit("root", "  \t\r\n ", false), "ERR" + US + "Commit message is empty");
    EXPECT_EQ(h.fake->CallCount(), 0u);
}

TEST(Commit, HookFailureMapsOutputToErr)
{
    auto h = MakeHarness();
    h.fake->SetResponse("commit-msg hook declined\n", 1);
    EXPECT_EQ(h.backend->Commit("root", "msg", false), "ERR" + US + "commit-msg hook declined");
}

// ---- HeadMessage (amend pre-fill) --------------------------------------------------------------

TEST(HeadMessage, BuildsLogArgs)
{
    auto h = MakeHarness();
    h.fake->SetResponse("subj" + US + "body", 0);
    h.backend->HeadMessage("root");
    EXPECT_EQ(h.fake->ArgsOf(0), (Args{ "-C", "root", "log", "-1", "--pretty=format:%s%x1f%b" }));
}

TEST(HeadMessage, ParsesSubjectAndBody)
{
    auto h = MakeHarness();
    h.fake->SetResponse("the subject" + US + "body line 1\nbody line 2\n", 0);
    EXPECT_EQ(h.backend->HeadMessage("root"),
              "OK" + US + "the subject" + US + "body line 1\nbody line 2");
}

TEST(HeadMessage, ErrWhenNoCommits)
{
    auto h = MakeHarness();
    h.fake->SetResponse("fatal: your current branch 'master' does not have any commits yet\n", 128);
    EXPECT_EQ(h.backend->HeadMessage("root"),
              "ERR" + US + "fatal: your current branch 'master' does not have any commits yet");
}

// ---- Checkout (BR-003) -------------------------------------------------------------------------

TEST(Checkout, BuildsSwitchArgs)
{
    auto h = MakeHarness();
    h.fake->SetResponse("Switched to branch 'feature'\n", 0);
    EXPECT_EQ(h.backend->Checkout("root", "feature", false), "OK");
    EXPECT_EQ(h.fake->ArgsOf(0), (Args{ "-C", "root", "switch", "feature" }));
    EXPECT_FALSE(h.fake->InputOf(0).has_value());
}

TEST(Checkout, DetachAddsFlagBeforeRef)
{
    auto h = MakeHarness();
    h.fake->SetResponse("", 0);
    EXPECT_EQ(h.backend->Checkout("root", "abc1234", true), "OK");
    EXPECT_EQ(h.fake->ArgsOf(0), (Args{ "-C", "root", "switch", "--detach", "abc1234" }));
}

TEST(Checkout, NeverForces)
{
    auto h = MakeHarness();
    h.fake->SetResponse("", 0);
    h.backend->Checkout("root", "feature", false);
    EXPECT_FALSE(h.fake->ArgsContain(0, "--force"));
    EXPECT_FALSE(h.fake->ArgsContain(0, "-f"));
}

TEST(Checkout, EmptyRefReturnsErrWithoutCallingGit)
{
    auto h = MakeHarness();
    EXPECT_EQ(h.backend->Checkout("root", "  ", false),
              "ERR" + US + "No branch or commit was provided");
    EXPECT_EQ(h.fake->CallCount(), 0u);
}

TEST(Checkout, EmptyRootReturnsErrWithoutCallingGit)
{
    auto h = MakeHarness();
    EXPECT_EQ(h.backend->Checkout("", "feature", false),
              "ERR" + US + "No repository root was provided");
    EXPECT_EQ(h.fake->CallCount(), 0u);
}

TEST(Checkout, ErrIncludesGitOutputOnDirtyTreeRefusal)
{
    auto h = MakeHarness();
    h.fake->SetResponse("error: Your local changes to the following files would be overwritten\n", 1);
    EXPECT_EQ(h.backend->Checkout("root", "feature", false),
              "ERR" + US + "error: Your local changes to the following files would be overwritten");
}

// ---- CreateBranch (BR-004) ---------------------------------------------------------------------

TEST(CreateBranch, BranchFromHeadWhenNoStartPoint)
{
    auto h = MakeHarness();
    h.fake->SetResponse("", 0);
    EXPECT_EQ(h.backend->CreateBranch("root", "feature", "", false), "OK");
    EXPECT_EQ(h.fake->ArgsOf(0), (Args{ "-C", "root", "branch", "feature" }));
}

TEST(CreateBranch, BranchWithStartPoint)
{
    auto h = MakeHarness();
    h.fake->SetResponse("", 0);
    h.backend->CreateBranch("root", "feature", "origin/main", false);
    EXPECT_EQ(h.fake->ArgsOf(0), (Args{ "-C", "root", "branch", "feature", "origin/main" }));
}

TEST(CreateBranch, SwitchDashCWhenCheckoutRequested)
{
    auto h = MakeHarness();
    h.fake->SetResponse("", 0);
    h.backend->CreateBranch("root", "feature", "", true);
    EXPECT_EQ(h.fake->ArgsOf(0), (Args{ "-C", "root", "switch", "-c", "feature" }));
}

TEST(CreateBranch, SwitchDashCWithStartPoint)
{
    auto h = MakeHarness();
    h.fake->SetResponse("", 0);
    h.backend->CreateBranch("root", "feature", "abc1234", true);
    EXPECT_EQ(h.fake->ArgsOf(0), (Args{ "-C", "root", "switch", "-c", "feature", "abc1234" }));
}

TEST(CreateBranch, NeverUsesForcingVariants)
{
    // `switch -C` / `branch -f` would silently clobber an existing branch. Assert on the flag's
    // position rather than membership: every command already carries a leading `-C <root>`.
    auto h = MakeHarness();
    h.fake->SetResponse("", 0);
    h.backend->CreateBranch("root", "feature", "", true);
    EXPECT_EQ(h.fake->ArgsOf(0)[3], "-c");
    h.fake->SetResponse("", 0);
    h.backend->CreateBranch("root", "feature", "", false);
    EXPECT_FALSE(h.fake->ArgsContain(1, "-f"));
    EXPECT_FALSE(h.fake->ArgsContain(1, "--force"));
}

TEST(CreateBranch, BlankNameReturnsErrWithoutCallingGit)
{
    auto h = MakeHarness();
    EXPECT_EQ(h.backend->CreateBranch("root", " \t ", "", false),
              "ERR" + US + "No branch name was provided");
    EXPECT_EQ(h.fake->CallCount(), 0u);
}

TEST(CreateBranch, ErrWhenBranchAlreadyExists)
{
    auto h = MakeHarness();
    h.fake->SetResponse("fatal: a branch named 'feature' already exists\n", 128);
    EXPECT_EQ(h.backend->CreateBranch("root", "feature", "", false),
              "ERR" + US + "fatal: a branch named 'feature' already exists");
}

// ---- DeleteBranch (BR-005) ---------------------------------------------------------------------

TEST(DeleteBranch, SafeDeleteUsesLowercaseD)
{
    auto h = MakeHarness();
    h.fake->SetResponse("Deleted branch feature (was abc1234).\n", 0);
    EXPECT_EQ(h.backend->DeleteBranch("root", "feature", false), "OK");
    EXPECT_EQ(h.fake->ArgsOf(0), (Args{ "-C", "root", "branch", "-d", "feature" }));
}

TEST(DeleteBranch, ForceUsesUppercaseD)
{
    auto h = MakeHarness();
    h.fake->SetResponse("", 0);
    h.backend->DeleteBranch("root", "feature", true);
    EXPECT_EQ(h.fake->ArgsOf(0), (Args{ "-C", "root", "branch", "-D", "feature" }));
}

TEST(DeleteBranch, BlankNameReturnsErrWithoutCallingGit)
{
    auto h = MakeHarness();
    EXPECT_EQ(h.backend->DeleteBranch("root", "", false),
              "ERR" + US + "No branch name was provided");
    EXPECT_EQ(h.fake->CallCount(), 0u);
}

TEST(DeleteBranch, ErrIncludesNotFullyMergedMessage)
{
    // This exact text is what the UI quotes back in its "delete anyway?" follow-up (BR-005).
    auto h = MakeHarness();
    h.fake->SetResponse("error: the branch 'feature' is not fully merged\n", 1);
    EXPECT_EQ(h.backend->DeleteBranch("root", "feature", false),
              "ERR" + US + "error: the branch 'feature' is not fully merged");
}

// ---- RenameBranch (BR-006) ---------------------------------------------------------------------

TEST(RenameBranch, BuildsBranchDashMArgs)
{
    auto h = MakeHarness();
    h.fake->SetResponse("", 0);
    EXPECT_EQ(h.backend->RenameBranch("root", "old", "new"), "OK");
    EXPECT_EQ(h.fake->ArgsOf(0), (Args{ "-C", "root", "branch", "-m", "old", "new" }));
}

TEST(RenameBranch, BlankOldNameReturnsErrWithoutCallingGit)
{
    auto h = MakeHarness();
    EXPECT_EQ(h.backend->RenameBranch("root", "", "new"),
              "ERR" + US + "No branch name was provided");
    EXPECT_EQ(h.fake->CallCount(), 0u);
}

TEST(RenameBranch, BlankNewNameReturnsErrWithoutCallingGit)
{
    auto h = MakeHarness();
    EXPECT_EQ(h.backend->RenameBranch("root", "old", "  "),
              "ERR" + US + "No branch name was provided");
    EXPECT_EQ(h.fake->CallCount(), 0u);
}

TEST(RenameBranch, ErrOnFailure)
{
    auto h = MakeHarness();
    h.fake->SetResponse("fatal: a branch named 'new' already exists\n", 128);
    EXPECT_EQ(h.backend->RenameBranch("root", "old", "new"),
              "ERR" + US + "fatal: a branch named 'new' already exists");
}

// ---- CreateTag (TAG-002) -----------------------------------------------------------------------

TEST(CreateTag, LightweightAtCommitSendsNoStdin)
{
    auto h = MakeHarness();
    h.fake->SetResponse("", 0);
    EXPECT_EQ(h.backend->CreateTag("root", "v1.0", "abc1234", ""), "OK");
    EXPECT_EQ(h.fake->ArgsOf(0), (Args{ "-C", "root", "tag", "v1.0", "abc1234" }));
    EXPECT_FALSE(h.fake->InputOf(0).has_value());
}

TEST(CreateTag, LightweightAtHeadOmitsCommitish)
{
    auto h = MakeHarness();
    h.fake->SetResponse("", 0);
    h.backend->CreateTag("root", "v1.0", "", "");
    EXPECT_EQ(h.fake->ArgsOf(0), (Args{ "-C", "root", "tag", "v1.0" }));
}

TEST(CreateTag, AnnotatedFeedsMessageOnStdin)
{
    auto h = MakeHarness();
    h.fake->SetResponse("", 0);
    EXPECT_EQ(h.backend->CreateTag("root", "v1.0", "abc1234", "release notes\nsecond line"), "OK");
    EXPECT_EQ(h.fake->ArgsOf(0), (Args{ "-C", "root", "tag", "-a", "-F", "-", "v1.0", "abc1234" }));
    ASSERT_TRUE(h.fake->InputOf(0).has_value());
    EXPECT_EQ(*h.fake->InputOf(0), "release notes\nsecond line");
}

TEST(CreateTag, AnnotatedAtHeadOmitsCommitish)
{
    auto h = MakeHarness();
    h.fake->SetResponse("", 0);
    h.backend->CreateTag("root", "v1.0", "", "notes");
    EXPECT_EQ(h.fake->ArgsOf(0), (Args{ "-C", "root", "tag", "-a", "-F", "-", "v1.0" }));
}

TEST(CreateTag, WhitespaceOnlyMessageStaysLightweight)
{
    auto h = MakeHarness();
    h.fake->SetResponse("", 0);
    h.backend->CreateTag("root", "v1.0", "", " \t\r\n ");
    EXPECT_EQ(h.fake->ArgsOf(0), (Args{ "-C", "root", "tag", "v1.0" }));
    EXPECT_FALSE(h.fake->InputOf(0).has_value());
}

TEST(CreateTag, BlankNameReturnsErrWithoutCallingGit)
{
    auto h = MakeHarness();
    EXPECT_EQ(h.backend->CreateTag("root", " ", "abc1234", ""),
              "ERR" + US + "No tag name was provided");
    EXPECT_EQ(h.fake->CallCount(), 0u);
}

TEST(CreateTag, ErrWhenTagExists)
{
    auto h = MakeHarness();
    h.fake->SetResponse("fatal: tag 'v1.0' already exists\n", 128);
    EXPECT_EQ(h.backend->CreateTag("root", "v1.0", "", ""),
              "ERR" + US + "fatal: tag 'v1.0' already exists");
}

// ---- DeleteTag (TAG-003) -----------------------------------------------------------------------

TEST(DeleteTag, BuildsTagDashDArgs)
{
    auto h = MakeHarness();
    h.fake->SetResponse("Deleted tag 'v1.0' (was abc1234)\n", 0);
    EXPECT_EQ(h.backend->DeleteTag("root", "v1.0"), "OK");
    EXPECT_EQ(h.fake->ArgsOf(0), (Args{ "-C", "root", "tag", "-d", "v1.0" }));
}

TEST(DeleteTag, BlankNameReturnsErrWithoutCallingGit)
{
    auto h = MakeHarness();
    EXPECT_EQ(h.backend->DeleteTag("root", ""), "ERR" + US + "No tag name was provided");
    EXPECT_EQ(h.fake->CallCount(), 0u);
}

TEST(DeleteTag, ErrOnFailure)
{
    auto h = MakeHarness();
    h.fake->SetResponse("error: tag 'v1.0' not found.\n", 1);
    EXPECT_EQ(h.backend->DeleteTag("root", "v1.0"), "ERR" + US + "error: tag 'v1.0' not found.");
}

// ---- AheadBehind (BR-007) ----------------------------------------------------------------------

TEST(AheadBehind, BuildsRevListThreeDotArgs)
{
    auto h = MakeHarness();
    h.fake->SetResponse("2\t3\n", 0);
    EXPECT_EQ(h.backend->AheadBehind("root", "main", "feature"), "2\t3");
    // The two refs are joined into ONE argument -- "main feature" as separate args would mean
    // something else entirely to rev-list.
    EXPECT_EQ(h.fake->ArgsOf(0),
              (Args{ "-C", "root", "rev-list", "--left-right", "--count", "main...feature" }));
}

TEST(AheadBehind, EmptyOnGitError)
{
    auto h = MakeHarness();
    h.fake->SetResponse("fatal: bad revision\n", 128);
    EXPECT_EQ(h.backend->AheadBehind("root", "main", "nope"), "");
}

TEST(AheadBehind, EmptyRefsReturnEmptyWithoutCallingGit)
{
    auto h = MakeHarness();
    EXPECT_EQ(h.backend->AheadBehind("root", "", "feature"), "");
    EXPECT_EQ(h.backend->AheadBehind("root", "main", ""), "");
    EXPECT_EQ(h.backend->AheadBehind("", "main", "feature"), "");
    EXPECT_EQ(h.fake->CallCount(), 0u);
}

// ---- Remotes (Phase 6, REMOTE-001..009) --------------------------------------------------------

namespace
{
    // A sink that records what the runner streamed and can be told to cancel. Mirrors what the
    // host does: accumulate chunks, decide on each callback whether to keep going.
    struct RecordingSink
    {
        std::string received;
        int calls = 0;
        int heartbeats = 0;
        bool cancelAfterFirstCall = false;

        ms::GitBackend::ProgressSink Get()
        {
            return [this](const char* bytes, std::size_t length) {
                ++calls;
                if (length == 0)
                    ++heartbeats;
                else
                    received.append(bytes, length);
                return !cancelAfterFirstCall;
            };
        }
    };
}

TEST(Remotes, BuildsRemoteDashVArgs)
{
    auto h = MakeHarness();
    h.fake->SetResponse("origin\thttps://example.test/r.git (fetch)\n"
                        "origin\thttps://example.test/r.git (push)\n", 0);
    // Returned verbatim: pairing the (fetch)/(push) lines is parsing, which lives in the host.
    EXPECT_EQ(h.backend->Remotes("root"),
              "origin\thttps://example.test/r.git (fetch)\n"
              "origin\thttps://example.test/r.git (push)\n");
    EXPECT_EQ(h.fake->ArgsOf(0), (Args{ "-C", "root", "remote", "-v" }));
}

TEST(Remotes, EmptyOnGitErrorOrBlankRoot)
{
    auto h = MakeHarness();
    h.fake->SetResponse("fatal: not a git repository\n", 128);
    EXPECT_EQ(h.backend->Remotes("root"), "");
    EXPECT_EQ(h.backend->Remotes(""), "");
    EXPECT_EQ(h.fake->CallCount(), 1u); // the blank root never reached git
}

TEST(SetRemoteUrl, BuildsFetchUrlArgs)
{
    auto h = MakeHarness();
    EXPECT_EQ(h.backend->SetRemoteUrl("root", "origin", "https://example.test/r.git", false), "OK");
    EXPECT_EQ(h.fake->ArgsOf(0),
              (Args{ "-C", "root", "remote", "set-url", "origin", "https://example.test/r.git" }));
}

TEST(SetRemoteUrl, PushUrlAddsDashDashPush)
{
    auto h = MakeHarness();
    EXPECT_EQ(h.backend->SetRemoteUrl("root", "origin", "https://example.test/r.git", true), "OK");
    EXPECT_EQ(h.fake->ArgsOf(0),
              (Args{ "-C", "root", "remote", "set-url", "--push", "origin",
                     "https://example.test/r.git" }));
}

TEST(SetRemoteUrl, BlankArgsReturnErrWithoutCallingGit)
{
    auto h = MakeHarness();
    EXPECT_EQ(h.backend->SetRemoteUrl("", "origin", "u", false),
              "ERR" + US + "No repository root was provided");
    EXPECT_EQ(h.backend->SetRemoteUrl("root", " ", "u", false),
              "ERR" + US + "No remote name was provided");
    EXPECT_EQ(h.backend->SetRemoteUrl("root", "origin", " \t", false),
              "ERR" + US + "No URL was provided");
    EXPECT_EQ(h.fake->CallCount(), 0u);
}

TEST(SetRemoteUrl, ErrOnFailure)
{
    auto h = MakeHarness();
    h.fake->SetResponse("error: No such remote 'upstream'\n", 2);
    EXPECT_EQ(h.backend->SetRemoteUrl("root", "upstream", "u", false),
              "ERR" + US + "error: No such remote 'upstream'");
}

TEST(Fetch, BuildsPlainFetchArgs)
{
    auto h = MakeHarness();
    EXPECT_EQ(h.backend->Fetch("root", "origin", false, false, false, {}), "OK");
    EXPECT_EQ(h.fake->ArgsOf(0), (Args{ "-C", "root", "fetch", "--progress", "origin" }));
}

TEST(Fetch, AllRemotesReplacesTheRemoteName)
{
    auto h = MakeHarness();
    // --all and a remote name are mutually exclusive to git; --all wins and the name is dropped.
    EXPECT_EQ(h.backend->Fetch("root", "origin", true, false, false, {}), "OK");
    EXPECT_EQ(h.fake->ArgsOf(0), (Args{ "-C", "root", "fetch", "--progress", "--all" }));
}

TEST(Fetch, PruneAndTagsAreOptIn)
{
    auto h = MakeHarness();
    EXPECT_EQ(h.backend->Fetch("root", "origin", false, true, true, {}), "OK");
    EXPECT_EQ(h.fake->ArgsOf(0),
              (Args{ "-C", "root", "fetch", "--progress", "origin", "--prune", "--tags" }));
}

TEST(Fetch, BlankRemoteReturnsErrUnlessAllRemotes)
{
    auto h = MakeHarness();
    EXPECT_EQ(h.backend->Fetch("root", " ", false, false, false, {}),
              "ERR" + US + "No remote was provided");
    EXPECT_EQ(h.backend->Fetch("", "origin", false, false, false, {}),
              "ERR" + US + "No repository root was provided");
    EXPECT_EQ(h.fake->CallCount(), 0u);
    // --all needs no remote name.
    EXPECT_EQ(h.backend->Fetch("root", "", true, false, false, {}), "OK");
    EXPECT_EQ(h.fake->CallCount(), 1u);
}

TEST(Pull, IsAlwaysFastForwardOnly)
{
    auto h = MakeHarness();
    EXPECT_EQ(h.backend->Pull("root", "origin", "main", {}), "OK");
    EXPECT_EQ(h.fake->ArgsOf(0),
              (Args{ "-C", "root", "pull", "--ff-only", "--progress", "origin", "main" }));
    // REMOTE-004 is fast-forward only by design: nothing may quietly merge or rebase.
    EXPECT_FALSE(h.fake->ArgsContain(0, "--rebase"));
    EXPECT_FALSE(h.fake->ArgsContain(0, "--no-ff"));
}

TEST(Pull, OmitsRemoteAndBranchWhenEitherIsBlank)
{
    auto h = MakeHarness();
    // Bare `git pull --ff-only` uses the branch's configured upstream, which is what an
    // unconfigured caller wants. A half-specified pair would be a git usage error.
    EXPECT_EQ(h.backend->Pull("root", "", "", {}), "OK");
    EXPECT_EQ(h.fake->ArgsOf(0), (Args{ "-C", "root", "pull", "--ff-only", "--progress" }));
    EXPECT_EQ(h.backend->Pull("root", "origin", "", {}), "OK");
    EXPECT_EQ(h.fake->ArgsOf(1), (Args{ "-C", "root", "pull", "--ff-only", "--progress" }));
}

TEST(Pull, ErrCarriesTheDivergedRefusal)
{
    auto h = MakeHarness();
    h.fake->SetResponse("fatal: Not possible to fast-forward, aborting.\n", 128);
    EXPECT_EQ(h.backend->Pull("root", "origin", "main", {}),
              "ERR" + US + "fatal: Not possible to fast-forward, aborting.");
}

TEST(Push, BuildsPlainPushArgs)
{
    auto h = MakeHarness();
    EXPECT_EQ(h.backend->Push("root", "origin", "main", false, false, {}), "OK");
    EXPECT_EQ(h.fake->ArgsOf(0), (Args{ "-C", "root", "push", "--progress", "origin", "main" }));
}

TEST(Push, SetUpstreamPublishesTheBranch)
{
    auto h = MakeHarness();
    EXPECT_EQ(h.backend->Push("root", "origin", "feature/x", true, false, {}), "OK");
    EXPECT_EQ(h.fake->ArgsOf(0),
              (Args{ "-C", "root", "push", "--progress", "--set-upstream", "origin", "feature/x" }));
}

TEST(Push, TagsAreOptIn)
{
    auto h = MakeHarness();
    EXPECT_EQ(h.backend->Push("root", "origin", "main", false, true, {}), "OK");
    EXPECT_EQ(h.fake->ArgsOf(0),
              (Args{ "-C", "root", "push", "--progress", "--tags", "origin", "main" }));
}

TEST(Push, BlankArgsReturnErrWithoutCallingGit)
{
    auto h = MakeHarness();
    EXPECT_EQ(h.backend->Push("", "origin", "main", false, false, {}),
              "ERR" + US + "No repository root was provided");
    EXPECT_EQ(h.backend->Push("root", " ", "main", false, false, {}),
              "ERR" + US + "No remote was provided");
    EXPECT_EQ(h.backend->Push("root", "origin", "\t", false, false, {}),
              "ERR" + US + "No branch was provided");
    EXPECT_EQ(h.fake->CallCount(), 0u);
}

TEST(Push, ErrCarriesTheRejection)
{
    auto h = MakeHarness();
    h.fake->SetResponse("! [rejected] main -> main (non-fast-forward)\n", 1);
    EXPECT_EQ(h.backend->Push("root", "origin", "main", false, false, {}),
              "ERR" + US + "! [rejected] main -> main (non-fast-forward)");
}

TEST(NetworkCommands, NeverForce)
{
    auto h = MakeHarness();
    h.backend->Fetch("root", "origin", false, true, true, {});
    h.backend->Pull("root", "origin", "main", {});
    h.backend->Push("root", "origin", "main", true, true, {});
    // Phase 6 deliberately ships no forced variant of any remote command: a rejected push must
    // reach the user as git's own refusal, never be overridden behind their back.
    for (size_t i = 0; i < h.fake->CallCount(); ++i)
    {
        EXPECT_FALSE(h.fake->ArgsContain(i, "--force")) << "call " << i;
        EXPECT_FALSE(h.fake->ArgsContain(i, "--force-with-lease")) << "call " << i;
        EXPECT_FALSE(h.fake->ArgsContain(i, "-f")) << "call " << i;
    }
}

TEST(NetworkCommands, AllPassProgressAndDisableTerminalPrompts)
{
    auto h = MakeHarness();
    h.backend->Fetch("root", "origin", false, false, false, {});
    h.backend->Pull("root", "origin", "main", {});
    h.backend->Push("root", "origin", "main", false, false, {});
    ASSERT_EQ(h.fake->CallCount(), 3u);
    for (size_t i = 0; i < 3; ++i)
    {
        // git prints no progress when stderr is not a tty, and it never is here.
        EXPECT_TRUE(h.fake->ArgsContain(i, "--progress")) << "call " << i;
        // Without this a GUI child can sit forever on a credential prompt nobody can answer.
        EXPECT_EQ(h.fake->EnvOf(i, "GIT_TERMINAL_PROMPT"), std::optional<std::string>("0"))
            << "call " << i;
    }
}

TEST(NetworkCommands, LocalCommandsGetNoSinkAndNoEnvOverrides)
{
    auto h = MakeHarness();
    h.backend->Log("root", 0, 10);
    h.backend->Commit("root", "msg", false);
    // Phase 8: stash/blame/search/reflog are all local and fast — no streaming, no environment.
    h.backend->StashApply("root", "");
    h.backend->StashDrop("root", "");
    h.backend->Blame("root", "HEAD", "a", false, "");
    h.backend->SearchLog("root", "message", "x", "", 0, 10, true, false, false);
    h.backend->Reflog("root", "HEAD", 10);
    ASSERT_EQ(h.fake->CallCount(), 7u);
    for (size_t i = 0; i < 7; ++i)
    {
        EXPECT_FALSE(h.fake->HadSink(i)) << "call " << i;
        EXPECT_FALSE(h.fake->EnvOf(i, "GIT_TERMINAL_PROMPT").has_value()) << "call " << i;
    }
}

TEST(NetworkCommands, StreamOutputThroughTheSink)
{
    auto h = MakeHarness();
    RecordingSink sink;
    h.fake->SetResponse("remote: Enumerating objects: 12, done.\n", 0);
    EXPECT_EQ(h.backend->Fetch("root", "origin", false, false, false, sink.Get()), "OK");
    EXPECT_TRUE(h.fake->HadSink(0));
    EXPECT_EQ(sink.received, "remote: Enumerating objects: 12, done.\n");
    // The heartbeat is what lets the host cancel a command that has gone quiet.
    EXPECT_EQ(sink.heartbeats, 1);
}

TEST(NetworkCommands, SinkCancellationEndsTheCommandAsErr)
{
    auto h = MakeHarness();
    RecordingSink sink;
    sink.cancelAfterFirstCall = true;
    h.fake->SetResponse("Connecting to example.test...\n", 0);
    // A cancelled command is a failed command: the child was killed, so nothing completed.
    EXPECT_EQ(h.backend->Push("root", "origin", "main", false, false, sink.Get()),
              "ERR" + US + "Connecting to example.test...");
    EXPECT_TRUE(h.fake->SinkCancelled(0));
    EXPECT_EQ(sink.calls, 1); // no heartbeat after the cancel
}

// ---- Merge / rebase / cherry-pick / revert (Phase 7) -------------------------------------------

TEST(Merge, BuildsPlainMergeArgs)
{
    auto h = MakeHarness();
    EXPECT_EQ(h.backend->Merge("root", "feature", false, false, {}), "OK");
    // --no-edit always: git would otherwise open an editor for the merge message, and a GUI child
    // blocked on one never returns. The `--` keeps a branch named like a path from being one.
    EXPECT_EQ(h.fake->ArgsOf(0),
              (Args{ "-C", "root", "merge", "--no-edit", "--", "feature" }));
}

TEST(Merge, NoFastForwardAndNoCommitAreOptIn)
{
    auto h = MakeHarness();
    h.backend->Merge("root", "feature", true, true, {});
    EXPECT_EQ(h.fake->ArgsOf(0),
              (Args{ "-C", "root", "merge", "--no-edit", "--no-ff", "--no-commit", "--", "feature" }));
}

TEST(Merge, BlankArgsReturnErrWithoutCallingGit)
{
    auto h = MakeHarness();
    EXPECT_EQ(h.backend->Merge("", "feature", false, false, {}),
              "ERR" + US + "No repository root was provided");
    EXPECT_EQ(h.backend->Merge("root", "  ", false, false, {}),
              "ERR" + US + "No branch or commit was provided");
    EXPECT_EQ(h.fake->CallCount(), 0u);
}

TEST(Merge, ConflictArrivesAsErrCarryingGitsOwnText)
{
    auto h = MakeHarness();
    // A conflict is a NORMAL outcome reported through the error channel: git exits 1 and the
    // payload is what the host shows and pattern-matches on.
    h.fake->SetResponse("Auto-merging f.txt\nCONFLICT (content): Merge conflict in f.txt\n"
                        "Automatic merge failed; fix conflicts and then commit the result.\n", 1);
    EXPECT_EQ(h.backend->Merge("root", "feature", false, false, {}),
              "ERR" + US + "Auto-merging f.txt\nCONFLICT (content): Merge conflict in f.txt\n"
                           "Automatic merge failed; fix conflicts and then commit the result.");
}

TEST(Rebase, BuildsPlainRebaseArgs)
{
    auto h = MakeHarness();
    EXPECT_EQ(h.backend->Rebase("root", "main", {}), "OK");
    // No `--` (git rebase takes no separator) and no options at all: interactive, autosquash and
    // autostash are all deliberately out of Phase 7.
    EXPECT_EQ(h.fake->ArgsOf(0), (Args{ "-C", "root", "rebase", "main" }));
}

TEST(Rebase, BlankArgsReturnErrWithoutCallingGit)
{
    auto h = MakeHarness();
    EXPECT_EQ(h.backend->Rebase("", "main", {}), "ERR" + US + "No repository root was provided");
    EXPECT_EQ(h.backend->Rebase("root", "\t ", {}), "ERR" + US + "No upstream branch was provided");
    EXPECT_EQ(h.fake->CallCount(), 0u);
}

TEST(CherryPick, BuildsSingleCommitArgs)
{
    auto h = MakeHarness();
    EXPECT_EQ(h.backend->CherryPick("root", { "abc123" }, false, {}), "OK");
    EXPECT_EQ(h.fake->ArgsOf(0),
              (Args{ "-C", "root", "cherry-pick", "--no-edit", "abc123" }));
}

TEST(CherryPick, AppliesMultipleCommitsInTheOrderGiven)
{
    auto h = MakeHarness();
    // One command for the whole set, left to right: that ordering IS the feature (CHERRY-002), and
    // it is also what leaves the rest queued in .git/sequencer when one of them conflicts.
    h.backend->CherryPick("root", { "oldest", "middle", "newest" }, false, {});
    EXPECT_EQ(h.fake->ArgsOf(0),
              (Args{ "-C", "root", "cherry-pick", "--no-edit", "oldest", "middle", "newest" }));
}

TEST(CherryPick, NoCommitIsOptIn)
{
    auto h = MakeHarness();
    h.backend->CherryPick("root", { "abc123" }, true, {});
    EXPECT_EQ(h.fake->ArgsOf(0),
              (Args{ "-C", "root", "cherry-pick", "--no-edit", "-n", "abc123" }));
}

TEST(CherryPick, BlankArgsReturnErrWithoutCallingGit)
{
    auto h = MakeHarness();
    EXPECT_EQ(h.backend->CherryPick("", { "abc" }, false, {}),
              "ERR" + US + "No repository root was provided");
    EXPECT_EQ(h.backend->CherryPick("root", {}, false, {}),
              "ERR" + US + "No commits were provided");
    EXPECT_EQ(h.fake->CallCount(), 0u);
}

TEST(Revert, OmitsMainlineForAnOrdinaryCommit)
{
    auto h = MakeHarness();
    EXPECT_EQ(h.backend->Revert("root", "abc123", 0, false, {}), "OK");
    // Passing -m for a non-merge commit is an error in git, so 0 has to mean "leave it out".
    EXPECT_EQ(h.fake->ArgsOf(0), (Args{ "-C", "root", "revert", "--no-edit", "abc123" }));
}

TEST(Revert, PassesMainlineForAMergeCommit)
{
    auto h = MakeHarness();
    h.backend->Revert("root", "abc123", 1, false, {});
    EXPECT_EQ(h.fake->ArgsOf(0),
              (Args{ "-C", "root", "revert", "--no-edit", "-m", "1", "abc123" }));
}

TEST(Revert, NoCommitIsOptIn)
{
    auto h = MakeHarness();
    h.backend->Revert("root", "abc123", 2, true, {});
    EXPECT_EQ(h.fake->ArgsOf(0),
              (Args{ "-C", "root", "revert", "--no-edit", "-m", "2", "-n", "abc123" }));
}

TEST(Revert, BlankArgsReturnErrWithoutCallingGit)
{
    auto h = MakeHarness();
    EXPECT_EQ(h.backend->Revert("", "abc", 0, false, {}),
              "ERR" + US + "No repository root was provided");
    EXPECT_EQ(h.backend->Revert("root", " ", 0, false, {}),
              "ERR" + US + "No commit was provided");
    EXPECT_EQ(h.fake->CallCount(), 0u);
}

TEST(SequencerAction, BuildsTheSubcommandAndFlag)
{
    auto h = MakeHarness();
    EXPECT_EQ(h.backend->SequencerAction("root", "merge", "abort", {}), "OK");
    EXPECT_EQ(h.backend->SequencerAction("root", "rebase", "continue", {}), "OK");
    EXPECT_EQ(h.backend->SequencerAction("root", "cherry-pick", "skip", {}), "OK");
    EXPECT_EQ(h.backend->SequencerAction("root", "revert", "abort", {}), "OK");
    EXPECT_EQ(h.fake->ArgsOf(0), (Args{ "-C", "root", "merge", "--abort" }));
    EXPECT_EQ(h.fake->ArgsOf(1), (Args{ "-C", "root", "rebase", "--continue" }));
    EXPECT_EQ(h.fake->ArgsOf(2), (Args{ "-C", "root", "cherry-pick", "--skip" }));
    EXPECT_EQ(h.fake->ArgsOf(3), (Args{ "-C", "root", "revert", "--abort" }));
}

TEST(SequencerAction, MergeHasNoSkip)
{
    auto h = MakeHarness();
    // Verified against git 2.54: `git merge --skip` is "error: unknown option `skip'". Catching it
    // here beats handing the user git's usage dump.
    EXPECT_EQ(h.backend->SequencerAction("root", "merge", "skip", {}),
              "ERR" + US + "\"skip\" is not a valid action for git merge");
    EXPECT_EQ(h.fake->CallCount(), 0u);
}

TEST(SequencerAction, RejectsAnythingOutsideTheAllowlists)
{
    auto h = MakeHarness();
    EXPECT_EQ(h.backend->SequencerAction("root", "push", "abort", {}),
              "ERR" + US + "Unknown operation: push");
    EXPECT_EQ(h.backend->SequencerAction("root", "rebase", "--exec=rm -rf /", {}),
              "ERR" + US + "\"--exec=rm -rf /\" is not a valid action for git rebase");
    EXPECT_EQ(h.backend->SequencerAction("root", "", "", {}),
              "ERR" + US + "Unknown operation: ");
    EXPECT_EQ(h.backend->SequencerAction("", "merge", "abort", {}),
              "ERR" + US + "No repository root was provided");
    // Nothing unrecognized ever reaches git.
    EXPECT_EQ(h.fake->CallCount(), 0u);
}

TEST(MergeTool, BuildsNoPromptArgsWithoutAToolName)
{
    auto h = MakeHarness();
    EXPECT_EQ(h.backend->MergeTool("root", "src/f.txt", "", {}), "OK");
    // A blank tool defers to the repository's merge.tool config; --no-prompt is never optional,
    // because the prompt would go to a terminal this process does not have.
    EXPECT_EQ(h.fake->ArgsOf(0),
              (Args{ "-C", "root", "mergetool", "--no-prompt", "--", "src/f.txt" }));
}

TEST(MergeTool, AddsTheToolWhenNamed)
{
    auto h = MakeHarness();
    h.backend->MergeTool("root", "src/f.txt", "kdiff3", {});
    EXPECT_EQ(h.fake->ArgsOf(0),
              (Args{ "-C", "root", "mergetool", "--no-prompt", "--tool=kdiff3", "--", "src/f.txt" }));
}

TEST(MergeTool, RejectsAToolNameThatIsNotOne)
{
    auto h = MakeHarness();
    EXPECT_EQ(h.backend->MergeTool("root", "f.txt", "not a tool", {}).rfind("ERR", 0), 0u);
    EXPECT_EQ(h.backend->MergeTool("root", "f.txt", "--upload-pack=x", {}).rfind("ERR", 0), 0u);
    EXPECT_EQ(h.backend->MergeTool("", "f.txt", "", {}),
              "ERR" + US + "No repository root was provided");
    EXPECT_EQ(h.backend->MergeTool("root", " ", "", {}), "ERR" + US + "No file was provided");
    EXPECT_EQ(h.fake->CallCount(), 0u);
}

TEST(Phase7Commands, NeverInteractiveOrForcing)
{
    auto h = MakeHarness();
    h.backend->Merge("root", "feature", true, true, {});
    h.backend->Rebase("root", "main", {});
    h.backend->CherryPick("root", { "abc" }, true, {});
    h.backend->Revert("root", "abc", 1, true, {});
    h.backend->SequencerAction("root", "rebase", "continue", {});
    h.backend->MergeTool("root", "f.txt", "kdiff3", {});
    ASSERT_EQ(h.fake->CallCount(), 6u);

    for (size_t i = 0; i < h.fake->CallCount(); ++i)
    {
        // An editor the user cannot see is a hang, not a prompt. `--continue` has no --no-edit
        // flag, so the environment is the ONLY cover for it.
        EXPECT_EQ(h.fake->EnvOf(i, "GIT_EDITOR"), std::optional<std::string>("true")) << "call " << i;
        EXPECT_EQ(h.fake->EnvOf(i, "GIT_SEQUENCE_EDITOR"), std::optional<std::string>("true"))
            << "call " << i;
        EXPECT_EQ(h.fake->EnvOf(i, "GIT_TERMINAL_PROMPT"), std::optional<std::string>("0"))
            << "call " << i;

        // Phase 7 ships no variant that rewrites more than was asked for, or that hides a step.
        EXPECT_FALSE(h.fake->ArgsContain(i, "-i")) << "call " << i;
        EXPECT_FALSE(h.fake->ArgsContain(i, "--interactive")) << "call " << i;
        EXPECT_FALSE(h.fake->ArgsContain(i, "--squash")) << "call " << i;
        EXPECT_FALSE(h.fake->ArgsContain(i, "--autosquash")) << "call " << i;
        EXPECT_FALSE(h.fake->ArgsContain(i, "--autostash")) << "call " << i;
        EXPECT_FALSE(h.fake->ArgsContain(i, "--force-rebase")) << "call " << i;
        EXPECT_FALSE(h.fake->ArgsContain(i, "-f")) << "call " << i;
        EXPECT_FALSE(h.fake->ArgsContain(i, "-X")) << "call " << i;
        EXPECT_FALSE(h.fake->ArgsContain(i, "-Xours")) << "call " << i;
        EXPECT_FALSE(h.fake->ArgsContain(i, "-Xtheirs")) << "call " << i;
    }
}

TEST(Phase7Commands, StreamOutputThroughTheSink)
{
    auto h = MakeHarness();
    RecordingSink sink;
    h.fake->SetResponse("Rebasing (1/3)\n", 0);
    EXPECT_EQ(h.backend->Rebase("root", "main", sink.Get()), "OK");
    EXPECT_TRUE(h.fake->HadSink(0));
    EXPECT_EQ(sink.received, "Rebasing (1/3)\n");
    EXPECT_EQ(sink.heartbeats, 1);
}

// ---- RepositoryState (MERGE-003 / REBASE-002) --------------------------------------------------

namespace
{
    // A throwaway directory standing in for a .git dir. The state probe reads real files, so the
    // tests build a real (temporary) directory — still hermetic: no git, no repository, no process.
    class TempGitDir
    {
    public:
        TempGitDir()
        {
            static int counter = 0;
            path_ = std::filesystem::temp_directory_path()
                  / ("ms-state-test-" + std::to_string(++counter) + "-"
                     + std::to_string(static_cast<long long>(
                           std::chrono::steady_clock::now().time_since_epoch().count())));
            std::filesystem::create_directories(path_);
        }

        ~TempGitDir()
        {
            std::error_code ec;
            std::filesystem::remove_all(path_, ec);
        }

        TempGitDir(const TempGitDir&) = delete;
        TempGitDir& operator=(const TempGitDir&) = delete;

        std::string Path() const { return path_.generic_string(); }

        void Write(const std::string& relative, const std::string& contents) const
        {
            std::filesystem::path file = path_ / relative;
            std::filesystem::create_directories(file.parent_path());
            std::ofstream out(file, std::ios::binary);
            out << contents;
        }

    private:
        std::filesystem::path path_;
    };

    // Fields of the OK record: state, detail, step, total, message.
    std::vector<std::string> SplitRecord(const std::string& record)
    {
        std::vector<std::string> fields;
        std::size_t start = 0;
        while (start <= record.size())
        {
            std::size_t end = record.find('\x1f', start);
            if (end == std::string::npos)
                end = record.size();
            fields.push_back(record.substr(start, end - start));
            start = end + 1;
        }
        return fields;
    }
}

TEST(RepositoryState, BuildsAbsoluteGitDirArgs)
{
    auto h = MakeHarness();
    TempGitDir dir;
    h.fake->SetResponse(dir.Path() + "\n", 0);
    h.backend->RepositoryState("root");
    // ONE spawn — the rest is reading that directory. Five rev-parse --verify calls would cost
    // ~5x the process-launch floor on every refresh, and still could not detect a rebase.
    ASSERT_EQ(h.fake->CallCount(), 1u);
    EXPECT_EQ(h.fake->ArgsOf(0), (Args{ "-C", "root", "rev-parse", "--absolute-git-dir" }));
}

TEST(RepositoryState, CleanRepositoryReportsNone)
{
    auto h = MakeHarness();
    TempGitDir dir;
    h.fake->SetResponse(dir.Path() + "\n", 0);
    auto f = SplitRecord(h.backend->RepositoryState("root"));
    ASSERT_EQ(f.size(), 6u);
    EXPECT_EQ(f[0], "OK");
    EXPECT_EQ(f[1], "none");
    EXPECT_EQ(f[2], "");
    EXPECT_EQ(f[3], "0");
    EXPECT_EQ(f[4], "0");
    EXPECT_EQ(f[5], "");
}

TEST(RepositoryState, MergeHeadMeansMergingAndCarriesTheMessage)
{
    auto h = MakeHarness();
    TempGitDir dir;
    dir.Write("MERGE_HEAD", "1234567890abcdef1234567890abcdef12345678\n");
    // Git's "# Conflicts:" block is an instruction to the editor, not message text — it must not
    // reach the commit editor, which is why the backend strips it rather than the host.
    dir.Write("MERGE_MSG", "Merge branch 'feature'\n\n# Conflicts:\n#\tf.txt\n");
    h.fake->SetResponse(dir.Path() + "\n", 0);

    auto f = SplitRecord(h.backend->RepositoryState("root"));
    ASSERT_EQ(f.size(), 6u);
    EXPECT_EQ(f[1], "merging");
    EXPECT_EQ(f[2], "1234567"); // short sha, as the banner shows it
    EXPECT_EQ(f[5], "Merge branch 'feature'");
}

TEST(RepositoryState, RebaseMergeDirectoryReportsBranchAndProgress)
{
    auto h = MakeHarness();
    TempGitDir dir;
    dir.Write("rebase-merge/head-name", "refs/heads/feature\n");
    dir.Write("rebase-merge/msgnum", "2\n");
    dir.Write("rebase-merge/end", "5\n");
    h.fake->SetResponse(dir.Path() + "\n", 0);

    auto f = SplitRecord(h.backend->RepositoryState("root"));
    ASSERT_EQ(f.size(), 6u);
    EXPECT_EQ(f[1], "rebasing");
    EXPECT_EQ(f[2], "feature"); // refs/heads/ stripped
    EXPECT_EQ(f[3], "2");
    EXPECT_EQ(f[4], "5");
}

TEST(RepositoryState, RebaseApplyDirectoryUsesItsOwnFileNames)
{
    auto h = MakeHarness();
    TempGitDir dir;
    // The apply backend (`rebase --apply`, or an am-based rebase) names them next/last, not
    // msgnum/end.
    dir.Write("rebase-apply/head-name", "refs/heads/topic\n");
    dir.Write("rebase-apply/next", "3\n");
    dir.Write("rebase-apply/last", "4\n");
    h.fake->SetResponse(dir.Path() + "\n", 0);

    auto f = SplitRecord(h.backend->RepositoryState("root"));
    EXPECT_EQ(f[1], "rebasing");
    EXPECT_EQ(f[2], "topic");
    EXPECT_EQ(f[3], "3");
    EXPECT_EQ(f[4], "4");
}

TEST(RepositoryState, CherryPickAndRevertHaveTheirOwnHeads)
{
    {
        auto h = MakeHarness();
        TempGitDir dir;
        dir.Write("CHERRY_PICK_HEAD", "abcdef1234567890\n");
        h.fake->SetResponse(dir.Path() + "\n", 0);
        auto f = SplitRecord(h.backend->RepositoryState("root"));
        EXPECT_EQ(f[1], "cherry-picking");
        EXPECT_EQ(f[2], "abcdef1");
    }
    {
        auto h = MakeHarness();
        TempGitDir dir;
        dir.Write("REVERT_HEAD", "fedcba0987654321\n");
        h.fake->SetResponse(dir.Path() + "\n", 0);
        auto f = SplitRecord(h.backend->RepositoryState("root"));
        EXPECT_EQ(f[1], "reverting");
        EXPECT_EQ(f[2], "fedcba0");
    }
}

TEST(RepositoryState, RebaseWinsOverAMergeHeadLeftBehind)
{
    auto h = MakeHarness();
    TempGitDir dir;
    // A rebase stopped on a conflict writes MERGE_MSG and can leave merge-ish state around; the
    // rebase directory is the authoritative answer, and it is what carries continue/skip/abort.
    dir.Write("rebase-merge/head-name", "refs/heads/feature\n");
    dir.Write("rebase-merge/msgnum", "1\n");
    dir.Write("rebase-merge/end", "1\n");
    dir.Write("MERGE_HEAD", "1234567890abcdef\n");
    h.fake->SetResponse(dir.Path() + "\n", 0);

    auto f = SplitRecord(h.backend->RepositoryState("root"));
    EXPECT_EQ(f[1], "rebasing");
}

TEST(RepositoryState, ErrWhenGitCannotResolveTheGitDir)
{
    auto h = MakeHarness();
    h.fake->SetResponse("fatal: not a git repository\n", 128);
    EXPECT_EQ(h.backend->RepositoryState("root"),
              "ERR" + US + "The folder is not a Git repository");
    EXPECT_EQ(h.backend->RepositoryState(""),
              "ERR" + US + "No repository root was provided");
    EXPECT_EQ(h.fake->CallCount(), 1u); // the blank root never reached git
}

// ---- Stash (Phase 8, STASH-001..004) -----------------------------------------------------------

TEST(StashList, BuildsListArgsWithTheLogFamilyFormat)
{
    auto h = MakeHarness();
    h.fake->SetResponse("stash@{0}" + US + "abc" + US + "abc1234" + US + "WIP on main: x" + US
                        + "2026-08-15T10:00:00+02:00" + US + "Ada" + RS, 0);
    EXPECT_NE(h.backend->StashList("root"), "");
    EXPECT_EQ(h.fake->ArgsOf(0), (Args{ "-C", "root", "stash", "list", STASH_FMT }));
}

TEST(StashList, EmptyWhenThereAreNoStashesOrNoRoot)
{
    auto h = MakeHarness();
    // git prints nothing and exits 0 for an empty stash: an empty list, not a failure.
    h.fake->SetResponse("", 0);
    EXPECT_EQ(h.backend->StashList("root"), "");
    EXPECT_EQ(h.backend->StashList(""), "");
    EXPECT_EQ(h.fake->CallCount(), 1u); // the blank root never reached git
}

TEST(StashList, FailureIsNotHandedToTheRecordParser)
{
    auto h = MakeHarness();
    // git writes diagnostics to the same merged stream the records arrive on, so a non-zero exit
    // has to be turned into "" here — otherwise the message is parsed as if it were stash data.
    h.fake->SetResponse("fatal: not a git repository\n", 128);
    EXPECT_EQ(h.backend->StashList("root"), "");
}

TEST(StashSave, ProbesRefsStashAroundThePushAndReportsSuccess)
{
    auto h = MakeHarness();
    h.fake->AddResponse("", 1);                              // no refs/stash yet
    h.fake->AddResponse("Saved working directory\n", 0);     // the push
    h.fake->AddResponse("abc123\n", 0);                      // refs/stash now exists
    EXPECT_EQ(h.backend->StashSave("root", "wip", false, false), "OK");
    ASSERT_EQ(h.fake->CallCount(), 3u);
    EXPECT_EQ(h.fake->ArgsOf(0), (Args{ "-C", "root", "rev-parse", "--verify", "--quiet", "refs/stash" }));
    EXPECT_EQ(h.fake->ArgsOf(1), (Args{ "-C", "root", "stash", "push", "-m", "wip" }));
    EXPECT_EQ(h.fake->ArgsOf(2), h.fake->ArgsOf(0));
}

TEST(StashSave, CleanTreeIsAnErrorEvenThoughGitExitsZero)
{
    auto h = MakeHarness();
    // The whole point of the two probes: `git stash push` succeeds on a clean tree and stashes
    // nothing, and its "No local changes to save" text is localizable, so it cannot be matched.
    h.fake->AddResponse("abc123\n", 0);
    h.fake->AddResponse("No local changes to save\n", 0);
    h.fake->AddResponse("abc123\n", 0); // refs/stash unmoved
    EXPECT_EQ(h.backend->StashSave("root", "", false, false),
              "ERR" + US + "There were no local changes to save.");
    EXPECT_EQ(h.fake->CallCount(), 3u);
}

TEST(StashSave, UntrackedAndKeepIndexAreOptInAndABlankMessageOmitsDashM)
{
    auto h = MakeHarness();
    h.fake->AddResponse("", 1);
    h.fake->AddResponse("", 0);
    h.fake->AddResponse("abc\n", 0);
    EXPECT_EQ(h.backend->StashSave("root", "   ", true, true), "OK");
    // A blank message must not become `-m "   "` — git would store the whitespace verbatim.
    EXPECT_EQ(h.fake->ArgsOf(1),
              (Args{ "-C", "root", "stash", "push", "--include-untracked", "--keep-index" }));
}

TEST(StashSave, ErrCarriesGitsOwnOutputAndSkipsTheSecondProbe)
{
    auto h = MakeHarness();
    h.fake->AddResponse("", 1);
    h.fake->AddResponse("error: unable to write index\n", 1);
    EXPECT_EQ(h.backend->StashSave("root", "wip", false, false),
              "ERR" + US + "error: unable to write index");
    EXPECT_EQ(h.fake->CallCount(), 2u); // a failed push is not re-probed
}

TEST(StashSave, BlankRootReturnsErrWithoutSpawningGit)
{
    auto h = MakeHarness();
    EXPECT_EQ(h.backend->StashSave("", "wip", false, false),
              "ERR" + US + "No repository root was provided");
    EXPECT_EQ(h.fake->CallCount(), 0u);
}

TEST(StashApplyPopDrop, BuildTheirArgsWithAndWithoutASelector)
{
    auto h = MakeHarness();
    EXPECT_EQ(h.backend->StashApply("root", "stash@{2}"), "OK");
    EXPECT_EQ(h.backend->StashPop("root", ""), "OK");
    EXPECT_EQ(h.backend->StashDrop("root", "stash@{10}"), "OK");
    ASSERT_EQ(h.fake->CallCount(), 3u);
    EXPECT_EQ(h.fake->ArgsOf(0), (Args{ "-C", "root", "stash", "apply", "stash@{2}" }));
    // An omitted selector is git's own default (the most recent entry), not an error.
    EXPECT_EQ(h.fake->ArgsOf(1), (Args{ "-C", "root", "stash", "pop" }));
    EXPECT_EQ(h.fake->ArgsOf(2), (Args{ "-C", "root", "stash", "drop", "stash@{10}" }));
}

TEST(StashApplyPopDrop, RejectAnythingThatIsNotAStashSelectorWithoutSpawningGit)
{
    auto h = MakeHarness();
    // Not ref names, not revisions: only the selector shape MsGitStashList hands back.
    for (const std::string& bad : { "refs/stash", "stash", "stash@{}", "stash@{a}", "stash@{1}x",
                                    "HEAD", "--force", "stash@{1", "stash{1}" })
    {
        EXPECT_EQ(h.backend->StashDrop("root", bad), "ERR" + US + "Not a stash entry: " + bad)
            << "for " << bad;
    }
    EXPECT_EQ(h.fake->CallCount(), 0u);
}

TEST(StashApplyPopDrop, ConflictArrivesAsErrCarryingGitsText)
{
    auto h = MakeHarness();
    h.fake->SetResponse("CONFLICT (content): Merge conflict in a.txt\n", 1);
    EXPECT_EQ(h.backend->StashPop("root", ""),
              "ERR" + US + "CONFLICT (content): Merge conflict in a.txt");
}

// ---- Blame (Phase 8, BLAME-001) ----------------------------------------------------------------

TEST(Blame, BuildsPorcelainArgsAndDefaultsToHead)
{
    auto h = MakeHarness();
    h.fake->SetResponse("abc 1 1 1\n\tline\n", 0);
    EXPECT_EQ(h.backend->Blame("root", "", "src/a.cpp", false, ""),
              "OK" + US + "abc 1 1 1\n\tline\n");
    // --porcelain, not --line-porcelain: the latter repeats every header on every line.
    EXPECT_EQ(h.fake->ArgsOf(0),
              (Args{ "-C", "root", "-c", "core.quotePath=false", "blame", "--porcelain",
                     "HEAD", "--", "src/a.cpp" }));
}

TEST(Blame, MoveDetectionModesMapToTheirFlags)
{
    auto h = MakeHarness();
    h.backend->Blame("root", "main", "a", false, "none");
    h.backend->Blame("root", "main", "a", false, "file");
    h.backend->Blame("root", "main", "a", false, "commit");
    h.backend->Blame("root", "main", "a", true, "any");
    ASSERT_EQ(h.fake->CallCount(), 4u);
    // Exact argv, not ArgsContain: every call already carries a "-C" (the repository root), so a
    // containment check for the copy-detection flag would pass no matter what was built.
    EXPECT_EQ(h.fake->ArgsOf(0),
              (Args{ "-C", "root", "-c", "core.quotePath=false", "blame", "--porcelain",
                     "main", "--", "a" }));
    EXPECT_EQ(h.fake->ArgsOf(1),
              (Args{ "-C", "root", "-c", "core.quotePath=false", "blame", "--porcelain",
                     "-M", "main", "--", "a" }));
    EXPECT_EQ(h.fake->ArgsOf(2),
              (Args{ "-C", "root", "-c", "core.quotePath=false", "blame", "--porcelain",
                     "-C", "main", "--", "a" }));
    // "any" is -C -C (also detect copies from files the commit itself created).
    EXPECT_EQ(h.fake->ArgsOf(3),
              (Args{ "-C", "root", "-c", "core.quotePath=false", "blame", "--porcelain",
                     "-w", "-C", "-C", "main", "--", "a" }));
}

TEST(Blame, RejectsBadInputWithoutSpawningGit)
{
    auto h = MakeHarness();
    EXPECT_EQ(h.backend->Blame("", "HEAD", "a", false, ""),
              "ERR" + US + "No repository root was provided");
    EXPECT_EQ(h.backend->Blame("root", "HEAD", "  ", false, ""),
              "ERR" + US + "No file was provided");
    // A leading '-' would be read as an option; git's --end-of-options needs 2.24, so refuse.
    EXPECT_EQ(h.backend->Blame("root", "HEAD", "-rf", false, ""),
              "ERR" + US + "Invalid revision or path");
    EXPECT_EQ(h.backend->Blame("root", "--all", "a", false, ""),
              "ERR" + US + "Invalid revision or path");
    EXPECT_EQ(h.backend->Blame("root", "HEAD", "a", false, "aggressive"),
              "ERR" + US + "Unknown move detection mode: aggressive");
    EXPECT_EQ(h.fake->CallCount(), 0u);
}

TEST(Blame, BinaryFilesAreRefusedRatherThanTruncated)
{
    auto h = MakeHarness();
    // A NUL anywhere in the payload would truncate the whole string at the managed marshaller,
    // silently showing a fraction of the file as if it were all of it.
    h.fake->SetResponse(std::string("abc 1 1 1\n\t\x00\x01binary", 21), 0);
    EXPECT_EQ(h.backend->Blame("root", "HEAD", "a.png", false, ""),
              "ERR" + US + "This file is binary; blame is not available.");
}

TEST(Blame, ErrCarriesGitsOwnMessage)
{
    auto h = MakeHarness();
    h.fake->SetResponse("fatal: no such path 'gone.txt' in HEAD\n", 128);
    EXPECT_EQ(h.backend->Blame("root", "HEAD", "gone.txt", false, ""),
              "ERR" + US + "fatal: no such path 'gone.txt' in HEAD");
}

TEST(Blame, PayloadMayContainTheFieldSeparator)
{
    auto h = MakeHarness();
    // The host splits on the FIRST 0x1F only; file content that happens to contain one must
    // survive intact rather than being read as a field boundary.
    h.fake->SetResponse("abc 1 1 1\n\tvalue" + US + "other\n", 0);
    EXPECT_EQ(h.backend->Blame("root", "HEAD", "a", false, ""),
              "OK" + US + "abc 1 1 1\n\tvalue" + US + "other\n");
}

// ---- Search (Phase 8, SEARCH-001/002) ----------------------------------------------------------

TEST(SearchLog, MessageModeUsesGrepAndTheSameFormatAsLog)
{
    auto h = MakeHarness();
    h.backend->SearchLog("root", "message", "needle", "", 0, 50, false, false, false);
    // FMT is the constant Log's test pins: search results feed the same positional parser, so a
    // drift between the two would silently mis-map fields into the wrong columns.
    EXPECT_EQ(h.fake->ArgsOf(0),
              (Args{ "-C", "root", "log", "--parents", "--date-order", "-n50",
                     "--fixed-strings", "--grep=needle", "--regexp-ignore-case", FMT, "--" }));
}

TEST(SearchLog, RegexAndCaseAndAllBranchesAreOptIn)
{
    auto h = MakeHarness();
    h.backend->SearchLog("root", "message", "ne+dle", "", 1, 0, true, true, true);
    const auto args = h.fake->ArgsOf(0);
    EXPECT_TRUE(h.fake->ArgsContain(0, "--all"));
    EXPECT_TRUE(h.fake->ArgsContain(0, "--topo-order"));
    EXPECT_TRUE(h.fake->ArgsContain(0, "--grep=ne+dle"));
    EXPECT_FALSE(h.fake->ArgsContain(0, "--fixed-strings"));    // useRegex
    EXPECT_FALSE(h.fake->ArgsContain(0, "--regexp-ignore-case")); // matchCase
    EXPECT_FALSE(AnyArgStartsWith(*h.fake, 0, "-n"));            // maxCount 0 = no limit
}

TEST(SearchLog, AuthorContentAndPathModesEachUseOnePredicate)
{
    auto h = MakeHarness();
    h.backend->SearchLog("root", "author", "ada", "", 0, 10, true, false, false);
    h.backend->SearchLog("root", "content", "malloc", "", 0, 10, true, false, false);
    h.backend->SearchLog("root", "content", "mall.c", "", 0, 10, true, true, false);
    h.backend->SearchLog("root", "path", "src/a.cpp", "", 0, 10, true, false, false);
    ASSERT_EQ(h.fake->CallCount(), 4u);
    EXPECT_TRUE(h.fake->ArgsContain(0, "--author=ada"));
    // -S counts occurrences; -G matches the diff text itself, which is what a regex means here.
    EXPECT_TRUE(h.fake->ArgsContain(1, "-Smalloc"));
    EXPECT_TRUE(h.fake->ArgsContain(2, "-Gmall.c"));
    // Path mode has no text predicate at all — the query IS the pathspec (SEARCH-002).
    EXPECT_EQ(h.fake->ArgsOf(3),
              (Args{ "-C", "root", "log", "--parents", "--date-order", "-n10", FMT,
                     "--", "src/a.cpp" }));
}

TEST(SearchLog, PathFilterNarrowsAnyMode)
{
    auto h = MakeHarness();
    h.backend->SearchLog("root", "message", "fix", "src/", 0, 10, true, false, false);
    const auto args = h.fake->ArgsOf(0);
    ASSERT_GE(args.size(), 2u);
    EXPECT_EQ(args[args.size() - 2], "--");
    EXPECT_EQ(args.back(), "src/");
    EXPECT_TRUE(h.fake->ArgsContain(0, "--grep=fix"));
}

TEST(SearchLog, HashModeVerifiesTheRevisionBeforeWalking)
{
    auto h = MakeHarness();
    h.fake->AddResponse("abc123def\n", 0);
    h.fake->AddResponse("abc123def" + US + "abc123d" + RS, 0);
    EXPECT_NE(h.backend->SearchLog("root", "hash", "abc123", "", 0, 10, true, false, false), "");
    ASSERT_EQ(h.fake->CallCount(), 2u);
    EXPECT_EQ(h.fake->ArgsOf(0),
              (Args{ "-C", "root", "rev-parse", "--verify", "--quiet", "abc123^{commit}" }));
    // The resolved sha is what the walk gets, not the user's abbreviation.
    EXPECT_EQ(h.fake->ArgsOf(1),
              (Args{ "-C", "root", "log", "--parents", "-n1", FMT, "abc123def" }));
}

TEST(SearchLog, HashModeYieldsEmptyForAnUnknownRevision)
{
    auto h = MakeHarness();
    h.fake->AddResponse("", 1);
    // A typo must come back as "no results", never as git's error text rendered into the list.
    EXPECT_EQ(h.backend->SearchLog("root", "hash", "nope", "", 0, 10, true, false, false), "");
    EXPECT_EQ(h.fake->CallCount(), 1u); // no log walk after a failed probe
}

TEST(SearchLog, RejectedPatternReadsAsNoResults)
{
    auto h = MakeHarness();
    // A bad regex makes git exit non-zero; its complaint must not reach the record parser.
    h.fake->SetResponse("fatal: invalid regex\n", 128);
    EXPECT_EQ(h.backend->SearchLog("root", "message", "*[", "", 0, 10, true, true, false), "");
}

TEST(SearchLog, EmptyWithoutSpawningGitWhenThereIsNothingToSearchFor)
{
    auto h = MakeHarness();
    EXPECT_EQ(h.backend->SearchLog("", "message", "x", "", 0, 10, true, false, false), "");
    EXPECT_EQ(h.backend->SearchLog("root", "subject", "x", "", 0, 10, true, false, false), "");
    // An unfiltered walk here would look like a search that happened to match everything.
    EXPECT_EQ(h.backend->SearchLog("root", "message", "  ", "", 0, 10, true, false, false), "");
    EXPECT_EQ(h.backend->SearchLog("root", "hash", "", "", 0, 10, true, false, false), "");
    EXPECT_EQ(h.backend->SearchLog("root", "hash", "-rf", "", 0, 10, true, false, false), "");
    EXPECT_EQ(h.fake->CallCount(), 0u);
}

// ---- Reflog (Phase 8, REFLOG-001) --------------------------------------------------------------

TEST(Reflog, BuildsShowArgsAndDefaultsToHead)
{
    auto h = MakeHarness();
    h.fake->SetResponse("HEAD@{0}" + US + "abc" + US + "abc1234" + US + "commit: x" + US
                        + "2026-08-15T10:00:00+02:00" + US + "Ada" + US + "x" + RS, 0);
    EXPECT_NE(h.backend->Reflog("root", "", 25), "");
    EXPECT_EQ(h.fake->ArgsOf(0),
              (Args{ "-C", "root", "reflog", "show", REFLOG_FMT, "-n25", "HEAD" }));
}

TEST(Reflog, TakesAnExplicitRefAndAnUnlimitedCount)
{
    auto h = MakeHarness();
    h.backend->Reflog("root", "refs/stash", 0);
    EXPECT_EQ(h.fake->ArgsOf(0),
              (Args{ "-C", "root", "reflog", "show", REFLOG_FMT, "refs/stash" }));
}

TEST(Reflog, EmptyForARefWithNoReflogOrABadRef)
{
    auto h = MakeHarness();
    // git exits non-zero for a ref that has no reflog; that degrades to an empty list, which is
    // why no .git filesystem probing is needed to keep it quiet.
    h.fake->SetResponse("fatal: 'refs/stash' is not a valid ref\n", 128);
    EXPECT_EQ(h.backend->Reflog("root", "refs/stash", 10), "");
    EXPECT_EQ(h.backend->Reflog("", "HEAD", 10), "");
    EXPECT_EQ(h.backend->Reflog("root", "--all", 10), "");
    EXPECT_EQ(h.fake->CallCount(), 1u);
}

// ---- Null runner (defensive) -------------------------------------------------------------------

TEST(NullRunner, DoesNotCrashAndYieldsErrorPaths)
{
    ms::GitBackend backend(nullptr);
    EXPECT_FALSE(backend.IsRepository("root"));
    EXPECT_EQ(backend.Log("root", 0, 10), "");
    EXPECT_FALSE(backend.FileBytesAt("root", "sha", "f").has_value());
    EXPECT_EQ(backend.StagePaths("root", { "a" }), "ERR" + US + "git add failed");
    EXPECT_EQ(backend.StageAll("root"), "ERR" + US + "git add failed");
    EXPECT_EQ(backend.UnstagePaths("root", { "a" }), "ERR" + US + "git unstage failed");
    EXPECT_EQ(backend.DiscardPaths("root", { "a" }), "ERR" + US + "git restore failed");
    EXPECT_EQ(backend.Commit("root", "msg", false), "ERR" + US + "git commit failed");
    EXPECT_EQ(backend.HeadMessage("root"), "ERR" + US + "There is no commit yet");
    EXPECT_EQ(backend.RefDetails("root"), "");
    EXPECT_EQ(backend.Checkout("root", "feature", false), "ERR" + US + "git switch failed");
    EXPECT_EQ(backend.CreateBranch("root", "feature", "", false), "ERR" + US + "git branch failed");
    EXPECT_EQ(backend.DeleteBranch("root", "feature", false), "ERR" + US + "git branch failed");
    EXPECT_EQ(backend.RenameBranch("root", "old", "new"), "ERR" + US + "git branch failed");
    EXPECT_EQ(backend.CreateTag("root", "v1", "", ""), "ERR" + US + "git tag failed");
    EXPECT_EQ(backend.DeleteTag("root", "v1"), "ERR" + US + "git tag failed");
    EXPECT_EQ(backend.AheadBehind("root", "a", "b"), "");
    EXPECT_EQ(backend.Remotes("root"), "");
    EXPECT_EQ(backend.SetRemoteUrl("root", "origin", "u", false),
              "ERR" + US + "git remote set-url failed");
    EXPECT_EQ(backend.Fetch("root", "origin", false, false, false, {}),
              "ERR" + US + "git fetch failed");
    EXPECT_EQ(backend.Pull("root", "origin", "main", {}), "ERR" + US + "git pull failed");
    EXPECT_EQ(backend.Push("root", "origin", "main", false, false, {}),
              "ERR" + US + "git push failed");
    EXPECT_EQ(backend.Merge("root", "feature", false, false, {}), "ERR" + US + "git merge failed");
    EXPECT_EQ(backend.Rebase("root", "main", {}), "ERR" + US + "git rebase failed");
    EXPECT_EQ(backend.CherryPick("root", { "abc" }, false, {}),
              "ERR" + US + "git cherry-pick failed");
    EXPECT_EQ(backend.Revert("root", "abc", 0, false, {}), "ERR" + US + "git revert failed");
    EXPECT_EQ(backend.SequencerAction("root", "merge", "abort", {}),
              "ERR" + US + "git merge --abort failed");
    EXPECT_EQ(backend.MergeTool("root", "f.txt", "", {}), "ERR" + US + "git mergetool failed");
    EXPECT_EQ(backend.RepositoryState("root"),
              "ERR" + US + "The folder is not a Git repository");
    EXPECT_EQ(backend.StashList("root"), "");
    EXPECT_EQ(backend.StashSave("root", "wip", false, false), "ERR" + US + "git stash failed");
    EXPECT_EQ(backend.StashApply("root", ""), "ERR" + US + "git stash apply failed");
    EXPECT_EQ(backend.StashPop("root", ""), "ERR" + US + "git stash pop failed");
    EXPECT_EQ(backend.StashDrop("root", ""), "ERR" + US + "git stash drop failed");
    EXPECT_EQ(backend.Blame("root", "HEAD", "a", false, ""), "ERR" + US + "git blame failed");
    EXPECT_EQ(backend.SearchLog("root", "message", "x", "", 0, 10, true, false, false), "");
    EXPECT_EQ(backend.Reflog("root", "HEAD", 10), "");
}
