#pragma once

#include <cstddef>
#include <cstdint>

#ifndef LIBRA_RUNTIME_CORE_API
#define LIBRA_RUNTIME_CORE_API
#endif

namespace liberafx::livepreview {

constexpr std::size_t headerBytes = 48;
constexpr std::size_t maxProgramBytes = 128u * 1024u * 1024u;
constexpr std::size_t maxErrorBytes = 4096;

enum class Kind : std::uint16_t { Program = 1, Playback = 2, CompileError = 3 };
enum class DecodeResult : std::uint8_t { Ok, InvalidFrame };
enum class SequenceResult : std::uint8_t { Accept, Stale, Resync };

// Payload is borrowed from the input buffer and is valid only while that buffer lives.
struct FrameView final {
    Kind kind{};
    std::uint64_t sequence = 0;
    std::uint64_t baseSequence = 0;
    double timeSeconds = 0;
    double playbackRate = 0;
    std::uint32_t flags = 0;
    const std::uint8_t* payload = nullptr;
    std::size_t payloadLength = 0;
};

LIBRA_RUNTIME_CORE_API auto decodeFrame(const void* data, std::size_t size, FrameView& out) noexcept -> DecodeResult;

// Validate before handling; commit only after the adapter has accepted the frame.
class LIBRA_RUNTIME_CORE_API SequenceCursor final {
public:
    auto validate(const FrameView& frame) const noexcept -> SequenceResult;
    auto commit(const FrameView& frame) noexcept -> SequenceResult;
    void reset() noexcept;
    auto hasProgram() const noexcept -> bool { return hasProgram_; }
    auto sequence() const noexcept -> std::uint64_t { return sequence_; }
private:
    std::uint64_t sequence_ = 0;
    bool hasSequence_ = false;
    bool hasProgram_ = false;
};

}
