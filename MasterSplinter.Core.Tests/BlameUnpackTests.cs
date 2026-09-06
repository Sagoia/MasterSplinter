using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using MasterSplinter.Entrypoint.Git;
using MasterSplinter.Entrypoint.Interop;
using MasterSplinter.Entrypoint.Models;

namespace MasterSplinter.Core.Tests;

/// <summary>
/// <c>GitRepository.ReadBlame</c> and the parts of <see cref="BlameLine"/> that are host-side
/// derivations. Porcelain parsing itself moved to <c>Parse/BlameParser.cpp</c> and is covered by
/// <c>parse_test.cpp</c>; what is pinned here is the wire contract and the gutter rules.
/// </summary>
public class BlameUnpackTests
{
    private const ushort KindBlame = 3;
    private const uint RecordSize = 72;

    private const string ShaA = "7f3f6795392cb18d48d799d944894d635a5cd95f";

    /// <summary>One packed blame record, laid out exactly as Parse/BlameParser.h documents it.</summary>
    private static byte[] Record(int origLine, int finalLine, long authorTime, int authorTz,
                                 bool isGroupStart, uint shaOff, uint shaLen,
                                 uint authorOff = 0, uint authorLen = 0,
                                 uint emailOff = 0, uint emailLen = 0,
                                 uint summaryOff = 0, uint summaryLen = 0,
                                 uint pathOff = 0, uint pathLen = 0,
                                 uint textOff = 0, uint textLen = 0)
    {
        var r = new byte[RecordSize];
        BinaryPrimitives.WriteInt32LittleEndian(r.AsSpan(0), origLine);
        BinaryPrimitives.WriteInt32LittleEndian(r.AsSpan(4), finalLine);
        BinaryPrimitives.WriteInt64LittleEndian(r.AsSpan(8), authorTime);
        BinaryPrimitives.WriteInt32LittleEndian(r.AsSpan(16), authorTz);
        r[20] = (byte)(isGroupStart ? 1 : 0);
        Ref(r, 24, shaOff, shaLen);
        Ref(r, 32, authorOff, authorLen);
        Ref(r, 40, emailOff, emailLen);
        Ref(r, 48, summaryOff, summaryLen);
        Ref(r, 56, pathOff, pathLen);
        Ref(r, 64, textOff, textLen);
        return r;
    }

    private static void Ref(byte[] r, int at, uint off, uint len)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(at), off);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(at + 4), len);
    }

    private static List<BlameLine> Read(byte[][] records, string heap)
        => GitRepository.ReadBlame(PackedBuffer.Wrap(PackedTestBuffer.Build(
            kind: KindBlame, recordSize: RecordSize,
            records: records, heap: Encoding.UTF8.GetBytes(heap))));

    [Fact]
    public void EveryRecordBecomesOneBlameLineWithItsFields()
    {
        // heap: sha (40) | "Alice" (5) | "a@a" (3) | "first commit" (12) | "f.txt" (5) | "one" (3)
        string heap = ShaA + "Alicea@afirst commitf.txtone";
        var lines = Read(new[]
        {
            Record(1, 1, 1787394695, 7 * 60, true, 0, 40,
                   authorOff: 40, authorLen: 5,
                   emailOff: 45, emailLen: 3,
                   summaryOff: 48, summaryLen: 12,
                   pathOff: 60, pathLen: 5,
                   textOff: 65, textLen: 3),
        }, heap);

        BlameLine only = Assert.Single(lines);
        Assert.Equal(ShaA, only.Sha);
        Assert.Equal("Alice", only.Author);
        Assert.Equal("a@a", only.AuthorEmail);
        Assert.Equal("first commit", only.Summary);
        Assert.Equal("f.txt", only.SourcePath);
        Assert.Equal("one", only.Text);
        Assert.Equal(1, only.OrigLine);
        Assert.Equal(1, only.FinalLine);
        Assert.True(only.IsGroupStart);
    }

    [Fact]
    public void TheTimestampIsRebuiltFromSecondsPlusAMinuteOffset()
    {
        // Building the DateTimeOffset is a .NET concern, so the wire carries only the two numbers.
        BlameLine line = Read(new[] { Record(1, 1, 1787394695, 7 * 60, true, 0, 40) }, ShaA)[0];

        Assert.Equal(TimeSpan.FromHours(7), line.When.Offset);
        Assert.Equal(1787394695, line.When.ToUnixTimeSeconds());
    }

    [Fact]
    public void ANegativeOffsetSurvivesAsANegativeOffset()
    {
        BlameLine line = Read(new[] { Record(1, 1, 1787394695, -(7 * 60 + 30), true, 0, 40) }, ShaA)[0];
        Assert.Equal(-TimeSpan.FromMinutes(7 * 60 + 30), line.When.Offset);
    }

    [Fact]
    public void AZeroOrCorruptAuthorTimeCostsOneTimestampNotTheFile()
    {
        var lines = Read(new[]
        {
            Record(1, 1, 0, 0, true, 0, 40),                       // never set
            Record(2, 2, long.MaxValue, 0, true, 0, 40),           // out of DateTimeOffset range
            Record(3, 3, 1787394695, 99 * 60, true, 0, 40),        // offset beyond +/-14h
        }, ShaA);

        Assert.Equal(3, lines.Count);
        Assert.All(lines, l => Assert.Equal(DateTimeOffset.MinValue, l.When));
    }

    [Fact]
    public void ShortShaIsDerivedFromTheFullSha()
    {
        BlameLine line = Read(new[] { Record(1, 1, 0, 0, true, 0, 40) }, ShaA)[0];
        Assert.Equal(ShaA[..7], line.ShortSha);
    }

    [Fact]
    public void GroupStartsDriveTheGutter()
    {
        var lines = Read(new[]
        {
            Record(1, 1, 1787394695, 0, true, 0, 40, authorOff: 40, authorLen: 5),
            Record(2, 2, 1787394695, 0, false, 0, 40, authorOff: 40, authorLen: 5),
        }, ShaA + "Alice");

        // A continuing line blanks its gutter so a run of lines reads as one block.
        Assert.Equal("Alice", lines[0].GutterAuthor);
        Assert.Equal("", lines[1].GutterAuthor);
        Assert.NotEqual("", lines[0].GutterSha);
        Assert.Equal("", lines[1].GutterSha);
    }

    [Fact]
    public void UncommittedLinesAreFlagged()
    {
        // git uses an all-zero sha for lines that are not committed yet.
        string zeros = new('0', 40);
        BlameLine only = Assert.Single(Read(new[] { Record(1, 1, 1787394695, 0, true, 0, 40) }, zeros));

        Assert.True(only.IsUncommitted);
        Assert.Equal("(working)", only.GutterSha);
        Assert.Equal("", only.GutterDate);   // no date for an uncommitted line
    }

    [Fact]
    public void TextCarryingSeparatorBytesSurvivesTheBoundary()
    {
        // Written as \u001f / \u001e, never \x1f: C# hex escapes are greedy, so
        // "\x1fsub" is the single character U+01FS rather than US followed by "sub".
        string text = "a\u001fb\u001ec";
        byte[] textBytes = Encoding.UTF8.GetBytes(text);

        BlameLine only = Assert.Single(Read(
            new[] { Record(1, 1, 0, 0, true, 0, 40, textOff: 40, textLen: (uint)textBytes.Length) },
            ShaA + text));

        Assert.Equal(text, only.Text);
    }

    [Fact]
    public void AnEmptyBufferProducesNoLines()
        => Assert.Empty(Read(System.Array.Empty<byte[]>(), ""));

    [Fact]
    public void AMalformedBufferProducesNoLinesRatherThanThrowing()
        => Assert.Empty(GitRepository.ReadBlame(PackedBuffer.Empty));
}
