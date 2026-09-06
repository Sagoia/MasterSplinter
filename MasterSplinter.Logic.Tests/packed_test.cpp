#include "pch.h"

#include <stdexcept>
#include <string>
#include <vector>

#include "Packed/PackedFormat.h"
#include "Packed/PackedWriter.h"

// The packed wire format. These tests exist because the format IS the contract with the host:
// the C# reader indexes records by a fixed offset table, so a shifted field or a stray pad byte
// would misread every record rather than fail loudly.

using ms::packed::ArrayRef;
using ms::packed::Kind;
using ms::packed::PackedWriter;
using ms::packed::Status;
using ms::packed::StringRef;
namespace pk = ms::packed;

namespace
{
    // Little-endian readers mirroring what BinaryPrimitives does on the C# side. Written by hand
    // rather than memcpy'd so a byte-order mistake in the writer cannot be cancelled out by the
    // same mistake in the test.
    std::uint16_t U16(const std::string& b, std::size_t off)
    {
        return static_cast<std::uint16_t>(static_cast<unsigned char>(b[off]) |
                                          (static_cast<unsigned char>(b[off + 1]) << 8));
    }

    std::uint32_t U32(const std::string& b, std::size_t off)
    {
        return static_cast<std::uint32_t>(static_cast<unsigned char>(b[off])) |
               (static_cast<std::uint32_t>(static_cast<unsigned char>(b[off + 1])) << 8) |
               (static_cast<std::uint32_t>(static_cast<unsigned char>(b[off + 2])) << 16) |
               (static_cast<std::uint32_t>(static_cast<unsigned char>(b[off + 3])) << 24);
    }

    // Pulls a heap string back out the way the host will: heap-relative offset plus length.
    std::string HeapString(const std::string& b, std::uint32_t off, std::uint32_t len)
    {
        const std::uint32_t heap = U32(b, pk::kOffHeapOffset);
        return b.substr(heap + off, len);
    }

    std::size_t RecordAt(const std::string& b, std::uint32_t index)
    {
        return U32(b, pk::kOffRecordsOffset) + index * U32(b, pk::kOffRecordSize);
    }

    const char kUs = '\x1f';
    const char kRs = '\x1e';
}

// ---- Header ------------------------------------------------------------------------------------

TEST(Packed, HeaderCarriesMagicVersionKindAndStatus)
{
    PackedWriter w(Kind::Log, 8);
    const std::string b = w.Finish();

    EXPECT_EQ(U32(b, pk::kOffMagic), pk::kMagic);
    EXPECT_EQ(U16(b, pk::kOffVersion), pk::kVersion);
    EXPECT_EQ(U16(b, pk::kOffKind), static_cast<std::uint16_t>(Kind::Log));
    EXPECT_EQ(U16(b, pk::kOffStatus), static_cast<std::uint16_t>(Status::Ok));
}

TEST(Packed, AnEmptyWriterStillProducesAReadableBuffer)
{
    PackedWriter w(Kind::Diff, 4);
    const std::string b = w.Finish();

    EXPECT_EQ(b.size(), pk::kHeaderSize);
    EXPECT_EQ(U32(b, pk::kOffRecordCount), 0u);
    EXPECT_EQ(U32(b, pk::kOffRecordsOffset), pk::kHeaderSize);
    EXPECT_EQ(U32(b, pk::kOffHeapOffset), pk::kHeaderSize);
}

TEST(Packed, TotalSizeMatchesTheEmittedBuffer)
{
    // The host validates *outLen against this field, so a drift here is a truncated read there.
    PackedWriter w(Kind::Log, 8);
    w.BeginRecord();
    w.PutRef(w.AddString("hello"));
    w.EndRecord();

    const std::string b = w.Finish();
    EXPECT_EQ(U32(b, pk::kOffTotalSize), b.size());
}

TEST(Packed, FlagsRoundTrip)
{
    PackedWriter w(Kind::Diff, 4);
    w.SetFlags(0x0005);
    EXPECT_EQ(U16(w.Finish(), pk::kOffFlags), 0x0005u);
}

// ---- Records -----------------------------------------------------------------------------------

TEST(Packed, RecordsAreFixedSizeAndZeroPadded)
{
    // A record shorter than its declared size is padded, not packed tight -- that is what keeps
    // record i addressable at recordsOffset + i*recordSize.
    PackedWriter w(Kind::Log, 16);
    w.BeginRecord();
    w.PutU32(0xAABBCCDDu);
    w.EndRecord();

    const std::string b = w.Finish();
    ASSERT_EQ(U32(b, pk::kOffRecordCount), 1u);
    EXPECT_EQ(U32(b, pk::kOffRecordSize), 16u);
    EXPECT_EQ(U32(b, RecordAt(b, 0)), 0xAABBCCDDu);
    for (std::size_t i = 4; i < 16; ++i)
        EXPECT_EQ(b[RecordAt(b, 0) + i], '\0') << "pad byte " << i;
}

TEST(Packed, MultipleRecordsAreIndexableByStride)
{
    PackedWriter w(Kind::Log, 4);
    for (std::uint32_t i = 0; i < 5; ++i)
    {
        w.BeginRecord();
        w.PutU32(i * 100);
        w.EndRecord();
    }

    const std::string b = w.Finish();
    ASSERT_EQ(U32(b, pk::kOffRecordCount), 5u);
    for (std::uint32_t i = 0; i < 5; ++i)
        EXPECT_EQ(U32(b, RecordAt(b, i)), i * 100);
}

TEST(Packed, IntegersAreStoredLittleEndianRegardlessOfHost)
{
    PackedWriter w(Kind::Log, 4);
    w.BeginRecord();
    w.PutU32(0x01020304u);
    w.EndRecord();

    const std::string b = w.Finish();
    const std::size_t at = RecordAt(b, 0);
    EXPECT_EQ(static_cast<unsigned char>(b[at + 0]), 0x04);
    EXPECT_EQ(static_cast<unsigned char>(b[at + 1]), 0x03);
    EXPECT_EQ(static_cast<unsigned char>(b[at + 2]), 0x02);
    EXPECT_EQ(static_cast<unsigned char>(b[at + 3]), 0x01);
}

TEST(Packed, SignedValuesSurviveTheRoundTrip)
{
    // -1 is how "no such row" travels in the graph adjacency and how an absent line number
    // travels in a diff, so the sign has to survive rather than read back as 4294967295.
    PackedWriter w(Kind::Diff, 16);
    w.BeginRecord();
    w.PutI32(-1);
    w.PutI64(-1234567890123LL);
    w.EndRecord();

    const std::string b = w.Finish();
    const std::size_t at = RecordAt(b, 0);
    EXPECT_EQ(static_cast<std::int32_t>(U32(b, at)), -1);
    const std::uint64_t lo = U32(b, at + 4);
    const std::uint64_t hi = U32(b, at + 8);
    EXPECT_EQ(static_cast<std::int64_t>(lo | (hi << 32)), -1234567890123LL);
}

TEST(Packed, ARecordThatOverrunsItsDeclaredSizeThrows)
{
    // Silently truncating would misalign every later record, so this is a hard failure. It is
    // caught by the ABI guard in GitApi.cpp rather than crossing the boundary.
    PackedWriter w(Kind::Log, 4);
    w.BeginRecord();
    w.PutU32(1);
    w.PutU32(2);
    EXPECT_THROW(w.EndRecord(), std::logic_error);
}

TEST(Packed, ARecordSizeThatWouldMisalignFieldsIsRejected)
{
    EXPECT_THROW(PackedWriter(Kind::Log, 6), std::logic_error);
}

// ---- The heap ----------------------------------------------------------------------------------

TEST(Packed, StringsSurviveEmbeddedNulUnitAndRecordSeparators)
{
    // THE reason this format exists. In the delimited format a 0x1E in a commit body split the
    // record in two and a 0x1F in a subject shifted every later field; both are just bytes here.
    std::string hostile = "sub";
    hostile += kUs;
    hostile += "ject";
    hostile += kRs;
    hostile.append("bo\0dy", 5);

    PackedWriter w(Kind::Log, 8);
    w.BeginRecord();
    w.PutRef(w.AddString(hostile));
    w.EndRecord();

    const std::string b = w.Finish();
    const std::size_t at = RecordAt(b, 0);
    EXPECT_EQ(HeapString(b, U32(b, at), U32(b, at + 4)), hostile);
}

TEST(Packed, AnEmptyStringInternsAsZeroZeroWithoutTouchingTheHeap)
{
    PackedWriter w(Kind::Log, 8);
    const StringRef ref = w.AddString("");
    EXPECT_EQ(ref.off, 0u);
    EXPECT_EQ(ref.len, 0u);

    w.BeginRecord();
    w.PutRef(ref);
    w.EndRecord();

    const std::string b = w.Finish();
    EXPECT_EQ(U32(b, pk::kOffTotalSize), pk::kHeaderSize + 8u);
}

TEST(Packed, SeveralStringsKeepTheirOwnOffsetsAndLengths)
{
    PackedWriter w(Kind::Log, 24);
    const StringRef a = w.AddString("alpha");
    const StringRef empty = w.AddString("");
    const StringRef c = w.AddString("gamma!");
    w.BeginRecord();
    w.PutRef(a);
    w.PutRef(empty);
    w.PutRef(c);
    w.EndRecord();

    const std::string b = w.Finish();
    const std::size_t at = RecordAt(b, 0);
    EXPECT_EQ(HeapString(b, U32(b, at + 0), U32(b, at + 4)), "alpha");
    EXPECT_EQ(U32(b, at + 12), 0u);
    EXPECT_EQ(HeapString(b, U32(b, at + 16), U32(b, at + 20)), "gamma!");
}

TEST(Packed, StringRefArraysAreFourByteAlignedInTheHeap)
{
    // ARM64 tolerates unaligned loads but the host reads these as u32 pairs; padding here is what
    // keeps that a plain aligned read.
    PackedWriter w(Kind::Log, 8);
    w.AddString("odd");  // 3 bytes, leaving the heap misaligned on purpose
    const ArrayRef arr = w.AddStringRefs({ w.AddString("p1"), w.AddString("p2") });

    EXPECT_EQ(arr.off % 4, 0u);
    EXPECT_EQ(arr.count, 2u);
}

TEST(Packed, AnEmptyArrayInternsAsZeroZero)
{
    PackedWriter w(Kind::Log, 8);
    const ArrayRef arr = w.AddStringRefs({});
    EXPECT_EQ(arr.off, 0u);
    EXPECT_EQ(arr.count, 0u);
}

TEST(Packed, ArrayElementsResolveBackToTheirStrings)
{
    // This is the shape a commit's parent list travels in.
    PackedWriter w(Kind::Log, 8);
    const std::vector<StringRef> parents = { w.AddString("aaa"), w.AddString("bbb") };
    const ArrayRef arr = w.AddStringRefs(parents);
    w.BeginRecord();
    w.PutArray(arr);
    w.EndRecord();

    const std::string b = w.Finish();
    const std::size_t at = RecordAt(b, 0);
    const std::uint32_t off = U32(b, at);
    ASSERT_EQ(U32(b, at + 4), 2u);

    const std::uint32_t heap = U32(b, pk::kOffHeapOffset);
    EXPECT_EQ(HeapString(b, U32(b, heap + off + 0), U32(b, heap + off + 4)), "aaa");
    EXPECT_EQ(HeapString(b, U32(b, heap + off + 8), U32(b, heap + off + 12)), "bbb");
}

// ---- Status and the extra section --------------------------------------------------------------

TEST(Packed, ErrorCarriesTheMessageAndNoRecords)
{
    // This replaces OK/ERR framing for packed exports: the failure travels in the same buffer.
    const std::string b = PackedWriter::Error(Kind::Blame, "no such path in that revision");

    EXPECT_EQ(U16(b, pk::kOffStatus), static_cast<std::uint16_t>(Status::Error));
    EXPECT_EQ(U16(b, pk::kOffKind), static_cast<std::uint16_t>(Kind::Blame));
    EXPECT_EQ(U32(b, pk::kOffRecordCount), 0u);
    EXPECT_EQ(HeapString(b, U32(b, pk::kOffErrorOff), U32(b, pk::kOffErrorLen)),
              "no such path in that revision");
}

TEST(Packed, AnErrorMessageMayItselfContainSeparators)
{
    std::string msg = "fatal:";
    msg += kUs;
    msg += kRs;
    msg += "bad";

    const std::string b = PackedWriter::Error(Kind::Log, msg);
    EXPECT_EQ(HeapString(b, U32(b, pk::kOffErrorOff), U32(b, pk::kOffErrorLen)), msg);
}

TEST(Packed, NoExtraSectionMeansOffsetAndLengthAreZero)
{
    const std::string b = PackedWriter(Kind::Log, 4).Finish();
    EXPECT_EQ(U32(b, pk::kOffExtraOffset), 0u);
    EXPECT_EQ(U32(b, pk::kOffExtraLen), 0u);
}

TEST(Packed, TheExtraSectionFollowsTheHeapAndKeepsItsBytes)
{
    // Phase E's graph display list rides here alongside the log records.
    PackedWriter w(Kind::Log, 8);
    w.BeginRecord();
    w.PutRef(w.AddString("odd"));  // misaligns the heap so the pad-to-4 is exercised
    w.EndRecord();
    const std::string extra("\x01\x02\x03\x04", 4);
    w.SetExtra(extra);

    const std::string b = w.Finish();
    const std::uint32_t off = U32(b, pk::kOffExtraOffset);
    const std::uint32_t len = U32(b, pk::kOffExtraLen);

    EXPECT_EQ(off % 4, 0u);
    EXPECT_EQ(len, 4u);
    EXPECT_EQ(b.substr(off, len), extra);
    EXPECT_EQ(U32(b, pk::kOffTotalSize), b.size());
}
