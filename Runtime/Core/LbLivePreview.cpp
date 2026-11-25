#include "liberafx/LbLivePreview.h"
#include "liberafx/host/LbPackagePreviewContract.h"

#include <charconv>
#include <cmath>
#include <cstring>
#include <string_view>
#include <utility>

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
        ((kind == 4 || kind == 5) && ((kind == 4 && read64(p + 8) != 0) ||
            base != 0 || time != 0 || rate != 0 || flags != 0 || length == 0 ||
            length > (kind == 4 ? maxErrorBytes : maxSceneBytes) ||
            !validUtf8(p + headerBytes, length))))
        return DecodeResult::InvalidFrame;
    out = {static_cast<Kind>(kind), read64(p + 8), base, time, rate, flags, p + headerBytes, length};
    return DecodeResult::Ok;
}

auto SequenceCursor::validate(const FrameView& frame) const noexcept -> SequenceResult
{
    if (frame.kind == Kind::Control)
    {
        return SequenceResult::Resync;
    }
    if (frame.kind == Kind::Scene && (!hasProgram_ || !hasSequence_ || frame.sequence == 0))
    {
        return SequenceResult::Resync;
    }
    if (hasSequence_ && frame.sequence < sequence_) return SequenceResult::Stale;
    if (frame.kind == Kind::Scene && frame.sequence == sequence_)
    {
        return SequenceResult::Stale;
    }
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
    auto readNumber(double& out) noexcept -> bool
    {
        skipWs();
        auto start = pos;
        if (pos < size && data[pos] == '-')
        {
            ++pos;
        }
        if (pos >= size)
        {
            return false;
        }
        if (data[pos] == '0')
        {
            ++pos;
        }
        else if (data[pos] >= '1' && data[pos] <= '9')
        {
            while (pos < size && data[pos] >= '0' && data[pos] <= '9')
            {
                ++pos;
            }
        }
        else
        {
            return false;
        }
        if (pos < size && data[pos] == '.')
        {
            ++pos;
            auto digits = pos;
            while (pos < size && data[pos] >= '0' && data[pos] <= '9')
            {
                ++pos;
            }
            if (pos == digits)
            {
                return false;
            }
        }
        if (pos < size && (data[pos] == 'e' || data[pos] == 'E'))
        {
            ++pos;
            if (pos < size && (data[pos] == '+' || data[pos] == '-'))
            {
                ++pos;
            }
            auto digits = pos;
            while (pos < size && data[pos] >= '0' && data[pos] <= '9')
            {
                ++pos;
            }
            if (pos == digits)
            {
                return false;
            }
        }
        auto result = std::from_chars(data + start, data + pos, out);
        return result.ec == std::errc{} && result.ptr == data + pos && std::isfinite(out);
    }
    template <typename T>
    auto readInteger(T& out) noexcept -> bool
    {
        skipWs();
        auto start = pos;
        if (pos < size && data[pos] == '-')
        {
            ++pos;
        }
        auto digits = pos;
        while (pos < size && data[pos] >= '0' && data[pos] <= '9')
        {
            ++pos;
        }
        if (pos == digits || (pos - digits > 1 && data[digits] == '0'))
        {
            return false;
        }
        auto parsed = std::from_chars(data + start, data + pos, out);
        return parsed.ec == std::errc{} && parsed.ptr == data + pos;
    }

};

auto hexNibble(char ch, std::uint8_t& out) noexcept -> bool {
    if (ch >= '0' && ch <= '9') { out = static_cast<std::uint8_t>(ch - '0'); return true; }
    if (ch >= 'a' && ch <= 'f') { out = static_cast<std::uint8_t>(10 + ch - 'a'); return true; }
    if (ch >= 'A' && ch <= 'F') { out = static_cast<std::uint8_t>(10 + ch - 'A'); return true; }
    return false;
}

auto parseGuid(std::string_view text, std::array<std::uint8_t, 16>& out) noexcept -> bool {
    if (text.size() != 36 || text[8] != '-' || text[13] != '-' ||
        text[18] != '-' || text[23] != '-')
    {
        return false;
    }
    std::uint8_t nibbles[32]{};
    std::size_t count = 0;
    for (auto index = std::size_t{0}; index < text.size(); ++index) {
        auto ch = text[index];
        if (index == 8 || index == 13 || index == 18 || index == 23) continue;
        if (count >= 32 || !hexNibble(ch, nibbles[count])) return false;
        ++count;
    }
    if (count != 32) return false;
    for (std::size_t i = 0; i < 16; ++i)
        out[i] = static_cast<std::uint8_t>((nibbles[i * 2] << 4) | nibbles[i * 2 + 1]);
    return true;
}

auto parseCurveKeys(JsonCursor& cur, std::vector<CurveKey>& keys) -> bool
{
    if (!cur.consume('['))
    {
        return false;
    }
    if (cur.consume(']'))
    {
        return true;
    }
    while (true)
    {
        if (keys.size() >= 10000 || !cur.consume('{') || cur.consume('}'))
        {
            return false;
        }
        auto item = CurveKey{};
        auto fields = std::uint32_t{};
        while (true)
        {
            auto key = std::string_view{};
            if (!cur.readString(key) || !cur.consume(':'))
            {
                return false;
            }
            auto bit = key == "time" ? 1u : key == "value" ? 2u :
                key == "inSlope" ? 4u : key == "outSlope" ? 8u :
                key == "interpolation" ? 16u : 0u;
            if (!bit || (fields & bit))
            {
                return false;
            }
            fields |= bit;
            if (bit == 16u)
            {
                auto interpolation = std::uint32_t{};
                if (!cur.readInteger(interpolation) || interpolation > 2)
                {
                    return false;
                }
                item.interpolation = static_cast<std::uint8_t>(interpolation);
            }
            else
            {
                auto& number = bit == 1u ? item.time : bit == 2u ? item.value :
                    bit == 4u ? item.inSlope : item.outSlope;
                if (!cur.readNumber(number) || std::abs(number) > 1000000)
                {
                    return false;
                }
            }
            if (cur.consume('}'))
            {
                break;
            }
            if (!cur.consume(','))
            {
                return false;
            }
        }
        if (fields != 31u || (!keys.empty() && item.time <= keys.back().time))
        {
            return false;
        }
        keys.push_back(item);
        if (cur.consume(']'))
        {
            return true;
        }
        if (!cur.consume(','))
        {
            return false;
        }
    }
}

struct ParsedMatrix final {
    bool present = false;
    std::array<std::array<std::uint8_t, 16>, 4> rowNodeIds{};
    std::array<double, 16> values{};
};

auto parseUpdateObject(JsonCursor& cur, ParameterDeltaUpdate& out,
                       ParsedMatrix& matrix) -> bool
{
    if (!cur.consume('{') || cur.consume('}'))
    {
        return false;
    }
    auto fields = std::uint32_t{0};
    while (true)
    {
        auto key = std::string_view{};
        if (!cur.readString(key) || !cur.consume(':'))
        {
            return false;
        }
        auto bit = key == "containerId" ? 1u : key == "nodeId" ? 2u :
            key == "inputSlot" ? 4u : key == "component" ? 8u :
            key == "isInt32" ? 16u : key == "intValue" ? 32u :
            key == "floatValue" ? 64u : key == "reset" ? 128u :
            key == "kind" ? 256u : key == "rowNodeIds" ? 512u :
            key == "values" ? 1024u : key == "keys" ? 2048u : 0u;
        if (bit == 0 || (fields & bit) != 0)
        {
            return false;
        }
        fields |= bit;
        if (key == "containerId" || key == "nodeId")
        {
            auto value = std::string_view{};
            auto& id = key == "containerId" ? out.containerId : out.nodeId;
            if (!cur.readString(value) || !parseGuid(value, id))
            {
                return false;
            }
        }
        else if (key == "inputSlot")
        {
            if (!cur.readInteger(out.inputSlot) || out.inputSlot > 255)
            {
                return false;
            }
        }
        else if (key == "component")
        {
            if (!cur.readInteger(out.component) || out.component > 3)
            {
                return false;
            }
        }
        else if (key == "isInt32")
        {
            if (!cur.readBool(out.isInt32))
            {
                return false;
            }
        }
        else if (key == "intValue")
        {
            if (!cur.readInteger(out.intValue))
            {
                return false;
            }
        }
        else if (key == "reset")
        {
            if (!cur.readBool(out.reset))
            {
                return false;
            }
        }
        else if (key == "kind")
        {
            auto value = std::string_view{};
            if (!cur.readString(value) || (value != "matrix" && value != "fcurve"))
            {
                return false;
            }
            matrix.present = value == "matrix";
            out.isCurve = value == "fcurve";
        }
        else if (key == "keys")
        {
            if (!parseCurveKeys(cur, out.curveKeys))
            {
                return false;
            }
        }
        else if (key == "rowNodeIds")
        {
            if (!cur.consume('['))
            {
                return false;
            }
            for (auto index = std::size_t{}; index < matrix.rowNodeIds.size(); ++index)
            {
                auto value = std::string_view{};
                if ((index != 0 && !cur.consume(',')) || !cur.readString(value) ||
                    !parseGuid(value, matrix.rowNodeIds[index]))
                {
                    return false;
                }
                for (auto previous = std::size_t{}; previous < index; ++previous)
                {
                    if (matrix.rowNodeIds[index] == matrix.rowNodeIds[previous])
                    {
                        return false;
                    }
                }
            }
            if (!cur.consume(']'))
            {
                return false;
            }
        }
        else if (key == "values")
        {
            if (!cur.consume('['))
            {
                return false;
            }
            for (auto index = std::size_t{}; index < matrix.values.size(); ++index)
            {
                if ((index != 0 && !cur.consume(',')) || !cur.readNumber(matrix.values[index]))
                {
                    return false;
                }
            }
            if (!cur.consume(']'))
            {
                return false;
            }
        }
        else if (!cur.readNumber(out.floatValue))
        {
            return false;
        }
        if (cur.consume('}'))
        {
            break;
        }
        if (!cur.consume(','))
        {
            return false;
        }
    }
    if (out.isCurve)
    {
        auto required = 1u | 2u | 256u | (out.reset ? 128u : 2048u);
        return fields == required || (!out.reset && fields == (required | 128u));
    }
    if (matrix.present)
    {
        return out.reset ? fields == (1u | 128u | 256u | 512u)
                         : fields == (1u | 256u | 512u | 1024u);
    }
    return out.reset ? fields == 143u : (fields == 127u || fields == 255u);
}
}

static auto parseParameterDeltaJsonImpl(const char* jsonUtf8, std::size_t length,
    std::vector<ParameterDeltaUpdate>& outUpdates, std::uint64_t* outSequence) -> Result
{
    outUpdates.clear();
    if (!jsonUtf8 || length == 0 || length > livepreview::maxSceneBytes)
    {
        return Result::InvalidArgument;
    }
    auto parsedUpdates = std::vector<ParameterDeltaUpdate>{};
    auto cur = JsonCursor{jsonUtf8, length, 0};
    if (!cur.consume('{') || cur.consume('}'))
    {
        return Result::InvalidArgument;
    }
    auto hasType = false;
    auto hasUpdates = false;
    auto hasSequence = false;
    auto sequence = std::uint64_t{};
    while (true)
    {
        auto key = std::string_view{};
        if (!cur.readString(key) || !cur.consume(':'))
        {
            return Result::InvalidArgument;
        }
        if (key == "type")
        {
            auto value = std::string_view{};
            if (hasType || !cur.readString(value))
            {
                return Result::InvalidArgument;
            }
            if (value != "parameter_delta")
            {
                return Result::UnsupportedBinary;
            }
            hasType = true;
        }
        else if (key == "updates")
        {
            if (hasUpdates || !cur.consume('['))
            {
                return Result::InvalidArgument;
            }
            hasUpdates = true;
            if (!cur.consume(']'))
            {
                while (true)
                {
                    auto update = ParameterDeltaUpdate{};
                    auto matrix = ParsedMatrix{};
                    if (!parseUpdateObject(cur, update, matrix))
                    {
                        return Result::InvalidArgument;
                    }
                    if (matrix.present)
                    {
                        for (auto row = std::size_t{}; row < matrix.rowNodeIds.size(); ++row)
                        {
                            for (auto column = std::size_t{}; column < 4; ++column)
                            {
                                auto lane = update;
                                lane.nodeId = matrix.rowNodeIds[row];
                                lane.component = static_cast<std::uint32_t>(column);
                                lane.floatValue = matrix.values[row * 4 + column];
                                parsedUpdates.push_back(lane);
                            }
                        }
                    }
                    else
                    {
                        parsedUpdates.push_back(update);
                    }
                    if (cur.consume(']'))
                    {
                        break;
                    }
                    if (!cur.consume(','))
                    {
                        return Result::InvalidArgument;
                    }
                }
            }
        }
        else if (key == "sequence")
        {
            if (hasSequence || !cur.readInteger(sequence))
            {
                return Result::InvalidArgument;
            }
            hasSequence = true;
        }
        else
        {
            return Result::InvalidArgument;
        }
        if (cur.consume('}'))
        {
            break;
        }
        if (!cur.consume(','))
        {
            return Result::InvalidArgument;
        }
    }
    cur.skipWs();
    if (cur.pos != cur.size || !hasType || !hasUpdates || !hasSequence)
    {
        return Result::InvalidArgument;
    }
    outUpdates = std::move(parsedUpdates);
    if (outSequence)
    {
        *outSequence = sequence;
    }
    return Result::Ok;
}

auto parseParameterDeltaJson(const char* jsonUtf8, std::size_t length,
    std::vector<ParameterDeltaUpdate>& outUpdates) -> Result
{
    return parseParameterDeltaJsonImpl(jsonUtf8, length, outUpdates, nullptr);
}

auto applyParameterDeltaJson(SpawnableHost& host, const char* jsonUtf8, std::size_t length) -> Result
{
    std::vector<ParameterDeltaUpdate> updates;
    const auto parsed = parseParameterDeltaJson(jsonUtf8, length, updates);
    if (parsed != Result::Ok) return parsed;
    return host.setNodeParameters(updates.data(), updates.size());
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
        auto sequence = std::uint64_t{};
        const auto parsed = parseParameterDeltaJsonImpl(
            reinterpret_cast<const char*>(frame.payload), frame.payloadLength, updates, &sequence);
        if (parsed != Result::Ok) return parsed;
        if (frame.sequence != 0 && frame.sequence != sequence)
        {
            return Result::InvalidArgument;
        }
        for (const auto& update : updates)
        {
            if (update.isCurve && !adapter.supportsCurveDeltas())
            {
                return Result::UnsupportedBinary;
            }
        }
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
