#pragma once

// A minimal reader over a packed buffer, for tests only.
//
// packed_test.cpp deliberately does NOT use this -- it reads the format with its own inline
// helpers so the writer and the reader cannot share a mistake. Everything downstream (the parser
// suites) does use it, because by then the format itself is already pinned.

#include <cstdint>
#include <string>

#include "Packed/PackedFormat.h"

namespace mstest
{
    class PackedRead
    {
    public:
        explicit PackedRead(std::string bytes) : b_(std::move(bytes)) {}

        std::uint16_t U16(std::size_t off) const
        {
            return static_cast<std::uint16_t>(Byte(off) | (Byte(off + 1) << 8));
        }

        std::uint32_t U32(std::size_t off) const
        {
            return static_cast<std::uint32_t>(Byte(off)) |
                   (static_cast<std::uint32_t>(Byte(off + 1)) << 8) |
                   (static_cast<std::uint32_t>(Byte(off + 2)) << 16) |
                   (static_cast<std::uint32_t>(Byte(off + 3)) << 24);
        }

        // ---- Header --------------------------------------------------------------------------

        ms::packed::Kind Kind() const
        {
            return static_cast<ms::packed::Kind>(U16(ms::packed::kOffKind));
        }

        bool IsError() const { return U16(ms::packed::kOffStatus) != 0; }
        std::uint16_t Flags() const { return U16(ms::packed::kOffFlags); }
        std::uint32_t Count() const { return U32(ms::packed::kOffRecordCount); }

        std::string Error() const
        {
            return Heap(U32(ms::packed::kOffErrorOff), U32(ms::packed::kOffErrorLen));
        }

        // ---- Records -------------------------------------------------------------------------

        std::uint8_t RecU8(std::uint32_t rec, std::uint32_t field) const
        {
            return Byte(RecordAt(rec) + field);
        }

        std::int32_t RecI32(std::uint32_t rec, std::uint32_t field) const
        {
            return static_cast<std::int32_t>(U32(RecordAt(rec) + field));
        }

        std::int64_t RecI64(std::uint32_t rec, std::uint32_t field) const
        {
            const std::size_t at = RecordAt(rec) + field;
            const std::uint64_t lo = U32(at);
            const std::uint64_t hi = U32(at + 4);
            return static_cast<std::int64_t>(lo | (hi << 32));
        }

        // A string field: an {off, len} pair pointing into the heap.
        std::string RecStr(std::uint32_t rec, std::uint32_t field) const
        {
            const std::size_t at = RecordAt(rec) + field;
            return Heap(U32(at), U32(at + 4));
        }

        const std::string& Bytes() const { return b_; }

    private:
        std::uint8_t Byte(std::size_t off) const
        {
            return off < b_.size() ? static_cast<std::uint8_t>(b_[off]) : 0;
        }

        std::size_t RecordAt(std::uint32_t rec) const
        {
            return U32(ms::packed::kOffRecordsOffset) + (rec * U32(ms::packed::kOffRecordSize));
        }

        std::string Heap(std::uint32_t off, std::uint32_t len) const
        {
            const std::uint32_t heap = U32(ms::packed::kOffHeapOffset);
            if (len == 0 || heap + off + len > b_.size())
                return std::string();
            return b_.substr(heap + off, len);
        }

        std::string b_;
    };
}
