using System;
using System.Buffers.Binary;
using System.Text;

namespace MasterSplinter.Entrypoint.Interop
{
    /// <summary>Which parser produced a packed buffer. Mirrors <c>ms::packed::Kind</c>.</summary>
    internal enum PackedKind : ushort
    {
        None = 0,
        Log = 1,
        Diff = 2,
        Blame = 3,
        Status = 4,
        NameStatus = 5,
        Refs = 6,
        Reflog = 7,
        Stash = 8,
        ShortStat = 9,
    }

    /// <summary>
    /// Reads the packed wire format produced by the native core (see
    /// <c>MasterSplinter.Logic/Packed/PackedFormat.h</c>, which is the authoritative layout).
    /// <para>
    /// Records are fixed-size and therefore directly indexable, so this type reads fields on
    /// demand rather than materialising a model up front. That is the point: a 50k-line diff
    /// costs one array here and zero <see cref="string"/> allocations until something asks for a
    /// line's text.
    /// </para>
    /// <para>
    /// <b>The bytes are managed, not a native pointer.</b> The blob is copied out of native
    /// memory and freed immediately (<c>NativeLogic.TakeBytes</c>). A <c>SafeHandle</c> over the
    /// native allocation would save one memcpy, but a span into it is not protected by the
    /// handle's ref-counting, so any of these buffers outliving a refresh would be a
    /// use-after-free. One memcpy is the cheaper side of that trade.
    /// </para>
    /// </summary>
    internal readonly struct PackedBuffer
    {
        // Header layout. Kept as named constants rather than a struct so the offsets read the same
        // here as they do in PackedFormat.h -- these two tables ARE the contract.
        private const uint Magic = 0x4B50534DU;   // MSPK, little-endian
        private const ushort Version = 1;
        private const int HeaderSize = 48;

        private const int OffMagic = 0;
        private const int OffVersion = 4;
        private const int OffKind = 6;
        private const int OffStatus = 8;
        private const int OffFlags = 10;
        private const int OffRecordCount = 12;
        private const int OffRecordSize = 16;
        private const int OffRecordsOffset = 20;
        private const int OffHeapOffset = 24;
        private const int OffTotalSize = 28;
        private const int OffExtraOffset = 32;
        private const int OffExtraLen = 36;
        private const int OffErrorOff = 40;
        private const int OffErrorLen = 44;

        private readonly byte[]? _bytes;
        private readonly bool _valid;

        private PackedBuffer(byte[]? bytes, bool valid)
        {
            _bytes = bytes;
            _valid = valid;
        }

        /// <summary>An absent buffer: not valid, no records, no error message.</summary>
        public static PackedBuffer Empty => new PackedBuffer(null, false);

        /// <summary>
        /// Wraps bytes returned by a packed export. A malformed or truncated buffer yields an
        /// invalid instance rather than throwing -- every accessor below is then inert, so a
        /// native bug degrades to an empty list instead of taking the app down.
        /// </summary>
        public static PackedBuffer Wrap(byte[]? bytes)
        {
            if (bytes == null || bytes.Length < HeaderSize)
                return Empty;

            var span = new ReadOnlySpan<byte>(bytes);
            if (BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(OffMagic)) != Magic)
                return Empty;
            if (BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(OffVersion)) != Version)
                return Empty;
            if (BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(OffTotalSize)) != (uint)bytes.Length)
                return Empty;

            long records = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(OffRecordsOffset));
            long size = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(OffRecordSize));
            long count = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(OffRecordCount));
            long heap = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(OffHeapOffset));
            if (records + (count * size) > bytes.Length || heap > bytes.Length || heap < records)
                return Empty;

            return new PackedBuffer(bytes, true);
        }

        public bool IsValid => _valid;

        private ReadOnlySpan<byte> Span => _valid ? new ReadOnlySpan<byte>(_bytes) : default;

        private uint Header(int offset) =>
            _valid ? BinaryPrimitives.ReadUInt32LittleEndian(Span.Slice(offset)) : 0u;

        private ushort Header16(int offset) =>
            _valid ? BinaryPrimitives.ReadUInt16LittleEndian(Span.Slice(offset)) : (ushort)0;

        public PackedKind Kind => (PackedKind)Header16(OffKind);

        public ushort Flags => Header16(OffFlags);

        /// <summary>True when the native side reported a failure; see <see cref="ErrorMessage"/>.</summary>
        public bool IsError => !_valid || Header16(OffStatus) != 0;

        /// <summary>The failure message, or empty when the buffer succeeded.</summary>
        public string ErrorMessage => HeapString(Header(OffErrorOff), Header(OffErrorLen));

        public int RecordCount => (int)Header(OffRecordCount);

        /// <summary>The kind-specific trailing section (Phase E graph display list rides here).</summary>
        public ReadOnlySpan<byte> Extra
        {
            get
            {
                uint off = Header(OffExtraOffset), len = Header(OffExtraLen);
                if (!_valid || len == 0 || off + (long)len > _bytes!.Length)
                    return default;
                return Span.Slice((int)off, (int)len);
            }
        }

        // ---- Field access ---------------------------------------------------------------------
        // `field` is a byte offset within the record, matching the per-kind layout comment on the
        // native writer. Out-of-range reads return a zero value rather than throwing, for the same
        // reason Wrap degrades: a native bug should not be able to crash the host.

        private int FieldAt(int record, int field, int width)
        {
            if (!_valid || record < 0 || record >= RecordCount)
                return -1;
            long size = Header(OffRecordSize);
            if (field < 0 || field + width > size)
                return -1;
            long at = Header(OffRecordsOffset) + (record * size) + field;
            return at + width <= _bytes!.Length ? (int)at : -1;
        }

        public byte U8(int record, int field)
        {
            int at = FieldAt(record, field, 1);
            return at < 0 ? (byte)0 : _bytes![at];
        }

        public ushort U16(int record, int field)
        {
            int at = FieldAt(record, field, 2);
            return at < 0 ? (ushort)0 : BinaryPrimitives.ReadUInt16LittleEndian(Span.Slice(at));
        }

        public uint U32(int record, int field)
        {
            int at = FieldAt(record, field, 4);
            return at < 0 ? 0u : BinaryPrimitives.ReadUInt32LittleEndian(Span.Slice(at));
        }

        public int I32(int record, int field)
        {
            int at = FieldAt(record, field, 4);
            return at < 0 ? 0 : BinaryPrimitives.ReadInt32LittleEndian(Span.Slice(at));
        }

        public long I64(int record, int field)
        {
            int at = FieldAt(record, field, 8);
            return at < 0 ? 0L : BinaryPrimitives.ReadInt64LittleEndian(Span.Slice(at));
        }

        /// <summary>Materialises a string field (an off/len pair) from the heap.</summary>
        public string Str(int record, int field)
        {
            int at = FieldAt(record, field, 8);
            if (at < 0)
                return string.Empty;
            return HeapString(BinaryPrimitives.ReadUInt32LittleEndian(Span.Slice(at)),
                              BinaryPrimitives.ReadUInt32LittleEndian(Span.Slice(at + 4)));
        }

        /// <summary>Element count of an array field (an off/count pair).</summary>
        public int ArrayCount(int record, int field)
        {
            int at = FieldAt(record, field, 8);
            return at < 0 ? 0 : (int)BinaryPrimitives.ReadUInt32LittleEndian(Span.Slice(at + 4));
        }

        /// <summary>One element of an array-of-strings field, by index.</summary>
        public string ArrayItem(int record, int field, int index)
        {
            int at = FieldAt(record, field, 8);
            if (at < 0 || index < 0 || index >= ArrayCount(record, field))
                return string.Empty;

            uint arrayOff = BinaryPrimitives.ReadUInt32LittleEndian(Span.Slice(at));
            long entry = Header(OffHeapOffset) + arrayOff + ((long)index * 8);
            if (entry + 8 > _bytes!.Length)
                return string.Empty;

            return HeapString(BinaryPrimitives.ReadUInt32LittleEndian(Span.Slice((int)entry)),
                              BinaryPrimitives.ReadUInt32LittleEndian(Span.Slice((int)entry + 4)));
        }

        /// <summary>
        /// One element of an array-of-TAGGED-strings field: 12 bytes each, {tag, off, len}. Used
        /// for a commit's ref decorations, where each carries its badge kind.
        /// </summary>
        public (uint Tag, string Text) TaggedItem(int record, int field, int index)
        {
            int at = FieldAt(record, field, 8);
            if (at < 0 || index < 0 || index >= ArrayCount(record, field))
                return (0, string.Empty);

            uint arrayOff = BinaryPrimitives.ReadUInt32LittleEndian(Span.Slice(at));
            long entry = Header(OffHeapOffset) + arrayOff + ((long)index * 12);
            if (entry + 12 > _bytes!.Length)
                return (0, string.Empty);

            uint tag = BinaryPrimitives.ReadUInt32LittleEndian(Span.Slice((int)entry));
            return (tag, HeapString(BinaryPrimitives.ReadUInt32LittleEndian(Span.Slice((int)entry + 4)),
                                    BinaryPrimitives.ReadUInt32LittleEndian(Span.Slice((int)entry + 8))));
        }

        /// <summary>All elements of an array-of-strings field.</summary>
        public string[] ArrayItems(int record, int field)
        {
            int count = ArrayCount(record, field);
            if (count <= 0)
                return Array.Empty<string>();

            var items = new string[count];
            for (int i = 0; i < count; i++)
                items[i] = ArrayItem(record, field, i);
            return items;
        }

        // Heap offsets are relative to heapOffset, never absolute, so records stay valid if the
        // header ever grows.
        private string HeapString(uint off, uint len)
        {
            if (!_valid || len == 0)
                return string.Empty;

            long at = Header(OffHeapOffset) + off;
            if (at < 0 || at + len > _bytes!.Length)
                return string.Empty;

            return Encoding.UTF8.GetString(_bytes, (int)at, (int)len);
        }
    }
}
