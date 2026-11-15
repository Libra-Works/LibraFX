#include "liberafx/LivePreview.h"

#include <cmath>
#include <cstring>

namespace liberafx::livepreview {
namespace {
auto read16(const std::uint8_t* p) noexcept -> std::uint16_t
{
    return static_cast<std::uint16_t>(p[0] | (static_cast<std::uint16_t>(p[1]) << 8));
}
auto read32(const std::uint8_t* p) noexcept -> std::uint32_t
{
    auto result = std::uint32_t{0};
    for (auto i = 0; i < 4; ++i) result |= static_cast<std::uint32_t>(p[i]) << (8 * i);
    return result;
}
auto read64(const std::uint8_t* p) noexcept -> std::uint64_t
{
    auto result = std::uint64_t{0};
    for (auto i = 0; i < 8; ++i) result |= static_cast<std::uint64_t>(p[i]) << (8 * i);
    return result;
}
auto readDouble(const std::uint8_t* p) noexcept -> double
{
    auto bits = read64(p);
    auto value = double{};
    static_assert(sizeof(value) == sizeof(bits), "wire requires 64-bit double");
    std::memcpy(&value, &bits, sizeof(value));
    return value;
}
auto validUtf8(const std::uint8_t* data, std::size_t size) noexcept -> bool
{
    for (auto i = std::size_t{0}; i < size;) {
        auto first = data[i++];
        if (first < 0x80) continue;
        auto count = first >= 0xc2 && first <= 0xdf ? 1 :
                     first >= 0xe0 && first <= 0xef ? 2 :
                     first >= 0xf0 && first <= 0xf4 ? 3 : 0;
        if (count == 0 || size - i < static_cast<std::size_t>(count)) return false;
        auto second = data[i];
        if ((first == 0xe0 && second < 0xa0) || (first == 0xed && second >= 0xa0) ||
            (first == 0xf0 && second < 0x90) || (first == 0xf4 && second >= 0x90)) return false;
        for (auto j = 0; j < count; ++j) if ((data[i++] & 0xc0) != 0x80) return false;
    }
    return true;
}
}

auto decodeFrame(const void* data, std::size_t size, FrameView& out) noexcept -> DecodeResult
{
    if (!data || size < headerBytes) return DecodeResult::InvalidFrame;
    auto p = static_cast<const std::uint8_t*>(data);
    if (std::memcmp(p, "LFXP", 4) != 0 || read16(p + 4) != 1) return DecodeResult::InvalidFrame;
    auto kind = read16(p + 6);
    auto length = read32(p + 44);
    auto time = readDouble(p + 24);
    auto rate = readDouble(p + 32);
    auto flags = read32(p + 40);
    auto base = read64(p + 16);
    if (kind < 1 || kind > 5 || length != size - headerBytes || (flags & ~3u) != 0 ||
        !std::isfinite(time) || !std::isfinite(rate) || rate < 0) return DecodeResult::InvalidFrame;
    if ((kind == 1 && (base != 0 || length == 0 || length > maxProgramBytes)) ||
        (kind == 2 && length != 0) ||
        (kind == 3 && (base != 0 || length > maxErrorBytes || !validUtf8(p + headerBytes, length))) ||
        ((kind == 4 || kind == 5) && (read64(p + 8) != 0 || base != 0 || time != 0 || rate != 0 ||
            flags != 0 || length == 0 || length > (kind == 4 ? maxErrorBytes : maxSceneBytes) ||
            !validUtf8(p + headerBytes, length))))
        return DecodeResult::InvalidFrame;
    out = {static_cast<Kind>(kind), read64(p + 8), base, time, rate, flags, p + headerBytes, length};
    return DecodeResult::Ok;
}

auto SequenceCursor::validate(const FrameView& frame) const noexcept -> SequenceResult
{
    if (frame.kind == Kind::Control || frame.kind == Kind::Scene) return SequenceResult::Resync;
    if (hasSequence_ && frame.sequence < sequence_) return SequenceResult::Stale;
    if (frame.kind == Kind::Playback) {
        if (!hasProgram_ || !hasSequence_ || frame.baseSequence != sequence_ ||
            frame.sequence == sequence_) return SequenceResult::Resync;
    }
    return SequenceResult::Accept;
}
auto SequenceCursor::commit(const FrameView& frame) noexcept -> SequenceResult
{
    auto result = validate(frame);
    if (result != SequenceResult::Accept) return result;
    sequence_ = frame.sequence;
    hasSequence_ = true;
    if (frame.kind == Kind::Program) hasProgram_ = true;
    return result;
}
void SequenceCursor::reset() noexcept
{
    sequence_ = 0;
    hasSequence_ = false;
    hasProgram_ = false;
}
}
