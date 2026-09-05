#pragma once

// Builds a packed buffer (see PackedFormat.h). Two growing byte buffers -- the fixed-size record
// table and the variable-length heap -- concatenated behind a header at Finish().
//
// Integers go in field by field through PutU32 and friends rather than by memcpy'ing a struct, so
// the emitted bytes do not depend on the compiler's struct layout or the machine's endianness.
// That is what lets the host read the buffer with a fixed offset table.
//
// KEEP PORTABLE: no <windows.h>. Compiled with PrecompiledHeader=NotUsing.

#include <cstdint>
#include <string>
#include <string_view>
#include <vector>

#include "PackedFormat.h"

namespace ms::packed
{
    class PackedWriter
    {
    public:
        // recordSize must be a multiple of 4 so every record field stays aligned; a record shape
        // that is not is a programming error, not a runtime condition.
        PackedWriter(Kind kind, std::uint32_t recordSize);

        // ---- Heap ------------------------------------------------------------------------------

        // Interns a string. Any byte is legal -- NUL, 0x1F and 0x1E included -- which is the whole
        // point of the format. An empty string interns as {0, 0} without touching the heap.
        StringRef AddString(std::string_view s);

        // Interns an array of string refs (a commit's parent hashes, say) as 8 bytes each. The heap
        // is padded to 4 first so the host can read the pairs without an unaligned load.
        ArrayRef AddStringRefs(const std::vector<StringRef>& refs);

        // ---- Records ---------------------------------------------------------------------------

        void BeginRecord();
        void PutU8(std::uint8_t v);
        void PutU16(std::uint16_t v);
        void PutU32(std::uint32_t v);
        void PutI32(std::int32_t v);
        void PutI64(std::int64_t v);
        void PutRef(StringRef r);   // off then len
        void PutArray(ArrayRef a);  // off then count
        // Zero-pads to recordSize. Writing PAST recordSize throws: the record shape and the
        // declared size have drifted, and silently truncating would misalign every later record.
        void EndRecord();

        // ---- Header ----------------------------------------------------------------------------

        void SetFlags(std::uint16_t flags);
        // The kind-specific trailing section -- Phase E's graph display list rides here.
        void SetExtra(std::string_view bytes);

        std::uint32_t RecordCount() const { return count_; }

        // Emits the whole buffer. std::string is used as a byte container: it is length-carrying
        // and binary-safe, and GitApi's DupBytes already copies by size rather than by strlen.
        std::string Finish();

        // A failed parse: no records, status Error, the message in the heap. This is what replaces
        // OK/ERR framing for packed exports.
        static std::string Error(Kind kind, std::string_view message);

    private:
        void Pad(std::string& buf, std::size_t alignment);
        static void PutU16At(std::string& buf, std::uint16_t v);
        static void PutU32At(std::string& buf, std::uint32_t v);

        Kind kind_;
        std::uint32_t recordSize_;
        std::uint32_t count_ = 0;
        std::uint16_t flags_ = 0;
        Status status_ = Status::Ok;
        StringRef error_{};
        std::size_t recordStart_ = 0;  // offset in records_ where the open record began
        bool inRecord_ = false;

        std::string records_;
        std::string heap_;
        std::string extra_;
        bool hasExtra_ = false;
    };
}
