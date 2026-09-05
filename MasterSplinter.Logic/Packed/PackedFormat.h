#pragma once

// The packed wire format: one contiguous, length-prefixed buffer carrying parsed git output
// across the C ABI.
//
// WHY THIS EXISTS: the delimited format (fields 0x1F, records 0x1E) cannot represent payloads
// that contain those bytes, and a commit message legitimately can. Length prefixes make every
// byte legal by construction -- NUL, 0x1F and 0x1E included -- so the desync class disappears
// rather than being narrowed. It also lets the host materialise strings lazily: records are
// fixed-size and therefore directly indexable, so a 50k-line diff costs one allocation instead
// of 50k until something actually reads a line.
//
// KEEP PORTABLE: no <windows.h>. Every integer is stored little-endian explicitly (not by
// memcpy of a struct), so the format is byte-identical on x64, ARM64 and whatever macOS runs on,
// and the host can read it with BinaryPrimitives without probing the architecture.

#include <cstddef>
#include <cstdint>

namespace ms::packed
{
    // 'M','S','P','K' as little-endian bytes: 4D 53 50 4B.
    inline constexpr std::uint32_t kMagic = 0x4B50534DU;
    inline constexpr std::uint16_t kVersion = 1;

    // Which parser produced this buffer. The host validates it before reading records, so a
    // mismatched export can never be read as the wrong record shape.
    enum class Kind : std::uint16_t
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
    };

    // Packed exports carry their own status instead of the OK/ERR string framing, so a failure
    // travels with a message and needs no separate convention (see docs/abi.md).
    enum class Status : std::uint16_t
    {
        Ok = 0,
        Error = 1,
    };

    // Header layout, 48 bytes, every field little-endian and 4-byte aligned:
    //
    //    0  u32  magic
    //    4  u16  version
    //    6  u16  kind
    //    8  u16  status
    //   10  u16  flags          kind-specific (e.g. Diff sets bit 0 for a binary file)
    //   12  u32  recordCount
    //   16  u32  recordSize     bytes per record; the host strides by this
    //   20  u32  recordsOffset  byte offset of the record table
    //   24  u32  heapOffset     byte offset of the blob heap
    //   28  u32  totalSize      whole buffer; must equal the export's *outLen
    //   32  u32  extraOffset    kind-specific trailing section, 0 when absent
    //   36  u32  extraLen
    //   40  u32  errorOff       error message, as a heap-relative string ref
    //   44  u32  errorLen
    //
    // recordsOffset is always kHeaderSize; heapOffset is recordsOffset + recordCount*recordSize.
    // Both are multiples of 4 because kHeaderSize is and recordSize is required to be, which is
    // what lets the host read u32 fields without unaligned access on ARM64.
    inline constexpr std::uint32_t kHeaderSize = 48;

    inline constexpr std::size_t kOffMagic = 0;
    inline constexpr std::size_t kOffVersion = 4;
    inline constexpr std::size_t kOffKind = 6;
    inline constexpr std::size_t kOffStatus = 8;
    inline constexpr std::size_t kOffFlags = 10;
    inline constexpr std::size_t kOffRecordCount = 12;
    inline constexpr std::size_t kOffRecordSize = 16;
    inline constexpr std::size_t kOffRecordsOffset = 20;
    inline constexpr std::size_t kOffHeapOffset = 24;
    inline constexpr std::size_t kOffTotalSize = 28;
    inline constexpr std::size_t kOffExtraOffset = 32;
    inline constexpr std::size_t kOffExtraLen = 36;
    inline constexpr std::size_t kOffErrorOff = 40;
    inline constexpr std::size_t kOffErrorLen = 44;

    // A string or byte run living in the heap. `off` is relative to heapOffset, never absolute,
    // so records stay valid if the header ever grows. An empty value is {0, 0}.
    struct StringRef
    {
        std::uint32_t off = 0;
        std::uint32_t len = 0;
    };

    // A counted run in the heap. Element width is implied by the field's documented type.
    struct ArrayRef
    {
        std::uint32_t off = 0;
        std::uint32_t count = 0;
    };

    // A string carrying a small integer tag -- a decoration and its badge kind, say. Stored in
    // the heap as 12 bytes: {tag u32, off u32, len u32}.
    struct TaggedRef
    {
        std::uint32_t tag = 0;
        StringRef ref;
    };

    inline constexpr std::uint32_t kTaggedRefSize = 12;
    inline constexpr std::uint32_t kStringRefSize = 8;
}
