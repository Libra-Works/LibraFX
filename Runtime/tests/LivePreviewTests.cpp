#include "liberafx/LivePreview.h"

#include <array>
#include <cstdlib>
#include <cstring>
#include <limits>

namespace {
void require(bool condition) { if (!condition) std::abort(); }
void put(std::uint8_t* p, std::uint64_t value, int count)
{
    for (auto i = 0; i < count; ++i) p[i] = static_cast<std::uint8_t>(value >> (i * 8));
}
void putDouble(std::uint8_t* p, double value)
{
    auto bits = std::uint64_t{};
    std::memcpy(&bits, &value, sizeof(bits));
    put(p, bits, 8);
}
}

int main()
{
    using namespace liberafx::livepreview;
    auto bytes = std::array<std::uint8_t, 52>{};
    std::memcpy(bytes.data(), "LFXP", 4);
    put(bytes.data() + 4, 1, 2);
    put(bytes.data() + 6, 1, 2);
    put(bytes.data() + 8, 10, 8);
    putDouble(bytes.data() + 24, 2.5);
    putDouble(bytes.data() + 32, 1);
    put(bytes.data() + 40, 3, 4);
    put(bytes.data() + 44, 4, 4);
    std::memcpy(bytes.data() + 48, "LFXB", 4);
    auto frame = FrameView{};
    require(decodeFrame(bytes.data(), bytes.size(), frame) == DecodeResult::Ok);
    require(frame.kind == Kind::Program && frame.sequence == 10 && frame.payload == bytes.data() + 48 &&
            frame.payloadLength == 4 && frame.timeSeconds == 2.5 && frame.flags == 3);
    auto cursor = SequenceCursor{};
    require(cursor.validate(frame) == SequenceResult::Accept && !cursor.hasProgram());
    auto playback = frame;
    playback.kind = Kind::Playback;
    playback.sequence = 11;
    playback.baseSequence = 10;
    require(cursor.validate(playback) == SequenceResult::Resync);
    require(cursor.commit(frame) == SequenceResult::Accept && cursor.hasProgram());
    require(cursor.validate(playback) == SequenceResult::Accept && cursor.sequence() == 10);
    require(cursor.commit(playback) == SequenceResult::Accept);
    require(cursor.validate(playback) == SequenceResult::Resync);
    require(cursor.validate(frame) == SequenceResult::Stale);
    frame.sequence = 11;
    require(cursor.commit(frame) == SequenceResult::Accept);
    auto error = frame;
    error.kind = Kind::CompileError;
    error.sequence = 12;
    require(cursor.commit(error) == SequenceResult::Accept && cursor.hasProgram());
    playback.sequence = 13;
    playback.baseSequence = 11;
    require(cursor.validate(playback) == SequenceResult::Resync);
    playback.baseSequence = 12;
    require(cursor.commit(playback) == SequenceResult::Accept);
    cursor.reset();
    require(!cursor.hasProgram() && cursor.validate(playback) == SequenceResult::Resync);

    require(decodeFrame(nullptr, bytes.size(), frame) == DecodeResult::InvalidFrame);
    require(decodeFrame(bytes.data(), 47, frame) == DecodeResult::InvalidFrame);
    require(decodeFrame(bytes.data(), 51, frame) == DecodeResult::InvalidFrame);
    put(bytes.data() + 44, 0xffffffffu, 4);
    require(decodeFrame(bytes.data(), bytes.size(), frame) == DecodeResult::InvalidFrame);
    put(bytes.data() + 44, 4, 4);
    put(bytes.data() + 40, 4, 4);
    require(decodeFrame(bytes.data(), bytes.size(), frame) == DecodeResult::InvalidFrame);
    put(bytes.data() + 40, 0, 4);
    putDouble(bytes.data() + 24, std::numeric_limits<double>::quiet_NaN());
    require(decodeFrame(bytes.data(), bytes.size(), frame) == DecodeResult::InvalidFrame);
    putDouble(bytes.data() + 24, 0);
    putDouble(bytes.data() + 32, -1);
    require(decodeFrame(bytes.data(), bytes.size(), frame) == DecodeResult::InvalidFrame);
    putDouble(bytes.data() + 32, std::numeric_limits<double>::infinity());
    require(decodeFrame(bytes.data(), bytes.size(), frame) == DecodeResult::InvalidFrame);
    putDouble(bytes.data() + 32, 1);
    put(bytes.data() + 6, 9, 2);
    require(decodeFrame(bytes.data(), bytes.size(), frame) == DecodeResult::InvalidFrame);
    put(bytes.data() + 6, 2, 2);
    require(decodeFrame(bytes.data(), bytes.size(), frame) == DecodeResult::InvalidFrame);
    put(bytes.data() + 44, 0, 4);
    require(decodeFrame(bytes.data(), headerBytes, frame) == DecodeResult::Ok);
    put(bytes.data() + 6, 3, 2);
    put(bytes.data() + 44, 4, 4);
    bytes[48] = 0xc0;
    require(decodeFrame(bytes.data(), bytes.size(), frame) == DecodeResult::InvalidFrame);
    auto largeError = std::array<std::uint8_t, headerBytes + maxErrorBytes + 1>{};
    std::memcpy(largeError.data(), "LFXP", 4);
    put(largeError.data() + 4, 1, 2);
    put(largeError.data() + 6, 3, 2);
    put(largeError.data() + 44, maxErrorBytes + 1, 4);
    require(decodeFrame(largeError.data(), largeError.size(), frame) == DecodeResult::InvalidFrame);
    put(bytes.data() + 6, 1, 2);
    put(bytes.data() + 16, 1, 8);
    require(decodeFrame(bytes.data(), bytes.size(), frame) == DecodeResult::InvalidFrame);
    return 0;
}
