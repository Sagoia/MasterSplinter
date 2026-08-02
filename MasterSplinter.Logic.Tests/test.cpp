#include "pch.h"

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
                     "-r", "-M", "--root", "--first-parent", "-m", "--name-status", "deadbeef" }));
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
              (Args{ "-C", "root", "diff-tree", "--shortstat", "-M", "--first-parent",
                     "--root", "--no-commit-id", "sha" }));
}

// ---- Diffs (whitespace flag mapping) -----------------------------------------------------------

TEST(FileDiff, NoWhitespaceFlagAndArgOrder)
{
    auto h = MakeHarness();
    h.fake->SetResponse("@@ -1 +1 @@\n", 0);
    h.backend->FileDiff("root", "sha", "path/to/file.cpp", 0);
    EXPECT_EQ(h.fake->ArgsOf(0),
              (Args{ "-C", "root", "diff-tree", "-p", "-M", "--first-parent", "--root",
                     "--no-commit-id", "--no-color", "sha", "--", "path/to/file.cpp" }));
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
                     "-M", "v1", "v2" }));
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
              (Args{ "-C", "root", "--no-optional-locks", "-c", "core.quotePath=false",
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
              (Args{ "-C", "root", "--no-optional-locks", "diff", "--no-color", "--", "f.cs" }));
}

TEST(WorkTreeFileDiff, StagedAddsCached)
{
    auto h = MakeHarness();
    h.fake->SetResponse("", 0);
    h.backend->WorkTreeFileDiff("root", "f.cs", 1, 0);
    EXPECT_EQ(h.fake->ArgsOf(0),
              (Args{ "-C", "root", "--no-optional-locks", "diff", "--cached", "--no-color",
                     "--", "f.cs" }));
}

TEST(WorkTreeFileDiff, UntrackedUsesNoIndexAgainstDevNull)
{
    auto h = MakeHarness();
    h.fake->SetResponse("", 0);
    h.backend->WorkTreeFileDiff("root", "new file.txt", 2, 0);
    EXPECT_EQ(h.fake->ArgsOf(0),
              (Args{ "-C", "root", "--no-optional-locks", "diff", "--no-color", "--no-index",
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
    ASSERT_EQ(h.fake->CallCount(), 2u);
    for (size_t i = 0; i < 2; ++i)
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
}
