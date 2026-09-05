using MasterSplinter.Entrypoint.Git;
using MasterSplinter.Entrypoint.Models;

namespace MasterSplinter.Core.Tests;

/// <summary>
/// The small mappings and framings that sit right on the ABI boundary. Small, but two of them have
/// already cost real bugs, so they are pinned rather than trusted.
/// <para>
/// Separators are written \u001f, never \x1f: C# hex escapes are GREEDY, so
/// "\x1ffatal" is the single char U+01FF rather than US followed by 'f'.
/// \u is fixed-width and cannot do that. (Same family as the for-each-ref %xx vs
/// log %xNN trap in docs/abi.md.)
/// </para>
/// </summary>
public class GitContractTests
{
    // ---- The area contract -------------------------------------------------------------------
    // MsGitWorkTreeFileDiff takes 0=unstaged, 1=staged, 2=untracked, but the C# enum declares
    // Staged FIRST. Casting the enum straight to int therefore SWAPS staged and unstaged — which
    // shipped once, and was only caught when a staged file displayed the unstaged diff.

    [Theory]
    [InlineData(WorkTreeArea.Unstaged, 0)]
    [InlineData(WorkTreeArea.Staged, 1)]
    [InlineData(WorkTreeArea.Untracked, 2)]
    public void AreaFlagMatchesTheAbiNumbering(WorkTreeArea area, int expected)
        => Assert.Equal(expected, GitRepository.AreaFlag(area));

    [Fact]
    public void AreaFlagIsNotJustTheEnumValue()
    {
        // The guard that would have caught the shipped bug: if these ever agree, someone has
        // "simplified" AreaFlag into a cast.
        Assert.NotEqual((int)WorkTreeArea.Staged, GitRepository.AreaFlag(WorkTreeArea.Staged));
        Assert.NotEqual((int)WorkTreeArea.Unstaged, GitRepository.AreaFlag(WorkTreeArea.Unstaged));
    }

    [Theory]
    [InlineData(WhitespaceMode.None, 0)]
    [InlineData(WhitespaceMode.IgnoreChange, 1)]
    [InlineData(WhitespaceMode.IgnoreAll, 2)]
    public void WsFlagMatchesTheAbiNumbering(WhitespaceMode mode, int expected)
        => Assert.Equal(expected, GitRepository.WsFlag(mode));

    // ---- OK / ERR framing --------------------------------------------------------------------

    [Fact]
    public void OkMeansSuccess() => Assert.Null(GitRepository.ParseOkErr("OK"));

    [Fact]
    public void OkWithTrailingFieldsIsStillSuccess()
        => Assert.Null(GitRepository.ParseOkErr("OK\u001fsubject\u001fbody"));

    [Fact]
    public void ErrCarriesGitsMessage()
        => Assert.Equal("fatal: bad ref", GitRepository.ParseOkErr("ERR\u001ffatal: bad ref"));

    [Fact]
    public void ErrMessageMayContainTheFieldSeparator()
    {
        // Split(US, 2) — only the FIRST separator delimits, so git output containing 0x1F survives.
        Assert.Equal("a\u001fb", GitRepository.ParseOkErr("ERR\u001fa\u001fb"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ERR")]
    [InlineData("ERR\u001f")]
    public void UnframedOrEmptyOutputFallsBackToAReadableMessage(string raw)
    {
        string? result = GitRepository.ParseOkErr(raw);
        Assert.NotNull(result);
        Assert.Contains("git", result, System.StringComparison.OrdinalIgnoreCase);
    }

    // ---- The WinUI bare-CR trap ---------------------------------------------------------------
    // A multi-line WinUI TextBox reports line breaks as a bare \r. Passed to git unchanged, a
    // two-line commit message becomes ONE line with an embedded CR.

    [Fact]
    public void BareCarriageReturnsBecomeNewlines()
        => Assert.Equal("subject\n\nbody", GitRepository.NormalizeMessage("subject\r\rbody"));

    [Fact]
    public void CrLfCollapsesToASingleNewline()
        => Assert.Equal("subject\n\nbody", GitRepository.NormalizeMessage("subject\r\n\r\nbody"));

    [Fact]
    public void PlainNewlinesAreLeftAlone()
        => Assert.Equal("a\nb", GitRepository.NormalizeMessage("a\nb"));

    [Fact]
    public void NormalizeIsIdempotent()
    {
        string once = GitRepository.NormalizeMessage("a\r\nb\rc");
        Assert.Equal(once, GitRepository.NormalizeMessage(once));
    }

    // ---- Stash subjects ------------------------------------------------------------------------

    [Theory]
    [InlineData("WIP on main: 1a2b3c4 tidy up", "main", "1a2b3c4 tidy up")]
    [InlineData("On feature/x: my message", "feature/x", "my message")]
    public void StashSubjectSplitsIntoBranchAndMessage(string subject, string branch, string message)
    {
        var (b, m) = GitRepository.SplitStashSubject(subject);
        Assert.Equal(branch, b);
        Assert.Equal(message, m);
    }

    [Fact]
    public void UnrecognisedStashSubjectIsKeptWhole()
    {
        var (branch, message) = GitRepository.SplitStashSubject("something else entirely");
        Assert.Equal("", branch);
        Assert.Equal("something else entirely", message);
    }

}
