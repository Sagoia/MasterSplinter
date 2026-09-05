using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using MasterSplinter.Entrypoint.Git;
using MasterSplinter.Entrypoint.Interop;
using MasterSplinter.Entrypoint.Models;

namespace MasterSplinter.Core.Tests;

/// <summary>
/// <c>GitRepository.ReadFiles</c> -- the host half of every changed-file list (a commit's, a
/// range's, and the working tree's), now that both parsers live in
/// <c>Parse/StatusParser.cpp</c>.
/// <para>
/// What is pinned here is the wire contract, and one guard: the section byte maps onto
/// <see cref="WorkTreeArea"/>, which is a DIFFERENT ordering from the <c>area</c> parameter the
/// worktree-diff export takes. Conflating those two shipped a bug once.
/// </para>
/// </summary>
public class FileUnpackTests
{
    private const ushort KindNameStatus = 5;
    private const ushort KindStatus = 4;
    private const uint RecordSize = 20;

    /// <summary>One packed record, laid out exactly as Parse/StatusParser.h documents it.</summary>
    private static byte[] Record(byte status, byte section, bool isWorkingTree,
                                 (uint Off, uint Len) path, (uint Off, uint Len) oldPath = default)
    {
        var r = new byte[RecordSize];
        r[0] = status;
        r[1] = section;
        r[2] = (byte)(isWorkingTree ? 1 : 0);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(4), path.Off);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(8), path.Len);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(12), oldPath.Off);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(16), oldPath.Len);
        return r;
    }

    private static List<ChangedFile> Read(byte[][] records, string heap, ushort kind = KindStatus)
        => GitRepository.ReadFiles(PackedBuffer.Wrap(PackedTestBuffer.Build(
            kind: kind, recordSize: RecordSize, records: records,
            heap: Encoding.UTF8.GetBytes(heap))));

    [Fact]
    public void TheStatusByteMapsOntoTheHostEnumInDeclarationOrder()
    {
        // The value travels as an integer, so this pins FileChangeStatus against StatusParser.h.
        var files = Read(new[]
        {
            Record(0, 0, false, (0, 1)),
            Record(1, 0, false, (0, 1)),
            Record(2, 0, false, (0, 1)),
            Record(3, 0, false, (0, 1)),
            Record(4, 0, false, (0, 1)),
            Record(5, 0, false, (0, 1)),
        }, "x", KindNameStatus);

        Assert.Equal(FileChangeStatus.Added, files[0].Status);
        Assert.Equal(FileChangeStatus.Modified, files[1].Status);
        Assert.Equal(FileChangeStatus.Deleted, files[2].Status);
        Assert.Equal(FileChangeStatus.Renamed, files[3].Status);
        Assert.Equal(FileChangeStatus.Untracked, files[4].Status);
        Assert.Equal(FileChangeStatus.Conflicted, files[5].Status);
    }

    [Fact]
    public void TheSectionByteMapsOntoWorkTreeAreaInDeclarationOrder()
    {
        var files = Read(new[]
        {
            Record(1, 0, true, (0, 1)),
            Record(1, 1, true, (0, 1)),
            Record(4, 2, true, (0, 1)),
            Record(5, 3, true, (0, 1)),
        }, "x");

        Assert.Equal(WorkTreeArea.Staged, files[0].Area);
        Assert.Equal(WorkTreeArea.Unstaged, files[1].Area);
        Assert.Equal(WorkTreeArea.Untracked, files[2].Area);
        Assert.Equal(WorkTreeArea.Conflicted, files[3].Area);
    }

    [Fact]
    public void TheSectionNumberingIsNotTheAreaParameterNumbering()
    {
        // A GUARD, not a behaviour test. The packed section byte is WorkTreeArea's own order
        // (staged = 0), while the ABI's `area` PARAMETER is 0 = unstaged. Casting the enum to int
        // for that parameter therefore SWAPS staged and unstaged -- which shipped once and was
        // caught only in UI verification. AreaFlag is the mapping; it must never be a plain cast.
        Assert.Equal(0, (int)WorkTreeArea.Staged);
        Assert.Equal(1, GitRepository.AreaFlag(WorkTreeArea.Staged));
        Assert.Equal(0, GitRepository.AreaFlag(WorkTreeArea.Unstaged));
        Assert.NotEqual((int)WorkTreeArea.Staged, GitRepository.AreaFlag(WorkTreeArea.Staged));
    }

    [Fact]
    public void PathsAndOldPathsComeBackFromTheHeap()
    {
        // heap: "new.txt" at 0..7, "old.txt" at 7..14
        var files = Read(new[] { Record(3, 0, true, (0, 7), (7, 7)) }, "new.txtold.txt");

        ChangedFile only = Assert.Single(files);
        Assert.Equal("new.txt", only.Path);
        Assert.Equal("old.txt", only.OldPath);
        // The display form the file list shows for a rename.
        Assert.Equal("old.txt \u2192 new.txt", only.DisplayPath);
    }

    [Fact]
    public void AnEntryWithNoOldPathShowsJustItsPath()
    {
        ChangedFile only = Assert.Single(Read(new[] { Record(1, 0, true, (0, 5)) }, "a.txt"));

        Assert.Equal("", only.OldPath);
        Assert.Equal("a.txt", only.DisplayPath);
    }

    [Fact]
    public void TheWorkingTreeFlagIsCarried()
    {
        Assert.True(Assert.Single(Read(new[] { Record(1, 0, true, (0, 1)) }, "x")).IsWorkingTree);
        Assert.False(Assert.Single(
            Read(new[] { Record(1, 0, false, (0, 1)) }, "x", KindNameStatus)).IsWorkingTree);
    }

    [Fact]
    public void PathsCarryingSeparatorBytesSurviveTheBoundary()
    {
        // A path really can contain these on a POSIX filesystem, and the old pipeline translated
        // NUL to \u001e on the way here -- so a path holding \u001e split the list in two.
        string path = "a\u001fb\u001ec.txt";
        var files = Read(new[] { Record(1, 0, true, (0, (uint)Encoding.UTF8.GetByteCount(path))) }, path);

        Assert.Equal(path, Assert.Single(files).Path);
    }

    [Fact]
    public void AnEmptyBufferYieldsNoFiles()
        => Assert.Empty(Read(System.Array.Empty<byte[]>(), ""));

    [Fact]
    public void AMalformedBufferYieldsNoFilesRatherThanThrowing()
        => Assert.Empty(GitRepository.ReadFiles(PackedBuffer.Empty));

    [Fact]
    public void StatusBucketsEachRecordByItsSection()
    {
        // The whole of GitRepository.Status is now this bucketing, so it is worth pinning that a
        // file appearing in two sections lands in both lists.
        var files = Read(new[]
        {
            Record(1, 0, true, (0, 5)),   // staged   a.txt
            Record(1, 1, true, (0, 5)),   // unstaged a.txt
            Record(4, 2, true, (5, 5)),   // untracked b.txt
            Record(5, 3, true, (10, 5)),  // conflicted c.txt
        }, "a.txtb.txtc.txt");

        Assert.Equal(4, files.Count);
        Assert.Equal(2, files.FindAll(f => f.Path == "a.txt").Count);
        Assert.Contains(files, f => f.Area == WorkTreeArea.Untracked && f.Path == "b.txt");
        Assert.Contains(files, f => f.Area == WorkTreeArea.Conflicted && f.Path == "c.txt");
    }
}
