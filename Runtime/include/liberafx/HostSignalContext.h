#pragma once

#include "liberafx/RuntimeResult.h"
#include <array>
#include <cmath>
#include <cstring>
#include <cstddef>

namespace liberafx {

enum class HostQualityAvailability : std::uint8_t { Unprovided, Known, Unknown, Unsupported };
enum class HostQualityTier : std::uint8_t { Low, Medium, High, Ultra, Cinematic };
enum class HostQualitySource : std::uint8_t { HostPolicy, EngineEffects, PlatformPolicy };
enum class QualityComparison : std::uint8_t { Equal, NotEqual, Less, LessOrEqual, Greater, GreaterOrEqual };

// Immutable for one game tick/job. Hosts publish a new snapshot at the next job boundary.
struct HostQualitySnapshot final {
    HostQualityAvailability availability = HostQualityAvailability::Unprovided;
    HostQualityTier tier = HostQualityTier::Low;
    HostQualitySource source = HostQualitySource::HostPolicy;
    std::uint64_t frameSerial = 0;
};

inline Result compareHostQuality(const HostQualitySnapshot& snapshot, QualityComparison comparison,
    HostQualityTier expected, bool& matches) noexcept
{
    matches = false;
    if (snapshot.availability == HostQualityAvailability::Unprovided) return Result::NotReady;
    if (snapshot.availability != HostQualityAvailability::Known ||
        static_cast<unsigned>(snapshot.tier) > static_cast<unsigned>(HostQualityTier::Cinematic) ||
        static_cast<unsigned>(expected) > static_cast<unsigned>(HostQualityTier::Cinematic) ||
        static_cast<unsigned>(snapshot.source) > static_cast<unsigned>(HostQualitySource::PlatformPolicy))
        return Result::InvalidValueType;
    auto actual = static_cast<unsigned>(snapshot.tier);
    auto requested = static_cast<unsigned>(expected);
    switch (comparison) {
    case QualityComparison::Equal: matches = actual == requested; break;
    case QualityComparison::NotEqual: matches = actual != requested; break;
    case QualityComparison::Less: matches = actual < requested; break;
    case QualityComparison::LessOrEqual: matches = actual <= requested; break;
    case QualityComparison::Greater: matches = actual > requested; break;
    case QualityComparison::GreaterOrEqual: matches = actual >= requested; break;
    default: return Result::InvalidArgument;
    }
    return Result::Ok;
}

enum class SignalValueKind : std::uint8_t { Scalar, Vector2, Vector3, Vector4, Color, Int32, Boolean };
using SignalFieldId = std::array<std::uint8_t, 16>;

struct SignalFieldValue final {
    SignalFieldId id{};
    SignalValueKind kind = SignalValueKind::Scalar;
    double components[4]{};
    std::int32_t integer = 0;
};

inline Result validateSignalField(const SignalFieldValue& field) noexcept
{
    auto nonzero = false;
    for (auto byte : field.id) nonzero = nonzero || byte != 0;
    if (!nonzero) return Result::InvalidArgument;
    auto width = 0u;
    switch (field.kind) {
    case SignalValueKind::Scalar: width = 1; break;
    case SignalValueKind::Vector2: width = 2; break;
    case SignalValueKind::Vector3: width = 3; break;
    case SignalValueKind::Vector4:
    case SignalValueKind::Color: width = 4; break;
    case SignalValueKind::Int32: break;
    case SignalValueKind::Boolean:
        if (field.integer != 0 && field.integer != 1) return Result::InvalidValueType;
        break;
    default: return Result::InvalidValueType;
    }
    if (width && field.integer != 0) return Result::InvalidValueType;
    for (auto component = 0u; component < 4; ++component) {
        if (!std::isfinite(field.components[component])) return Result::NonFinite;
        if (component >= width && field.components[component] != 0) return Result::InvalidValueType;
    }
    return Result::Ok;
}

// Copied with an event; no borrowed graph/engine pointers or per-field allocations.
// Only the producer mutates this object. Consumers receive a const delivery snapshot.
class SignalPayload final {
public:
    static constexpr std::size_t capacity = 16;
    Result append(const SignalFieldValue& field) noexcept
    {
        auto result = validateSignalField(field);
        if (result != Result::Ok) return result;
        for (auto index = std::size_t{}; index < count_; ++index)
            if (fields_[index].id == field.id) return Result::InvalidArgument;
        if (count_ == capacity) return Result::QueueRejected;
        fields_[count_++] = field;
        return Result::Ok;
    }
    Result read(SignalFieldId id, SignalValueKind expected, SignalFieldValue& out) const noexcept
    {
        for (auto index = std::size_t{}; index < count_; ++index) {
            if (fields_[index].id != id) continue;
            if (fields_[index].kind != expected) return Result::InvalidValueType;
            out = fields_[index];
            return Result::Ok;
        }
        return Result::NodeNotFound;
    }
    std::size_t size() const noexcept { return count_; }
    const SignalFieldValue* field(std::size_t index) const noexcept
    {
        return index < count_ ? &fields_[index] : nullptr;
    }
    Result set(const SignalFieldValue& field) noexcept
    {
        auto result = validateSignalField(field);
        if (result != Result::Ok) return result;
        for (auto index = std::size_t{}; index < count_; ++index)
        {
            if (fields_[index].id != field.id) continue;
            if (fields_[index].kind != field.kind) return Result::InvalidValueType;
            fields_[index] = field;
            return Result::Ok;
        }
        return append(field);
    }
private:
    std::array<SignalFieldValue, capacity> fields_{};
    std::size_t count_ = 0;
};

using QualitySignalTarget = Result (*)(void* context, const SignalPayload& payload) noexcept;

inline Result dispatchQualitySignal(const HostQualitySnapshot& quality, QualityComparison comparison,
    HostQualityTier expected, const SignalPayload& payload, QualitySignalTarget whenTrue,
    QualitySignalTarget whenFalse, void* context) noexcept
{
    auto selected = false;
    auto result = compareHostQuality(quality, comparison, expected, selected);
    if (result != Result::Ok) return result;
    auto target = selected ? whenTrue : whenFalse;
    return target ? target(context, payload) : Result::InvalidArgument;
}

}
