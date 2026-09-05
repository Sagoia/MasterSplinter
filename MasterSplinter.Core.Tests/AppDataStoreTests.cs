using System;
using System.Collections.Generic;
using System.IO;
using MasterSplinter.Entrypoint.Git;
using MasterSplinter.Entrypoint.Models;

namespace MasterSplinter.Core.Tests;

/// <summary>
/// The two preference stores. Testable at all because the directory is injected rather than read
/// from <c>ApplicationData.Current</c> — which is also what let them move out of the WinUI assembly.
/// </summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "ms-store-" + Guid.NewGuid().ToString("N")[..8]);

    public TempDir() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { }
    }
}

public class SettingsStoreTests
{
    [Fact]
    public void AMissingFileYieldsDefaults()
    {
        using var dir = new TempDir();
        AppSettings settings = new SettingsStore(dir.Path).Load();

        Assert.Equal("", settings.EditorCommand);
        Assert.Equal("", settings.MergeTool);
    }

    [Fact]
    public void SettingsRoundTrip()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.Path);

        store.Save(new AppSettings { EditorCommand = "code -g {path}", MergeTool = "vscode" });
        AppSettings loaded = new SettingsStore(dir.Path).Load();   // a fresh instance, as a restart would

        Assert.Equal("code -g {path}", loaded.EditorCommand);
        Assert.Equal("vscode", loaded.MergeTool);
    }

    [Fact]
    public void CorruptJsonYieldsDefaultsRatherThanThrowing()
    {
        // A preference file is a convenience. Failing the operation the user actually asked for
        // because it is unreadable would be worse than starting from defaults.
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "settings.json"), "{ this is not json");

        AppSettings settings = new SettingsStore(dir.Path).Load();

        Assert.Equal("", settings.EditorCommand);
    }

    [Fact]
    public void SavingToAnUnwritableLocationDoesNotThrow()
    {
        // Best effort: an IO failure must not surface as an error to the user.
        var store = new SettingsStore(Path.Combine("Z:", "no-such-drive", "nested"));
        store.Save(new AppSettings { EditorCommand = "x" });   // must simply not throw
    }
}

public class RecentRepositoriesStoreTests
{
    private static RepositoryInfo Repo(string name, string path)
        => new() { Name = name, RootPath = path, Branch = "main" };

    [Fact]
    public void AMissingFileYieldsAnEmptyList()
    {
        using var dir = new TempDir();
        Assert.Empty(new RecentRepositoriesStore(dir.Path).Load());
    }

    [Fact]
    public void AddedRepositoriesComeBackNewestFirst()
    {
        using var dir = new TempDir();
        var store = new RecentRepositoriesStore(dir.Path);

        store.Add(Repo("first", @"C:\repos\first"));
        List<RecentRepository> list = store.Add(Repo("second", @"C:\repos\second"));

        Assert.Equal(2, list.Count);
        Assert.Equal("second", list[0].Name);
        Assert.Equal("first", list[1].Name);
    }

    [Fact]
    public void ReopeningARepositoryMovesItToTheTopWithoutDuplicating()
    {
        using var dir = new TempDir();
        var store = new RecentRepositoriesStore(dir.Path);

        store.Add(Repo("a", @"C:\repos\a"));
        store.Add(Repo("b", @"C:\repos\b"));
        List<RecentRepository> list = store.Add(Repo("a", @"C:\repos\a"));

        Assert.Equal(2, list.Count);
        Assert.Equal("a", list[0].Name);
    }

    [Fact]
    public void PathsDedupeCaseInsensitively()
    {
        // Windows would otherwise show the same repository twice under different casing — exactly
        // the duplicate a recent list must not have.
        using var dir = new TempDir();
        var store = new RecentRepositoriesStore(dir.Path);

        store.Add(Repo("a", @"C:\Repos\Thing"));
        List<RecentRepository> list = store.Add(Repo("a", @"c:\repos\thing"));

        Assert.Single(list);
    }

    [Fact]
    public void TheListIsCappedAndDropsTheOldest()
    {
        using var dir = new TempDir();
        var store = new RecentRepositoriesStore(dir.Path);

        for (int i = 0; i < RecentRepositoriesStore.MaxEntries + 5; i++)
            store.Add(Repo($"r{i}", $@"C:\repos\r{i}"));

        List<RecentRepository> list = store.Load();

        Assert.Equal(RecentRepositoriesStore.MaxEntries, list.Count);
        Assert.Equal($"r{RecentRepositoriesStore.MaxEntries + 4}", list[0].Name);
        Assert.DoesNotContain(list, r => r.Name == "r0");   // the oldest fell off
    }

    [Fact]
    public void EntriesSurviveAcrossInstances()
    {
        using var dir = new TempDir();
        new RecentRepositoriesStore(dir.Path).Add(Repo("kept", @"C:\repos\kept"));

        List<RecentRepository> list = new RecentRepositoriesStore(dir.Path).Load();

        Assert.Single(list);
        Assert.Equal("kept", list[0].Name);
        Assert.NotEqual(default, list[0].LastOpenedUtc);
    }

    [Fact]
    public void CorruptJsonYieldsAnEmptyListRatherThanThrowing()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "recent.json"), "[[[ broken");

        Assert.Empty(new RecentRepositoriesStore(dir.Path).Load());
    }
}
