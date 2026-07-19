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

    // Must match the pretty-format string in GitBackend::Log exactly.
    const std::string FMT =
        "--pretty=format:%H%x1f%h%x1f%P%x1f%an%x1f%ae%x1f%aI%x1f%cn%x1f%ce%x1f%cI%x1f%D%x1f%s%x1f%b%x1e";

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

// ---- Refs --------------------------------------------------------------------------------------

TEST(Refs, BuildsForEachRefArgs)
{
    auto h = MakeHarness();
    h.fake->SetResponse("refs/heads/main\n", 0);
    h.backend->Refs("root");
    EXPECT_EQ(h.fake->ArgsOf(0),
              (Args{ "-C", "root", "for-each-ref", "--format=%(refname)",
                     "refs/heads", "refs/tags", "refs/remotes" }));
}

TEST(Refs, EmptyRootReturnsEmptyWithoutCallingGit)
{
    auto h = MakeHarness();
    EXPECT_EQ(h.backend->Refs(""), "");
    EXPECT_EQ(h.fake->CallCount(), 0u);
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
}
