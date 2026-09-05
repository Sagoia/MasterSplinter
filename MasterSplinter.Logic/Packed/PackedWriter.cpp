// KEEP PORTABLE: no <windows.h>. Compiled with PrecompiledHeader=NotUsing.

#include "PackedWriter.h"

#include <stdexcept>

namespace ms::packed
{
    namespace
    {
        // Explicit little-endian stores. Deliberately not memcpy of an integer: the format has to
        // read identically on a big-endian target, and the host reads it with
        // BinaryPrimitives.ReadUInt32LittleEndian rather than sniffing the architecture.
        void AppendU16(std::string& buf, std::uint16_t v)
        {
            buf.push_back(static_cast<char>(v & 0xFF));
            buf.push_back(static_cast<char>((v >> 8) & 0xFF));
        }

        void AppendU32(std::string& buf, std::uint32_t v)
        {
            buf.push_back(static_cast<char>(v & 0xFF));
            buf.push_back(static_cast<char>((v >> 8) & 0xFF));
            buf.push_back(static_cast<char>((v >> 16) & 0xFF));
            buf.push_back(static_cast<char>((v >> 24) & 0xFF));
        }

        void AppendU64(std::string& buf, std::uint64_t v)
        {
            AppendU32(buf, static_cast<std::uint32_t>(v & 0xFFFFFFFFU));
            AppendU32(buf, static_cast<std::uint32_t>((v >> 32) & 0xFFFFFFFFU));
        }
    }

    PackedWriter::PackedWriter(Kind kind, std::uint32_t recordSize)
        : kind_(kind), recordSize_(recordSize)
    {
        if (recordSize % 4 != 0)
            throw std::logic_error("packed record size must be a multiple of 4");
    }

    void PackedWriter::Pad(std::string& buf, std::size_t alignment)
    {
        while (buf.size() % alignment != 0)
            buf.push_back('\0');
    }

    StringRef PackedWriter::AddString(std::string_view s)
    {
        if (s.empty())
            return StringRef{};

        StringRef ref;
        ref.off = static_cast<std::uint32_t>(heap_.size());
        ref.len = static_cast<std::uint32_t>(s.size());
        heap_.append(s.data(), s.size());
        return ref;
    }

    ArrayRef PackedWriter::AddStringRefs(const std::vector<StringRef>& refs)
    {
        if (refs.empty())
            return ArrayRef{};

        Pad(heap_, 4);

        ArrayRef arr;
        arr.off = static_cast<std::uint32_t>(heap_.size());
        arr.count = static_cast<std::uint32_t>(refs.size());
        for (const StringRef& r : refs)
        {
            AppendU32(heap_, r.off);
            AppendU32(heap_, r.len);
        }
        return arr;
    }

    ArrayRef PackedWriter::AddTaggedRefs(const std::vector<TaggedRef>& items)
    {
        if (items.empty())
            return ArrayRef{};

        Pad(heap_, 4);

        ArrayRef arr;
        arr.off = static_cast<std::uint32_t>(heap_.size());
        arr.count = static_cast<std::uint32_t>(items.size());
        for (const TaggedRef& t : items)
        {
            AppendU32(heap_, t.tag);
            AppendU32(heap_, t.ref.off);
            AppendU32(heap_, t.ref.len);
        }
        return arr;
    }

    void PackedWriter::BeginRecord()
    {
        recordStart_ = records_.size();
        inRecord_ = true;
    }

    void PackedWriter::PutU8(std::uint8_t v) { records_.push_back(static_cast<char>(v)); }
    void PackedWriter::PutU16(std::uint16_t v) { AppendU16(records_, v); }
    void PackedWriter::PutU32(std::uint32_t v) { AppendU32(records_, v); }
    void PackedWriter::PutI32(std::int32_t v) { AppendU32(records_, static_cast<std::uint32_t>(v)); }
    void PackedWriter::PutI64(std::int64_t v) { AppendU64(records_, static_cast<std::uint64_t>(v)); }

    void PackedWriter::PutRef(StringRef r)
    {
        AppendU32(records_, r.off);
        AppendU32(records_, r.len);
    }

    void PackedWriter::PutArray(ArrayRef a)
    {
        AppendU32(records_, a.off);
        AppendU32(records_, a.count);
    }

    void PackedWriter::EndRecord()
    {
        if (!inRecord_)
            throw std::logic_error("EndRecord without BeginRecord");

        const std::size_t written = records_.size() - recordStart_;
        if (written > recordSize_)
            throw std::logic_error("packed record overran its declared size");

        records_.resize(recordStart_ + recordSize_, '\0');
        inRecord_ = false;
        ++count_;
    }

    void PackedWriter::SetFlags(std::uint16_t flags) { flags_ = flags; }

    void PackedWriter::SetExtra(std::string_view bytes)
    {
        extra_.assign(bytes.data(), bytes.size());
        hasExtra_ = true;
    }

    std::string PackedWriter::Finish()
    {
        if (inRecord_)
            throw std::logic_error("Finish with a record still open");

        const std::uint32_t recordsOffset = kHeaderSize;
        const std::uint32_t heapOffset = recordsOffset + static_cast<std::uint32_t>(records_.size());

        // The extra section follows the heap, padded to 4 so its own integer fields stay aligned.
        std::string heap = heap_;
        std::uint32_t extraOffset = 0;
        std::uint32_t extraLen = 0;
        if (hasExtra_)
        {
            Pad(heap, 4);
            extraOffset = heapOffset + static_cast<std::uint32_t>(heap.size());
            extraLen = static_cast<std::uint32_t>(extra_.size());
        }

        const std::uint32_t totalSize =
            heapOffset + static_cast<std::uint32_t>(heap.size()) + extraLen;

        std::string out;
        out.reserve(totalSize);
        AppendU32(out, kMagic);
        AppendU16(out, kVersion);
        AppendU16(out, static_cast<std::uint16_t>(kind_));
        AppendU16(out, static_cast<std::uint16_t>(status_));
        AppendU16(out, flags_);
        AppendU32(out, count_);
        AppendU32(out, recordSize_);
        AppendU32(out, recordsOffset);
        AppendU32(out, heapOffset);
        AppendU32(out, totalSize);
        AppendU32(out, extraOffset);
        AppendU32(out, extraLen);
        AppendU32(out, error_.off);
        AppendU32(out, error_.len);

        out.append(records_);
        out.append(heap);
        if (hasExtra_)
            out.append(extra_);
        return out;
    }

    std::string PackedWriter::Error(Kind kind, std::string_view message)
    {
        // recordSize is nominal here -- there are no records -- but it still has to satisfy the
        // multiple-of-4 rule the constructor enforces.
        PackedWriter w(kind, 4);
        w.status_ = Status::Error;
        w.error_ = w.AddString(message);
        return w.Finish();
    }
}
