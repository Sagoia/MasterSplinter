using System.Buffers.Binary;

namespace MasterSplinter.Core.Tests;

/// <summary>
/// Assembles packed buffers <b>by hand</b> from the layout documented in
/// <c>MasterSplinter.Logic/Packed/PackedFormat.h</c>.
/// <para>
/// Deliberately not a port of the native <c>PackedWriter</c>: if the test buffers were produced
/// by the same logic the reader is checked against, a layout mistake would cancel itself out and
/// the suite would stay green while the real boundary broke. This spells the offsets out, so it
/// is the pin.
/// </para>
/// </summary>
internal static class PackedTestBuffer
{
    public const int HeaderSize = 48;
    public const uint Magic = 0x4B50534DU;   // MSPK, little-endian

    /// <summary>
    /// Builds a buffer. <paramref name="records"/> are zero-padded to
    /// <paramref name="recordSize"/>; offsets inside them are heap-relative.
    /// </summary>
    public static byte[] Build(
        ushort kind = 1,
        ushort status = 0,
        ushort flags = 0,
        uint recordSize = 8,
        byte[][]? records = null,
        byte[]? heap = null,
        byte[]? extra = null,
        uint errorOff = 0,
        uint errorLen = 0,
        ushort version = 1)
    {
        records ??= System.Array.Empty<byte[]>();
        heap ??= System.Array.Empty<byte>();

        var recordBytes = new List<byte>();
        foreach (byte[] r in records)
        {
            var padded = new byte[recordSize];
            System.Array.Copy(r, padded, r.Length);
            recordBytes.AddRange(padded);
        }

        uint recordsOffset = HeaderSize;
        uint heapOffset = recordsOffset + (uint)recordBytes.Count;

        var heapBytes = new List<byte>(heap);
        uint extraOffset = 0, extraLen = 0;
        if (extra != null)
        {
            while (heapBytes.Count % 4 != 0)
                heapBytes.Add(0);
            extraOffset = heapOffset + (uint)heapBytes.Count;
            extraLen = (uint)extra.Length;
        }

        uint totalSize = heapOffset + (uint)heapBytes.Count + extraLen;

        var buf = new byte[totalSize];
        var span = buf.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span[0..], Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], version);
        BinaryPrimitives.WriteUInt16LittleEndian(span[6..], kind);
        BinaryPrimitives.WriteUInt16LittleEndian(span[8..], status);
        BinaryPrimitives.WriteUInt16LittleEndian(span[10..], flags);
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], (uint)records.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], recordSize);
        BinaryPrimitives.WriteUInt32LittleEndian(span[20..], recordsOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..], heapOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(span[28..], totalSize);
        BinaryPrimitives.WriteUInt32LittleEndian(span[32..], extraOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(span[36..], extraLen);
        BinaryPrimitives.WriteUInt32LittleEndian(span[40..], errorOff);
        BinaryPrimitives.WriteUInt32LittleEndian(span[44..], errorLen);

        recordBytes.CopyTo(buf, (int)recordsOffset);
        heapBytes.CopyTo(buf, (int)heapOffset);
        extra?.CopyTo(buf, (int)extraOffset);
        return buf;
    }

    /// <summary>An off/len or off/count pair, as a record field holds it.</summary>
    public static byte[] Pair(uint a, uint b)
    {
        var p = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(0), a);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(4), b);
        return p;
    }

    public static byte[] Concat(params byte[][] parts)
    {
        var all = new List<byte>();
        foreach (byte[] p in parts)
            all.AddRange(p);
        return all.ToArray();
    }
}
