#pragma once

// Changed-file parsing: git's NUL-separated path streams in, a packed buffer out.
//
// Two shapes, one record layout:
//   * --name-status (a commit's or a range's file list): the status and each path are SEPARATE
//     tokens -- one path normally, two (old then new) for R/C.
//   * --porcelain=v1 (the working tree): "XY <path>" is packed into ONE token, and a rename is
//     followed by a second token holding the original path.
//
// Both are read straight off git's NUL-separated output. Nothing translates NUL to 0x1E on the
// way any more: a path may legitimately contain 0x1E, and translating would reintroduce exactly
// the desync class Phase D exists to remove.
//
// KEEP PORTABLE: no <windows.h>. Compiled with PrecompiledHeader=NotUsing.

#include <cstdint>
#include <string>
#include <string_view>

namespace ms::parse
{
    // MUST match the host's Models/Diff.cs FileChangeStatus declaration order.
    enum class FileChangeStatus : std::uint8_t
    {
        Added = 0,
        Modified = 1,
        Deleted = 2,
        Renamed = 3,
        Untracked = 4,
        Conflicted = 5,
    };

    // Which working-tree section an entry belongs to. MUST match the host's WorkTreeArea
    // declaration order.
    //
    // TRAP: this is NOT the numbering MsGitWorkTreeFileDiff's `area` PARAMETER uses. That one is
    // 0=unstaged, 1=staged, 2=untracked, and the host must keep mapping it through
    // GitRepository.AreaFlag rather than casting. Two different orderings for the word "area" is
    // how this ABI shipped a bug once; both are pinned by tests. See docs/abi.md.
    enum class StatusSection : std::uint8_t
    {
        Staged = 0,
        Unstaged = 1,
        Untracked = 2,
        Conflicted = 3,
    };

    // Record layout for one changed file. 20 bytes.
    //
    //    0  u8  status          FileChangeStatus
    //    1  u8  section         StatusSection (meaningless unless isWorkingTree)
    //    2  u8  isWorkingTree
    //    3  u8  (reserved)
    //    4  {off,len} path      for a rename/copy this is the NEW path, which is what diff and
    //                           show must address
    //   12  {off,len} oldPath   empty unless this entry is a rename/copy
    inline constexpr std::uint32_t kFileRecordSize = 20;
    inline constexpr std::uint32_t kFileOffStatus = 0;
    inline constexpr std::uint32_t kFileOffSection = 1;
    inline constexpr std::uint32_t kFileOffIsWorkingTree = 2;
    inline constexpr std::uint32_t kFileOffPath = 4;
    inline constexpr std::uint32_t kFileOffOldPath = 12;

    // git diff --name-status -z. Yields Kind::NameStatus.
    std::string ParseNameStatus(std::string_view nulSeparated);

    // git status --porcelain=v1 -z. Yields Kind::Status.
    //
    // A file that is both staged and modified again ("MM") produces TWO records, one per section;
    // a conflicted file produces exactly one, in its own section. That split has to happen before
    // the staged/unstaged one, or a "UU" file is listed twice with no hint anything is wrong.
    std::string ParsePorcelainStatus(std::string_view nulSeparated);

    // The status letter git uses in both formats.
    FileChangeStatus MapStatus(char c);

    // MERGE-003. The seven porcelain-v1 code pairs meaning "unmerged", per git's own list:
    // DD (both deleted), AU (added by us), UD (deleted by them), UA (added by them),
    // DU (deleted by us), AA (both added), UU (both modified).
    bool IsUnmerged(char x, char y);
}
