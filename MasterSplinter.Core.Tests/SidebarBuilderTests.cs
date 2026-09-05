using System;
using System.Collections.Generic;
using System.Linq;
using MasterSplinter.Entrypoint.Git;
using MasterSplinter.Entrypoint.Models;
using MasterSplinter.Entrypoint.ViewModels;

namespace MasterSplinter.Core.Tests;

/// <summary>The sidebar tree: which sections appear, how they nest, and the upstream split.</summary>
public class SidebarBuilderTests
{
    private static BranchInfo Branch(string name, bool current = false, string upstream = "")
        => new($"refs/heads/{name}", name, "sha-" + name, upstream, 0, 0, false, current);

    private static TagInfo Tag(string name) => new($"refs/tags/{name}", name, "sha-" + name, false);

    private static RemoteBranchInfo Remote(string remote, string name)
        => new($"refs/remotes/{remote}/{name}", remote, name, $"sha-{remote}-{name}");

    private static StashEntry Stash(int index, string message)
        => new(index, $"stash@{{{index}}}", "sha", "sho", message, "main", DateTimeOffset.UnixEpoch, "A");

    private static GitRepository.RefList Refs(
        IEnumerable<BranchInfo>? branches = null,
        IEnumerable<TagInfo>? tags = null,
        IEnumerable<RemoteBranchInfo>? remotes = null)
        => new((branches ?? Enumerable.Empty<BranchInfo>()).ToList(),
               (tags ?? Enumerable.Empty<TagInfo>()).ToList(),
               (remotes ?? Enumerable.Empty<RemoteBranchInfo>()).ToList());

    private static List<SidebarItemVM> Build(GitRepository.RefList refs, params StashEntry[] stashes)
        => SidebarBuilder.Build(refs, stashes);

    private static string[] Headers(IEnumerable<SidebarItemVM> items)
        => items.Where(i => i.Kind == SidebarKind.SectionHeader).Select(i => i.Text).ToArray();

    // ---- Sections ------------------------------------------------------------------------------

    [Fact]
    public void FileStatusAndBranchesAlwaysAppear()
    {
        List<SidebarItemVM> items = Build(Refs());

        Assert.Equal(new[] { "FILE STATUS", "BRANCHES" }, Headers(items));
        Assert.Contains(items, i => i.Kind == SidebarKind.WorkingCopy);
        Assert.Contains(items, i => i.Kind == SidebarKind.Reflog);
    }

    [Fact]
    public void EmptySectionsAreOmittedRatherThanShownEmpty()
    {
        string[] headers = Headers(Build(Refs(branches: new[] { Branch("main") })));

        Assert.DoesNotContain("TAGS", headers);
        Assert.DoesNotContain("STASHES", headers);
        Assert.DoesNotContain("REMOTES", headers);
    }

    [Fact]
    public void EverySectionAppearsWhenPopulated()
    {
        List<SidebarItemVM> items = Build(
            Refs(branches: new[] { Branch("main") },
                 tags: new[] { Tag("v1.0") },
                 remotes: new[] { Remote("origin", "main") }),
            Stash(0, "WIP"));

        Assert.Equal(new[] { "FILE STATUS", "BRANCHES", "TAGS", "STASHES", "REMOTES" }, Headers(items));
    }

    // ---- Nesting -------------------------------------------------------------------------------

    [Fact]
    public void RemoteBranchesNestUnderTheirRemoteNode()
    {
        List<SidebarItemVM> items = Build(Refs(remotes: new[]
        {
            Remote("origin", "main"), Remote("origin", "dev"), Remote("upstream", "main"),
        }));

        SidebarItemVM origin = items.Single(i => i.Kind == SidebarKind.Remote && i.Text == "origin");
        List<SidebarItemVM> under = items.Where(i => i.ParentItem == origin).ToList();

        Assert.Equal(1, origin.Level);
        Assert.Equal(2, under.Count);
        Assert.All(under, i => Assert.Equal(2, i.Level));
        Assert.All(under, i => Assert.Equal(SidebarKind.RemoteBranch, i.Kind));
    }

    [Fact]
    public void RemoteBranchesShowTheShortNameButCarryTheQualifiedOne()
    {
        // Displayed as "dev" under the "origin" node, but git needs "origin/dev".
        List<SidebarItemVM> items = Build(Refs(remotes: new[] { Remote("origin", "dev") }));
        SidebarItemVM leaf = items.Single(i => i.Kind == SidebarKind.RemoteBranch);

        Assert.Equal("dev", leaf.Text);
        Assert.Equal("origin/dev", leaf.ShortName);
    }

    [Fact]
    public void RemotesAreGroupedAndOrderedByName()
    {
        List<SidebarItemVM> items = Build(Refs(remotes: new[]
        {
            Remote("upstream", "main"), Remote("origin", "main"),
        }));

        string[] remoteNodes = items.Where(i => i.Kind == SidebarKind.Remote)
                                    .Select(i => i.Text).ToArray();
        Assert.Equal(new[] { "origin", "upstream" }, remoteNodes);
    }

    [Fact]
    public void ARemoteWithNoBranchNameIsSkipped()
    {
        // refs/remotes/origin/HEAD is filtered upstream, but an empty leaf must not render.
        List<SidebarItemVM> items = Build(Refs(remotes: new[] { Remote("origin", "") }));

        Assert.Contains(items, i => i.Kind == SidebarKind.Remote);
        Assert.DoesNotContain(items, i => i.Kind == SidebarKind.RemoteBranch);
    }

    // ---- Selection and stashes -----------------------------------------------------------------

    [Fact]
    public void TheCurrentBranchStartsSelected()
    {
        List<SidebarItemVM> items = Build(Refs(branches: new[]
        {
            Branch("main"), Branch("feature", current: true),
        }));

        SidebarItemVM selected = Assert.Single(items.Where(i => i.IsSelected));
        Assert.Equal("feature", selected.Text);
    }

    [Fact]
    public void DetachedHeadSelectsNothingRatherThanFailing()
    {
        List<SidebarItemVM> items = Build(Refs(branches: new[] { Branch("main"), Branch("dev") }));
        Assert.DoesNotContain(items, i => i.IsSelected);
    }

    [Fact]
    public void StashRowsCarryThePositionalSelector()
    {
        // The selector is what apply/pop/drop take and it is positional, so the index is shown too:
        // git's own "WIP on main" messages read alike.
        List<SidebarItemVM> items = Build(Refs(), Stash(0, "WIP on main"), Stash(1, "WIP on main"));
        List<SidebarItemVM> rows = items.Where(i => i.Kind == SidebarKind.Stash).ToList();

        Assert.Equal(2, rows.Count);
        Assert.Equal("stash@{0}", rows[0].RefName);
        Assert.Equal("stash@{1}", rows[1].RefName);
        Assert.Contains("{0}", rows[0].Text);
        Assert.Contains("{1}", rows[1].Text);
    }

    // ---- Visibility ----------------------------------------------------------------------------

    [Fact]
    public void CollapsingASectionHidesItsWholeSubtree()
    {
        List<SidebarItemVM> items = Build(Refs(remotes: new[] { Remote("origin", "dev") }));
        SidebarItemVM remotesHeader = items.Single(i => i.Text == "REMOTES");

        remotesHeader.IsExpanded = false;
        SidebarBuilder.RecomputeVisibility(items);

        // Both the remote node AND its grandchild branch must go, not just the direct child.
        Assert.False(items.Single(i => i.Kind == SidebarKind.Remote).IsVisible);
        Assert.False(items.Single(i => i.Kind == SidebarKind.RemoteBranch).IsVisible);
        Assert.True(remotesHeader.IsVisible);   // the header itself stays
    }

    [Fact]
    public void CollapsingARemoteHidesOnlyThatRemotesBranches()
    {
        List<SidebarItemVM> items = Build(Refs(remotes: new[]
        {
            Remote("origin", "dev"), Remote("upstream", "main"),
        }));
        SidebarItemVM origin = items.Single(i => i.Kind == SidebarKind.Remote && i.Text == "origin");

        origin.IsExpanded = false;
        SidebarBuilder.RecomputeVisibility(items);

        Assert.False(items.Single(i => i.ShortName == "origin/dev").IsVisible);
        Assert.True(items.Single(i => i.ShortName == "upstream/main").IsVisible);
    }

    // ---- SplitUpstream -------------------------------------------------------------------------

    [Theory]
    [InlineData("origin/main", "origin", "main")]
    [InlineData("origin/feature/x", "origin", "feature/x")]
    public void UpstreamSplitsOnTheKnownRemoteName(string upstream, string remote, string branch)
    {
        var (r, b) = SidebarBuilder.SplitUpstream(upstream, new[] { "origin" });
        Assert.Equal(remote, r);
        Assert.Equal(branch, b);
    }

    [Fact]
    public void TheLongestMatchingRemoteWins()
    {
        // "origin" must not shadow "origin-mirror".
        var (remote, branch) = SidebarBuilder.SplitUpstream(
            "origin-mirror/main", new[] { "origin", "origin-mirror" });

        Assert.Equal("origin-mirror", remote);
        Assert.Equal("main", branch);
    }

    [Fact]
    public void AnUnknownRemoteFallsBackToTheFirstSlash()
    {
        var (remote, branch) = SidebarBuilder.SplitUpstream("whatever/main", Array.Empty<string>());
        Assert.Equal("whatever", remote);
        Assert.Equal("main", branch);
    }

    [Fact]
    public void AnUpstreamWithNoSlashIsAllBranch()
    {
        var (remote, branch) = SidebarBuilder.SplitUpstream("main", new[] { "origin" });
        Assert.Equal("", remote);
        Assert.Equal("main", branch);
    }

    [Fact]
    public void NoUpstreamYieldsEmptyParts()
    {
        var (remote, branch) = SidebarBuilder.SplitUpstream("", new[] { "origin" });
        Assert.Equal("", remote);
        Assert.Equal("", branch);
    }
}
