using System.Buffers.Binary;
using System.Text;
using MasterSplinter.Entrypoint.Git;
using MasterSplinter.Entrypoint.Interop;
using MasterSplinter.Entrypoint.Models;

namespace MasterSplinter.Core.Tests;

/// <summary>
/// <c>GitRepository.ReadDiff</c> -- the host half of the unified diff, now that parsing itself
/// lives in the native core (<c>Parse/DiffParser.cpp</c>, covered by <c>parse_test.cpp</c>).
/// <para>
/// What is left to pin here is the wire contract in both directions: the kind byte must map onto
/// the same <see cref="DiffLineKind"/> the native enum was written against, and -1 must become an
/// empty gutter rather than the text "-1".
/// </para>
/// </summary>
public class DiffUnpackTests
{
    private const ushort KindDiff = 2;
    private const uint RecordSize = 20;
    private const ushort FlagBinary = 0x0001;

    /// <summary>One packed diff record, laid out exactly as Parse/DiffParser.h documents it.</summary>
    private static byte[] Record(byte kind, int oldNo, int newNo, uint textOff, uint textLen)
    {
        var r = new byte[RecordSize];
        r[0] = kind;                                                       // u8  kind   at 0
        BinaryPrimitives.WriteInt32LittleEndian(r.AsSpan(4), oldNo);       // i32 oldNo  at 4
        BinaryPrimitives.WriteInt32LittleEndian(r.AsSpan(8), newNo);       // i32 newNo  at 8
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(12), textOff);   // u32 off    at 12
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(16), textLen);   // u32 len    at 16
        return r;
    }

    private static PackedBuffer Buffer(byte[][] records, string heap = "", ushort flags = 0)
        => PackedBuffer.Wrap(PackedTestBuffer.Build(
            kind: KindDiff, flags: flags, recordSize: RecordSize,
            records: records, heap: Encoding.UTF8.GetBytes(heap)));

    [Fact]
    public void TheKindByteMapsOntoTheHostEnumInDeclarationOrder()
    {
        // The value travels as an integer, so this pins the two enums together. Reordering
        // DiffLineKind without touching DiffParser.h would silently recolour every diff.
        var (lines, _) = GitRepository.ReadDiff(Buffer(new[]
        {
            Record(0, 1, 1, 0, 0),
            Record(1, -1, 1, 0, 0),
            Record(2, 1, -1, 0, 0),
            Record(3, -1, -1, 0, 0),
        }));

        Assert.Equal(DiffLineKind.Context, lines[0].Kind);
        Assert.Equal(DiffLineKind.Added, lines[1].Kind);
        Assert.Equal(DiffLineKind.Removed, lines[2].Kind);
        Assert.Equal(DiffLineKind.Hunk, lines[3].Kind);
    }

    [Fact]
    public void MinusOneBecomesAnEmptyGutterNotTheTextMinusOne()
    {
        var (lines, _) = GitRepository.ReadDiff(Buffer(new[] { Record(1, -1, 7, 0, 0) }));

        Assert.Equal("", lines[0].OldNo);
        Assert.Equal("7", lines[0].NewNo);
    }

    [Fact]
    public void BothGuttersAreFilledForAContextLine()
    {
        var (lines, _) = GitRepository.ReadDiff(Buffer(new[] { Record(0, 12, 34, 0, 0) }));

        Assert.Equal("12", lines[0].OldNo);
        Assert.Equal("34", lines[0].NewNo);
    }

    [Fact]
    public void LineTextComesBackFromTheHeap()
    {
        var (lines, _) = GitRepository.ReadDiff(Buffer(
            new[] { Record(1, -1, 1, 0, 5), Record(1, -1, 2, 5, 6) },
            heap: "alphabravo!"));

        Assert.Equal("alpha", lines[0].Text);
        Assert.Equal("bravo!", lines[1].Text);
    }

    [Fact]
    public void TextCarryingSeparatorBytesSurvivesTheBoundary()
    {
        // Written as \u001f / \u001e, never \x1f: C# hex escapes are greedy, so
        // "\x1fsub" is the single character U+01FS rather than US followed by "sub".
        string hostile = "a\u001fb\u001ec\u0000d";
        byte[] bytes = Encoding.UTF8.GetBytes(hostile);

        var (lines, _) = GitRepository.ReadDiff(Buffer(
            new[] { Record(0, 1, 1, 0, (uint)bytes.Length) }, heap: hostile));

        Assert.Equal(hostile, lines[0].Text);
    }

    [Fact]
    public void TheBinaryHeaderFlagIsSurfaced()
    {
        Assert.True(GitRepository.ReadDiff(Buffer(System.Array.Empty<byte[]>(), flags: FlagBinary)).IsBinary);
        Assert.False(GitRepository.ReadDiff(Buffer(System.Array.Empty<byte[]>())).IsBinary);
    }

    [Fact]
    public void AnEmptyBufferYieldsNoLinesAndIsNotBinary()
    {
        var (lines, isBinary) = GitRepository.ReadDiff(Buffer(System.Array.Empty<byte[]>()));

        Assert.Empty(lines);
        Assert.False(isBinary);
    }

    [Fact]
    public void AMalformedBufferRendersAnEmptyDiffRatherThanThrowing()
    {
        // Unparseable patch text always produced an empty pane; a native bug must not do worse.
        var (lines, isBinary) = GitRepository.ReadDiff(PackedBuffer.Empty);

        Assert.Empty(lines);
        Assert.False(isBinary);
    }
}
