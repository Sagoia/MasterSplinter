// KEEP PORTABLE: no <windows.h>. Compiled with PrecompiledHeader=NotUsing.

#include "GraphLayout.h"

#include <algorithm>

namespace ms::graph
{
    namespace
    {
        constexpr std::int32_t kEmpty = -1;

        // One drawn segment, before it is flattened into bytes.
        struct Segment
        {
            std::uint8_t x1, y1, x2, y2, color;
        };

        void AppendU32(std::string& out, std::uint32_t v)
        {
            out.push_back(static_cast<char>(v & 0xFF));
            out.push_back(static_cast<char>((v >> 8) & 0xFF));
            out.push_back(static_cast<char>((v >> 16) & 0xFF));
            out.push_back(static_cast<char>((v >> 24) & 0xFF));
        }

        std::uint8_t Clamp(std::size_t v)
        {
            return static_cast<std::uint8_t>(std::min<std::size_t>(v, 255));
        }

        // The active-lane table. A slot holds the row index it is waiting for, or kEmpty.
        class Lanes
        {
        public:
            // The lane a row sits on: the leftmost one already waiting for it, or a fresh slot
            // when nothing is (a branch tip, or a root reached from --all).
            std::size_t Claim(std::int32_t row, std::vector<std::size_t>& joins)
            {
                joins.clear();
                std::size_t dot = kNone;
                for (std::size_t i = 0; i < slots_.size(); ++i)
                {
                    if (slots_[i] != row)
                        continue;
                    if (dot == kNone)
                        dot = i;
                    else
                        joins.push_back(i);
                }
                if (dot == kNone)
                    dot = Allocate();
                return dot;
            }

            // A lane already waiting for this row, or a fresh one. Reusing is what makes two
            // commits that share a parent converge onto one line instead of drawing two.
            std::size_t ClaimForParent(std::int32_t row)
            {
                for (std::size_t i = 0; i < slots_.size(); ++i)
                {
                    if (slots_[i] == row)
                        return i;
                }
                return Allocate();
            }

            // A lane strictly LEFT of `before` that is already heading for `row`, or kNone.
            //
            // This is what keeps the graph narrow. Without it every branch tip holds its own lane
            // all the way down to its parent, so a repository with a few dozen stale branches
            // fans out into a wall of parallel lines. git collapses the lane as soon as it can see
            // both are heading for the same commit, and so does this. Leftward only: pulling a
            // long-running lane rightward onto a tip would move the mainline around instead.
            std::size_t LeftLaneHeadingFor(std::int32_t row, std::size_t before) const
            {
                for (std::size_t i = 0; i < before && i < slots_.size(); ++i)
                {
                    if (slots_[i] == row)
                        return i;
                }
                return kNone;
            }

            std::size_t Allocate()
            {
                for (std::size_t i = 0; i < slots_.size(); ++i)
                {
                    if (slots_[i] == kEmpty)
                    {
                        colors_[i] = NextColor();
                        return i;
                    }
                }
                if (slots_.size() >= kMaxLanes)
                    return slots_.empty() ? 0 : slots_.size() - 1;   // saturate rather than grow
                slots_.push_back(kEmpty);
                colors_.push_back(NextColor());
                return slots_.size() - 1;
            }

            void Set(std::size_t lane, std::int32_t row)
            {
                if (lane < slots_.size())
                    slots_[lane] = row;
            }

            void Free(std::size_t lane) { Set(lane, kEmpty); }

            bool Active(std::size_t lane) const
            {
                return lane < slots_.size() && slots_[lane] != kEmpty;
            }

            std::uint8_t Color(std::size_t lane) const
            {
                return lane < colors_.size() ? colors_[lane] : 0;
            }

            // Trims trailing empty slots so laneCount reflects what is actually drawn.
            std::size_t Width() const
            {
                std::size_t width = 0;
                for (std::size_t i = 0; i < slots_.size(); ++i)
                {
                    if (slots_[i] != kEmpty)
                        width = i + 1;
                }
                return width;
            }

            static constexpr std::size_t kNone = static_cast<std::size_t>(-1);

        private:
            // Cycled per allocation rather than derived from the lane index, so a branch keeps
            // one colour for its whole life -- which is the thing a reader actually follows.
            std::uint8_t NextColor()
            {
                const std::uint8_t c = next_;
                next_ = static_cast<std::uint8_t>((next_ + 1) % kColorCount);
                return c;
            }

            std::vector<std::int32_t> slots_;
            std::vector<std::uint8_t> colors_;
            std::uint8_t next_ = 0;
        };
    }

    std::string BuildDisplayList(const std::vector<std::vector<std::int32_t>>& adjacency,
                                 bool oldestFirst)
    {
        std::string out;
        AppendU32(out, static_cast<std::uint32_t>(adjacency.size()));

        Lanes lanes;
        std::vector<std::size_t> joins;
        std::vector<Segment> segments;
        std::vector<std::size_t> rowOffsets;
        if (oldestFirst)
            rowOffsets.reserve(adjacency.size());

        for (std::size_t step = 0; step < adjacency.size(); ++step)
        {
            // Lane state always advances from children to parents, regardless of display order.
            const std::size_t row = oldestFirst ? adjacency.size() - 1 - step : step;
            if (oldestFirst)
                rowOffsets.push_back(out.size());
            const std::int32_t here = static_cast<std::int32_t>(row);
            segments.clear();

            const std::size_t dot = lanes.Claim(here, joins);
            const std::uint8_t dotColor = lanes.Color(dot);
            const bool hadIncoming = lanes.Active(dot);

            // Every lane that is not part of this row passes straight through it.
            const std::size_t widthBefore = lanes.Width();
            for (std::size_t l = 0; l < widthBefore; ++l)
            {
                if (!lanes.Active(l) || l == dot)
                    continue;
                if (std::find(joins.begin(), joins.end(), l) != joins.end())
                    continue;
                segments.push_back({ Clamp(l), kTop, Clamp(l), kBottom, lanes.Color(l) });
            }

            // Lanes that were waiting for this row arrive at its dot. The leftmost is the dot's
            // own lane and comes in vertically; the rest are joins and come in diagonally.
            if (hadIncoming)
                segments.push_back({ Clamp(dot), kTop, Clamp(dot), kCentre, dotColor });
            for (std::size_t j : joins)
            {
                segments.push_back({ Clamp(j), kTop, Clamp(dot), kCentre, lanes.Color(j) });
                lanes.Free(j);
            }

            // The dot's lane is consumed here; the first parent re-claims it below.
            lanes.Free(dot);

            const std::vector<std::int32_t>& parents = adjacency[row];
            std::uint8_t flags = 0;
            if (parents.size() > 1)
                flags |= kFlagMerge;
            if (parents.empty())
                flags |= kFlagRoot;

            for (std::size_t k = 0; k < parents.size(); ++k)
            {
                const std::int32_t parent = parents[k];
                if (parent < 0)
                {
                    // Outside the loaded window. The history really does continue, so the line
                    // leaves the bottom of the row -- it just has nowhere to land.
                    flags |= kFlagBoundary;
                    if (k == 0)
                        segments.push_back({ Clamp(dot), kCentre, Clamp(dot), kBottom, dotColor });
                    continue;
                }

                if (k == 0)
                {
                    const std::size_t merged = lanes.LeftLaneHeadingFor(parent, dot);
                    if (merged != Lanes::kNone)
                    {
                        // Something to the left is already going where this commit is going, so
                        // fold into it now rather than running two lines to the same dot.
                        segments.push_back({ Clamp(dot), kCentre, Clamp(merged), kBottom,
                                             lanes.Color(merged) });
                    }
                    else
                    {
                        lanes.Set(dot, parent);
                        segments.push_back({ Clamp(dot), kCentre, Clamp(dot), kBottom, dotColor });
                    }
                }
                else
                {
                    // A merge parent forks off to its own lane -- or joins one already heading
                    // for the same commit.
                    const std::size_t lane = lanes.ClaimForParent(parent);
                    lanes.Set(lane, parent);
                    segments.push_back({ Clamp(dot), kCentre, Clamp(lane), kBottom,
                                         lanes.Color(lane) });
                }
            }

            // laneCount has to cover the lanes drawn on this row, which includes the dot even
            // when its lane ends here, and any lane a parent just claimed.
            std::size_t laneCount = std::max(widthBefore, lanes.Width());
            laneCount = std::max(laneCount, dot + 1);
            for (const Segment& s : segments)
                laneCount = std::max<std::size_t>(laneCount, std::max(s.x1, s.x2) + 1u);

            out.push_back(static_cast<char>(Clamp(laneCount)));
            out.push_back(static_cast<char>(Clamp(dot)));
            out.push_back(static_cast<char>(dotColor));
            out.push_back(static_cast<char>(flags));
            out.push_back(static_cast<char>(Clamp(std::min(segments.size(), kMaxSegments))));

            const std::size_t emit = std::min(segments.size(), kMaxSegments);
            for (std::size_t i = 0; i < emit; ++i)
            {
                const Segment& s = segments[i];
                out.push_back(static_cast<char>(s.x1));
                out.push_back(static_cast<char>(oldestFirst ? kBottom - s.y1 : s.y1));
                out.push_back(static_cast<char>(s.x2));
                out.push_back(static_cast<char>(oldestFirst ? kBottom - s.y2 : s.y2));
                out.push_back(static_cast<char>(s.color));
            }
        }

        if (oldestFirst)
        {
            // Rows have variable lengths, so reverse whole row spans, keeping their headers and
            // segments together. Their coordinates were already mirrored during emission.
            std::string reversed;
            reversed.reserve(out.size());
            AppendU32(reversed, static_cast<std::uint32_t>(adjacency.size()));
            std::size_t end = out.size();
            for (auto it = rowOffsets.rbegin(); it != rowOffsets.rend(); ++it)
            {
                reversed.append(out, *it, end - *it);
                end = *it;
            }
            return reversed;
        }
        return out;
    }
}
