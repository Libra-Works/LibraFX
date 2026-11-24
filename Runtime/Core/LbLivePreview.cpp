#include "liberafx/LbLivePreview.h"
#include "liberafx/host/LbPackagePreviewContract.h"

#include <cmath>
#include <cstdlib>
#include <cstring>
#include <string_view>

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

namespace liberafx::host {
namespace {
struct JsonCursor final {
    const char* data = nullptr;
    std::size_t size = 0;
    std::size_t pos = 0;

    void skipWs() noexcept {
        while (pos < size && (data[pos] == ' ' || data[pos] == '\t' || data[pos] == '\r' || data[pos] == '\n'))
            ++pos;
    }
    auto consume(char ch) noexcept -> bool {
        skipWs();
        if (pos >= size || data[pos] != ch) return false;
        ++pos;
        return true;
    }
    auto peek() noexcept -> char {
        skipWs();
        return pos < size ? data[pos] : '\0';
    }
    auto readString(std::string_view& out) noexcept -> bool {
        skipWs();
        if (pos >= size || data[pos] != '"') return false;
        ++pos;
        const auto start = pos;
        while (pos < size) {
            const auto ch = data[pos];
            if (ch == '"') {
                out = std::string_view(data + start, pos - start);
                ++pos;
                return true;
            }
            if (ch == '\\' || static_cast<unsigned char>(ch) < 0x20) return false;
            ++pos;
        }
        return false;
    }
    auto readBool(bool& out) noexcept -> bool {
        skipWs();
        if (size - pos >= 4 && std::memcmp(data + pos, "true", 4) == 0) {
            pos += 4;
            out = true;
            return true;
        }
        if (size - pos >= 5 && std::memcmp(data + pos, "false", 5) == 0) {
            pos += 5;
            out = false;
            return true;
        }
        return false;
    }
    auto readNumber(double& out) noexcept -> bool {
        skipWs();
        if (pos >= size) return false;
        const auto start = pos;
        while (pos < size) {
            const auto ch = data[pos];
            if ((ch >= '0' && ch <= '9') || ch == '-' || ch == '+' || ch == '.' || ch == 'e' || ch == 'E')
                ++pos;
            else
                break;
        }
        if (pos == start) return false;
        char buf[64];
        const auto len = pos - start;
        if (len >= sizeof(buf)) return false;
        std::memcpy(buf, data + start, len);
        buf[len] = '\0';
        char* end = nullptr;
        out = std::strtod(buf, &end);
        return end == buf + len && std::isfinite(out);
    }
    auto skipValue() noexcept -> bool {
        const auto ch = peek();
        if (ch == '"') {
            std::string_view dummy;
            return readString(dummy);
        }
        if (ch == 't' || ch == 'f') {
            bool dummy = false;
            return readBool(dummy);
        }
        if (ch == 'n' && size - pos >= 4 && std::memcmp(data + pos, "null", 4) == 0) {
            pos += 4;
            return true;
        }
        if (ch == '{') {
            ++pos;
            if (consume('}')) return true;
            while (true) {
                std::string_view key;
                if (!readString(key) || !consume(':') || !skipValue()) return false;
                if (consume('}')) return true;
                if (!consume(',')) return false;
            }
        }
        if (ch == '[') {
            ++pos;
            if (consume(']')) return true;
            while (true) {
                if (!skipValue()) return false;
                if (consume(']')) return true;
                if (!consume(',')) return false;
            }
        }
        double dummy = 0;
        return readNumber(dummy);
    }
};

auto hexNibble(char ch, std::uint8_t& out) noexcept -> bool {
    if (ch >= '0' && ch <= '9') { out = static_cast<std::uint8_t>(ch - '0'); return true; }
    if (ch >= 'a' && ch <= 'f') { out = static_cast<std::uint8_t>(10 + ch - 'a'); return true; }
    if (ch >= 'A' && ch <= 'F') { out = static_cast<std::uint8_t>(10 + ch - 'A'); return true; }
    return false;
}

auto parseGuid(std::string_view text, std::array<std::uint8_t, 16>& out) noexcept -> bool {
    std::uint8_t nibbles[32]{};
    std::size_t count = 0;
    for (const auto ch : text) {
        if (ch == '-') continue;
        if (count >= 32 || !hexNibble(ch, nibbles[count])) return false;
        ++count;
    }
    if (count != 32) return false;
    for (std::size_t i = 0; i < 16; ++i)
        out[i] = static_cast<std::uint8_t>((nibbles[i * 2] << 4) | nibbles[i * 2 + 1]);
    return true;
}

auto parseUpdateObject(JsonCursor& cur, ParameterDeltaUpdate& out) noexcept -> bool {
    if (!cur.consume('{')) return false;
    if (cur.consume('}')) return false;
    bool hasNodeId = false;
    while (true) {
        std::string_view key;
        if (!cur.readString(key) || !cur.consume(':')) return false;
        if (key == "containerId") {
            std::string_view val;
            if (!cur.readString(val) || !parseGuid(val, out.containerId)) return false;
        } else if (key == "nodeId") {
            std::string_view val;
            if (!cur.readString(val) || !parseGuid(val, out.nodeId)) return false;
            hasNodeId = true;
        } else if (key == "inputSlot") {
            double v = 0;
            if (!cur.readNumber(v) || v < 0 || v > 1000000) return false;
            out.inputSlot = static_cast<std::uint32_t>(v);
        } else if (key == "component") {
            double v = 0;
            if (!cur.readNumber(v) || v < 0 || v > 3) return false;
            out.component = static_cast<std::uint32_t>(v);
        } else if (key == "isInt32") {
            if (!cur.readBool(out.isInt32)) return false;
        } else if (key == "intValue") {
            double v = 0;
            if (!cur.readNumber(v)) return false;
            out.intValue = static_cast<std::int32_t>(v);
        } else if (key == "floatValue") {
            double v = 0;
            if (!cur.readNumber(v)) return false;
            out.floatValue = static_cast<float>(v);
        } else {
            if (!cur.skipValue()) return false;
        }
        if (cur.consume('}')) break;
        if (!cur.consume(',')) return false;
    }
    return hasNodeId;
}
}

auto parseParameterDeltaJson(const char* jsonUtf8, std::size_t length,
    std::vector<ParameterDeltaUpdate>& outUpdates) -> Result
{
    outUpdates.clear();
    if (!jsonUtf8 || length == 0 || length > livepreview::maxSceneBytes) return Result::InvalidArgument;
    JsonCursor cur{jsonUtf8, length, 0};
    if (!cur.consume('{')) return Result::InvalidArgument;
    if (cur.consume('}')) return Result::InvalidArgument;
    bool isParameterDelta = false;
    bool hasUpdates = false;
    while (true) {
        std::string_view key;
        if (!cur.readString(key) || !cur.consume(':')) return Result::InvalidArgument;
        if (key == "type") {
            std::string_view val;
            if (!cur.readString(val)) return Result::InvalidArgument;
            if (val != "parameter_delta") return Result::UnsupportedBinary;
            isParameterDelta = true;
        } else if (key == "updates") {
            if (!cur.consume('[')) return Result::InvalidArgument;
            hasUpdates = true;
            if (!cur.consume(']')) {
                while (true) {
                    ParameterDeltaUpdate update{};
                    if (!parseUpdateObject(cur, update)) return Result::InvalidArgument;
                    outUpdates.push_back(update);
                    if (cur.consume(']')) break;
                    if (!cur.consume(',')) return Result::InvalidArgument;
                }
            }
        } else {
            if (!cur.skipValue()) return Result::InvalidArgument;
        }
        if (cur.consume('}')) break;
        if (!cur.consume(',')) return Result::InvalidArgument;
    }
    cur.skipWs();
    if (cur.pos != cur.size || !isParameterDelta || !hasUpdates) return Result::InvalidArgument;
    return Result::Ok;
}

auto applyParameterDeltaJson(SpawnableHost& host, const char* jsonUtf8, std::size_t length) -> Result
{
    std::vector<ParameterDeltaUpdate> updates;
    const auto parsed = parseParameterDeltaJson(jsonUtf8, length, updates);
    if (parsed != Result::Ok) return parsed;
    for (const auto& u : updates) {
        const auto res = host.setNodeParameter(
            u.containerId.data(), u.nodeId.data(), u.inputSlot, u.component, u.isInt32, u.intValue, u.floatValue);
        if (res != Result::Ok) return res;
    }
    return Result::Ok;
}

auto dispatchPackagePreviewFrame(const livepreview::FrameView& frame, IPackagePreviewAdapter& adapter) -> Result
{
    const bool playing = (frame.flags & 1u) != 0;
    switch (frame.kind) {
    case livepreview::Kind::Program:
        if (!adapter.hasBuiltinSpriteMaterials()) return Result::NotReady;
        return adapter.loadPackage(frame.payload, frame.payloadLength, playing, frame.playbackRate);
    case livepreview::Kind::Playback:
        return adapter.setPlayback(playing, frame.playbackRate);
    case livepreview::Kind::Scene: {
        std::vector<ParameterDeltaUpdate> updates;
        const auto parsed = parseParameterDeltaJson(
            reinterpret_cast<const char*>(frame.payload), frame.payloadLength, updates);
        if (parsed != Result::Ok) return parsed;
        return adapter.applyParameterDelta(updates.data(), updates.size());
    }
    default:
        return Result::UnsupportedBinary;
    }
}

void computePreviewTimeStep(PreviewClockState& clock,
    double targetTimeSeconds, double currentHostTimeSeconds, bool playing,
    bool& outNeedsRestart, double& outStepDelta, int& outStepCount) noexcept
{
    outNeedsRestart = false;
    outStepDelta = 1.0 / 60.0;
    outStepCount = 0;
    const double clampedTarget = targetTimeSeconds > 0.0 && std::isfinite(targetTimeSeconds) ? targetTimeSeconds : 0.0;
    if (clock.hasEditorTime && clampedTarget + 1e-4 < clock.lastEditorTime) {
        outNeedsRestart = true;
    }
    clock.lastEditorTime = clampedTarget;
    clock.hasEditorTime = true;
    const double effectiveCurrent = outNeedsRestart ? 0.0 : currentHostTimeSeconds;
    const double remaining = clampedTarget - effectiveCurrent;
    if (remaining > 1e-5 && (!playing || remaining > 0.1)) {
        const int steps = static_cast<int>(std::ceil(remaining / outStepDelta));
        outStepCount = steps > 120 ? 120 : steps;
    }
}
}
