using System.Linq;
using MasterSplinter.Entrypoint.Git;
using MasterSplinter.Entrypoint.Models;

namespace MasterSplinter.Core.Tests;

/// <summary>
/// The bound on retained diffs. Without it, browsing grows memory monotonically — every diff loaded
/// stays reachable through its commit for as long as the log does.
/// </summary>
public class DiffCacheTests
{
    /// <summary>A file with <paramref name="lines"/> of loaded diff, as a real load would leave it.</summary>
    private static ChangedFile Loaded(string path, int lines)
    {
        var file = new ChangedFile { Path = path, DiffLoaded = true };
        for (int i = 0; i < lines; i++)
        {
            file.Diff.Add(new DiffLine { Text = $"line {i}" });
            file.Rows.Add(new DiffRow());
        }
        return file;
    }

    [Fact]
    public void RetainingUnderBudgetKeepsEverything()
    {
        var cache = new DiffCache(maxLines: 100);
        ChangedFile a = Loaded("a", 10), b = Loaded("b", 10);

        cache.Retain(a);
        cache.Retain(b);

        Assert.Equal(2, cache.Count);
        Assert.Equal(20, cache.RetainedLines);
        Assert.True(a.DiffLoaded);
        Assert.True(b.DiffLoaded);
    }

    [Fact]
    public void TheLeastRecentlyUsedIsEvictedFirst()
    {
        var cache = new DiffCache(maxLines: 25);
        ChangedFile oldest = Loaded("oldest", 10);
        ChangedFile middle = Loaded("middle", 10);
        ChangedFile newest = Loaded("newest", 10);

        cache.Retain(oldest);
        cache.Retain(middle);
        cache.Retain(newest);   // 30 > 25, so one has to go

        Assert.False(oldest.DiffLoaded);
        Assert.True(middle.DiffLoaded);
        Assert.True(newest.DiffLoaded);
    }

    [Fact]
    public void EvictionReleasesBothRepresentations()
    {
        // Unified lines AND side-by-side rows — holding one without the other would keep most of
        // the memory while looking evicted.
        var cache = new DiffCache(maxLines: 10);
        ChangedFile evicted = Loaded("old", 10);

        cache.Retain(evicted);
        cache.Retain(Loaded("new", 10));

        Assert.Empty(evicted.Diff);
        Assert.Empty(evicted.Rows);
        Assert.False(evicted.DiffLoaded);
    }

    [Fact]
    public void EvictionFlipsDiffLoadedSoTheFileReloadsOnDemand()
    {
        // The whole scheme depends on this: a released file must look unloaded, or revisiting it
        // shows an empty diff instead of re-reading it.
        var cache = new DiffCache(maxLines: 5);
        ChangedFile file = Loaded("f", 5);

        cache.Retain(file);
        cache.Retain(Loaded("other", 5));

        Assert.False(file.DiffLoaded);
    }

    [Fact]
    public void TheFileJustLoadedIsNeverEvicted()
    {
        // Even a single diff bigger than the whole budget stays: the user is looking at it, and
        // dropping it would reload-and-drop forever.
        var cache = new DiffCache(maxLines: 10);
        ChangedFile huge = Loaded("huge", 5000);

        cache.Retain(huge);

        Assert.True(huge.DiffLoaded);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void RevisitingAFileMakesItMostRecentAgain()
    {
        var cache = new DiffCache(maxLines: 25);
        ChangedFile a = Loaded("a", 10), b = Loaded("b", 10), c = Loaded("c", 10);

        cache.Retain(a);
        cache.Retain(b);
        cache.Retain(a);   // a is touched again, so b becomes the oldest
        cache.Retain(c);

        Assert.True(a.DiffLoaded);
        Assert.False(b.DiffLoaded);
        Assert.True(c.DiffLoaded);
    }

    [Fact]
    public void RetainingTheSameFileTwiceDoesNotDoubleCountIt()
    {
        var cache = new DiffCache(maxLines: 100);
        ChangedFile file = Loaded("f", 10);

        cache.Retain(file);
        cache.Retain(file);

        Assert.Equal(1, cache.Count);
        Assert.Equal(10, cache.RetainedLines);
    }

    [Fact]
    public void ClearReleasesEverything()
    {
        var cache = new DiffCache(maxLines: 1000);
        ChangedFile a = Loaded("a", 10), b = Loaded("b", 10);
        cache.Retain(a);
        cache.Retain(b);

        cache.Clear();

        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.RetainedLines);
        Assert.False(a.DiffLoaded);
        Assert.False(b.DiffLoaded);
        Assert.Empty(a.Diff);
    }

    [Fact]
    public void BrowsingManyCommitsStaysBounded()
    {
        // The scenario that motivated this: 465 diffs across a browsing session used to be retained
        // in full.
        var cache = new DiffCache(maxLines: 1000);
        foreach (int i in Enumerable.Range(0, 465))
            cache.Retain(Loaded($"f{i}", 50));

        Assert.True(cache.RetainedLines <= 1000, $"retained {cache.RetainedLines} lines");
        Assert.True(cache.Count <= 21, $"retained {cache.Count} files");
    }

    [Fact]
    public void ABinaryFileWithNoLinesIsStillTracked()
    {
        // Binary diffs load zero lines but still occupy a slot; they must not confuse the budget.
        var cache = new DiffCache(maxLines: 10);
        var binary = new ChangedFile { Path = "img.png", DiffLoaded = true, IsBinary = true };

        cache.Retain(binary);

        Assert.Equal(1, cache.Count);
        Assert.Equal(0, cache.RetainedLines);
        Assert.True(binary.DiffLoaded);
    }
}
