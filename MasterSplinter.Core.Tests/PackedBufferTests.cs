using System.Buffers.Binary;
using System.Text;
using MasterSplinter.Entrypoint.Interop;

namespace MasterSplinter.Core.Tests;

/// <summary>
/// The host half of the packed wire format.
/// <para>
/// Buffers here are built <b>by hand</b> from the documented offsets rather than by calling the
/// native writer. That is deliberate: if both sides were generated from one helper, a layout
/// mistake would cancel itself out and the suite would stay green while the real boundary broke.
/// These bytes are the pin; <c>packed_test.cpp</c> pins the other side against the same table.
/// </para>
/// </summary>
public class PackedBufferTests
{
    private const int HeaderSize = PackedTestBuffer.HeaderSize;

    // Buffers are assembled by hand from the documented offsets -- see PackedTestBuffer.
    private static byte[] Build(
        ushort kind = 1, ushort status = 0, ushort flags = 0, uint recordSize = 8,
        byte[][]? records = null, byte[]? heap = null, byte[]? extra = null,
        uint errorOff = 0, uint errorLen = 0, ushort version = 1)
        => PackedTestBuffer.Build(kind, status, flags, recordSize, records, heap, extra,
                                  errorOff, errorLen, version);

    private static byte[] Pair(uint a, uint b) => PackedTestBuffer.Pair(a, b);

    private static byte[] Concat(params byte[][] parts) => PackedTestBuffer.Concat(parts);

    // ---- Validation -----------------------------------------------------------------------

    [Fact]
    public void AWellFormedBufferIsValid()
    {
        PackedBuffer b = PackedBuffer.Wrap(Build(kind: 1));
        Assert.True(b.IsValid);
        Assert.Equal(PackedKind.Log, b.Kind);
        Assert.False(b.IsError);
    }

    [Fact]
    public void NullOrTruncatedBytesDegradeToAnEmptyBufferRatherThanThrowing()
    {
        // A native bug must not be able to crash the host, so every failure mode here is inert.
        Assert.False(PackedBuffer.Wrap(null).IsValid);
        Assert.False(PackedBuffer.Wrap(System.Array.Empty<byte>()).IsValid);
        Assert.False(PackedBuffer.Wrap(new byte[HeaderSize - 1]).IsValid);
    }

    [Fact]
    public void AWrongMagicIsRejected()
    {
        byte[] bytes = Build();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0), 0xDEADBEEF);
        Assert.False(PackedBuffer.Wrap(bytes).IsValid);
    }

    [Fact]
    public void AFutureVersionIsRejectedRatherThanMisread()
    {
        Assert.False(PackedBuffer.Wrap(Build(version: 2)).IsValid);
    }

    [Fact]
    public void ATotalSizeThatDisagreesWithTheBufferIsRejected()
    {
        // This is the truncation guard: the native side publishes the length it meant to send.
        byte[] bytes = Build();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), (uint)bytes.Length + 16);
        Assert.False(PackedBuffer.Wrap(bytes).IsValid);
    }

    [Fact]
    public void ARecordTableRunningPastTheBufferIsRejected()
    {
        byte[] bytes = Build(records: new[] { Pair(0, 0) });
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 9999);  // recordCount
        Assert.False(PackedBuffer.Wrap(bytes).IsValid);
    }

    [Fact]
    public void AnInvalidBufferReadsAsEmptyOnEveryAccessor()
    {
        PackedBuffer b = PackedBuffer.Empty;
        Assert.Equal(0, b.RecordCount);
        Assert.Equal(0u, b.U32(0, 0));
        Assert.Equal(string.Empty, b.Str(0, 0));
        Assert.Equal(string.Empty, b.ErrorMessage);
        Assert.True(b.Extra.IsEmpty);
        Assert.True(b.IsError);
    }

    // ---- Records --------------------------------------------------------------------------

    [Fact]
    public void RecordsAreIndexedByStride()
    {
        byte[][] records =
        {
            Pair(10, 11),
            Pair(20, 21),
            Pair(30, 31),
        };
        PackedBuffer b = PackedBuffer.Wrap(Build(records: records));

        Assert.Equal(3, b.RecordCount);
        Assert.Equal(10u, b.U32(0, 0));
        Assert.Equal(21u, b.U32(1, 4));
        Assert.Equal(30u, b.U32(2, 0));
    }

    [Fact]
    public void IntegerWidthsReadBackAtTheirOwnOffsets()
    {
        var record = new byte[16];
        record[0] = 0xAB;                                                    // u8  at 0
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(2), 0xBEEF);   // u16 at 2
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(4), -12345);    // i32 at 4
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(8), -9876543210L); // i64 at 8

        PackedBuffer b = PackedBuffer.Wrap(Build(recordSize: 16, records: new[] { record }));
        Assert.Equal(0xAB, b.U8(0, 0));
        Assert.Equal(0xBEEF, b.U16(0, 2));
        Assert.Equal(-12345, b.I32(0, 4));
        Assert.Equal(-9876543210L, b.I64(0, 8));
    }

    [Fact]
    public void OutOfRangeRecordsAndFieldsReadAsZeroRatherThanThrowing()
    {
        PackedBuffer b = PackedBuffer.Wrap(Build(records: new[] { Pair(1, 2) }));

        Assert.Equal(0u, b.U32(5, 0));    // record past the end
        Assert.Equal(0u, b.U32(-1, 0));   // negative record
        Assert.Equal(0u, b.U32(0, 64));   // field past the record
        Assert.Equal(string.Empty, b.Str(0, 64));
    }

    // ---- The heap -------------------------------------------------------------------------

    [Fact]
    public void StringFieldsResolveThroughTheHeap()
    {
        byte[] heap = Encoding.UTF8.GetBytes("alphabeta");
        // Two records pointing into one heap: "alpha" at 0..5, "beta" at 5..9.
        PackedBuffer b = PackedBuffer.Wrap(Build(
            records: new[] { Pair(0, 5), Pair(5, 4) },
            heap: heap));

        Assert.Equal("alpha", b.Str(0, 0));
        Assert.Equal("beta", b.Str(1, 0));
    }

    [Fact]
    public void AZeroLengthStringFieldIsEmpty()
    {
        PackedBuffer b = PackedBuffer.Wrap(Build(
            records: new[] { Pair(0, 0) },
            heap: Encoding.UTF8.GetBytes("ignored")));

        Assert.Equal(string.Empty, b.Str(0, 0));
    }

    [Fact]
    public void StringsCarryingSeparatorBytesSurviveIntact()
    {
        // The whole reason the format exists: in the delimited format a 0x1E in a body split
        // the record in two and a 0x1F in a subject shifted every later field.
        // Separators are written as \u001f / \u001e, never \x1f: C# hex escapes
        // are greedy, so "\x1fsub" is the single character U+01FS, not US followed by "sub".
        string hostile = "sub\u001fject\u001ebo\u0000dy";
        byte[] heap = Encoding.UTF8.GetBytes(hostile);

        PackedBuffer b = PackedBuffer.Wrap(Build(
            records: new[] { Pair(0, (uint)heap.Length) },
            heap: heap));

        Assert.Equal(hostile, b.Str(0, 0));
    }

    [Fact]
    public void MultiByteUtf8IsDecodedByByteLengthNotCharacterCount()
    {
        byte[] heap = Encoding.UTF8.GetBytes("h\u00e9llo\u2192");
        PackedBuffer b = PackedBuffer.Wrap(Build(
            records: new[] { Pair(0, (uint)heap.Length) },
            heap: heap));

        Assert.Equal("h\u00e9llo\u2192", b.Str(0, 0));
    }

    [Fact]
    public void AStringRunningPastTheBufferReadsAsEmpty()
    {
        PackedBuffer b = PackedBuffer.Wrap(Build(
            records: new[] { Pair(0, 9999) },
            heap: Encoding.UTF8.GetBytes("short")));

        Assert.Equal(string.Empty, b.Str(0, 0));
    }

    // ---- Arrays ---------------------------------------------------------------------------

    [Fact]
    public void ArrayFieldsResolveEachElementThroughTheHeap()
    {
        // Heap: "aaabbb" then, 4-aligned, two off/len pairs pointing at them. This is the shape a
        // commit's parent list travels in.
        byte[] strings = Encoding.UTF8.GetBytes("aaabbb");   // 6 bytes
        byte[] pad = new byte[2];                            // align the pair table to 4
        byte[] table = Concat(Pair(0, 3), Pair(3, 3));
        byte[] heap = Concat(strings, pad, table);

        PackedBuffer b = PackedBuffer.Wrap(Build(
            records: new[] { Pair(8, 2) },   // array at heap offset 8, 2 elements
            heap: heap));

        Assert.Equal(2, b.ArrayCount(0, 0));
        Assert.Equal("aaa", b.ArrayItem(0, 0, 0));
        Assert.Equal("bbb", b.ArrayItem(0, 0, 1));
        Assert.Equal(new[] { "aaa", "bbb" }, b.ArrayItems(0, 0));
    }

    [Fact]
    public void AnEmptyArrayYieldsNoElements()
    {
        PackedBuffer b = PackedBuffer.Wrap(Build(records: new[] { Pair(0, 0) }));

        Assert.Equal(0, b.ArrayCount(0, 0));
        Assert.Empty(b.ArrayItems(0, 0));
        Assert.Equal(string.Empty, b.ArrayItem(0, 0, 0));
    }

    [Fact]
    public void AnOutOfRangeArrayIndexIsEmptyRatherThanThrowing()
    {
        byte[] heap = Concat(Encoding.UTF8.GetBytes("aaa"), new byte[1], Pair(0, 3));
        PackedBuffer b = PackedBuffer.Wrap(Build(records: new[] { Pair(4, 1) }, heap: heap));

        Assert.Equal("aaa", b.ArrayItem(0, 0, 0));
        Assert.Equal(string.Empty, b.ArrayItem(0, 0, 1));
        Assert.Equal(string.Empty, b.ArrayItem(0, 0, -1));
    }

    // ---- Status, flags and the extra section ----------------------------------------------

    [Fact]
    public void AnErrorStatusCarriesItsMessage()
    {
        // Packed exports carry status in the header instead of the OK/ERR string framing.
        byte[] heap = Encoding.UTF8.GetBytes("no such path in that revision");
        PackedBuffer b = PackedBuffer.Wrap(Build(
            kind: 3, status: 1, heap: heap, errorOff: 0, errorLen: (uint)heap.Length));

        Assert.True(b.IsError);
        Assert.Equal(PackedKind.Blame, b.Kind);
        Assert.Equal("no such path in that revision", b.ErrorMessage);
        Assert.Equal(0, b.RecordCount);
    }

    [Fact]
    public void ASuccessfulBufferHasNoErrorMessage()
    {
        PackedBuffer b = PackedBuffer.Wrap(Build(records: new[] { Pair(0, 0) }));
        Assert.False(b.IsError);
        Assert.Equal(string.Empty, b.ErrorMessage);
    }

    [Fact]
    public void FlagsAreReadFromTheHeader()
    {
        Assert.Equal(5, PackedBuffer.Wrap(Build(flags: 5)).Flags);
    }

    [Fact]
    public void TheExtraSectionIsExposedAsItsOwnSpan()
    {
        byte[] extra = { 1, 2, 3, 4, 5 };
        PackedBuffer b = PackedBuffer.Wrap(Build(
            records: new[] { Pair(0, 3) },
            heap: Encoding.UTF8.GetBytes("odd"),   // misaligns the heap, exercising the pad
            extra: extra));

        Assert.Equal(extra, b.Extra.ToArray());
        Assert.Equal("odd", b.Str(0, 0));
    }

    [Fact]
    public void NoExtraSectionMeansAnEmptySpan()
    {
        Assert.True(PackedBuffer.Wrap(Build()).Extra.IsEmpty);
    }
}
