#pragma once

#include <atomic>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include "liberafx/platform/RenderQueue.h"

namespace liberafx {

// Preview scene protocol v1. This is NOT a bytecode opcode, package resource,
// engine object, or persistent draw. Each accepted batch replaces a whole scope.
enum class DebugDrawKind : std::uint8_t { Text, Model };
struct DebugDrawCommand final {
    std::uint8_t nodeId[16]{};
    std::uint64_t instanceId = 0;
    DebugDrawKind kind = DebugDrawKind::Text;
    float world[16]{1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1};
    float size = 1;
    bool visible = true;
    char text[1025]{}; // UTF-8, owned snapshot, no borrowed string lifetimes.
    char asset[513]{}; // Opaque adapter binding, never interpreted by Core.
};
struct DebugDrawBatch final {
    const DebugDrawCommand* commands = nullptr;
    std::size_t count = 0;
};
using SubmitDebugDrawBatch = bool (*)(void*, DebugDrawBatch) noexcept;

class DebugDrawQueue final {
public:
    static constexpr auto maximumDraws = std::size_t{128};
    auto initialize(DebugDrawCommand* storage, std::size_t capacity) noexcept -> RenderResult
    {
        if (phase_.load(std::memory_order_acquire) != Phase::Empty) return RenderResult::Busy;
        if (!storage || capacity == 0 || capacity > maximumDraws) return RenderResult::InvalidArgument;
        storage_ = storage;
        capacity_ = capacity;
        return RenderResult::Ok;
    }
    auto begin() noexcept -> RenderResult
    {
        if (!storage_) return RenderResult::NotReady;
        auto expected = Phase::Empty;
        if (!phase_.compare_exchange_strong(expected, Phase::Writing, std::memory_order_acq_rel))
            return RenderResult::Busy;
        count_ = 0;
        return RenderResult::Ok;
    }
    auto append(const DebugDrawCommand& command) noexcept -> RenderResult
    {
        if (phase_.load(std::memory_order_relaxed) != Phase::Writing) return RenderResult::NotReady;
        if (!valid(command)) return RenderResult::InvalidArgument;
        if (count_ >= capacity_) return RenderResult::CapacityExceeded;
        for (auto index = std::size_t{0}; index < count_; ++index)
        {
            const auto& previous = storage_[index];
            auto same = previous.instanceId == command.instanceId;
            for (auto byte = 0u; byte < 16; ++byte) same &= previous.nodeId[byte] == command.nodeId[byte];
            if (same) return RenderResult::InvalidArgument;
        }
        storage_[count_++] = command;
        return RenderResult::Ok;
    }
    auto seal() noexcept -> RenderResult
    {
        auto expected = Phase::Writing;
        return phase_.compare_exchange_strong(expected, Phase::Ready, std::memory_order_release)
            ? RenderResult::Ok : RenderResult::NotReady;
    }
    auto submit(SubmitDebugDrawBatch callback, void* context) noexcept -> RenderResult
    {
        if (!callback) return RenderResult::InvalidArgument;
        auto expected = Phase::Ready;
        if (!phase_.compare_exchange_strong(expected, Phase::Submitting, std::memory_order_acquire))
            return RenderResult::NotReady;
        // Rejection MUST be atomic: the adapter must neither retain nor display
        // a partial batch. All engine work belongs on the consuming owner thread.
        auto accepted = callback(context, {storage_, count_});
        phase_.store(accepted ? Phase::Empty : Phase::Ready, std::memory_order_release);
        return accepted ? RenderResult::Ok : RenderResult::Rejected;
    }
    // Owner joins writer/consumer before reset on disconnect/destruction.
    void reset() noexcept { count_ = 0; phase_.store(Phase::Empty, std::memory_order_release); }
    static auto valid(const DebugDrawCommand& command) noexcept -> bool
    {
        if (command.kind > DebugDrawKind::Model || !std::isfinite(command.size) ||
            command.size <= 0 || command.size > 10000) return false;
        auto identity = false;
        for (auto value : command.nodeId) identity |= value != 0;
        if (!identity || !terminated(command.text) || !terminated(command.asset) ||
            (command.kind == DebugDrawKind::Model && command.asset[0] == 0)) return false;
        for (auto value : command.world) if (!std::isfinite(value)) return false;
        return !command.visible || (command.world[12] == 0 && command.world[13] == 0 &&
            command.world[14] == 0 && command.world[15] == 1);
    }
private:
    template<std::size_t N> static auto terminated(const char (&value)[N]) noexcept -> bool
    {
        for (auto item : value) if (item == 0) return true;
        return false;
    }
    enum class Phase : std::uint8_t { Empty, Writing, Ready, Submitting };
    DebugDrawCommand* storage_ = nullptr;
    std::size_t capacity_ = 0, count_ = 0;
    std::atomic<Phase> phase_{Phase::Empty};
};
}
