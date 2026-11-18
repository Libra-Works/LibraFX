#pragma once

#include "liberafx/LbGeneratorPool.h"

#include <array>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <limits>
#include <memory>
#include <mutex>
#include <new>
#include <optional>
#include <utility>
#include <vector>

namespace liberafx
{
namespace generator_pool_detail
{
inline auto inlineEnqueue(void *, Job job, void *context) noexcept -> bool
{
    job(context);
    return true;
}

struct AlignedBuffer final
{
    void *data = nullptr;
    std::size_t alignment = 16;
    ~AlignedBuffer()
    {
        if (data)
        {
            ::operator delete(data, std::align_val_t{alignment});
        }
    }
    void allocate(std::size_t bytes, std::size_t requested)
    {
        alignment = requested < 16 ? 16 : requested;
        if (bytes)
        {
            data = ::operator new(bytes, std::align_val_t{alignment});
        }
    }
};

struct Slot final
{
    AlignedBuffer work;
    AlignedBuffer defaults;
    std::array<DeferredSignal, 128> deferred{};
    // Must be destroyed before its storage.
    std::optional<Instance> instance;
    GeneratorInstanceId id = 0;
    double start = 0;
    Lifetime lifetime = Lifetime::infinite();
    std::uint64_t seed = 0;
    bool started = false;
    bool expiring = false;
    bool expired = false;
    bool destroyPublished = false;
    bool destroyDelivered = false;
    bool sampleReady = false;
    bool ownerDestroySent = false;
    double sampledAge = 0;
    double signalTime = 0;
    double signalEffectTime = 0;
    void *observerOwner = nullptr;
    Result startResult = Result::Ok;
    Result ownerDestroyResult = Result::Ok;
};
struct SpawnCommand final
{
    double time = 0;
    std::size_t count = 0;
};

// In-place stable ring filtering, including a wrapped/full queue.
template <class T, class Predicate>
void removeQueued(std::vector<T> &queue, std::size_t head, std::size_t &count,
                  Predicate remove) noexcept
{
    auto kept = std::size_t{};
    for (auto index = std::size_t{}; index < count; ++index)
    {
        const auto &item = queue[(head + index) % queue.size()];
        if (remove(item))
        {
            continue;
        }
        if (kept != index)
        {
            queue[(head + kept) % queue.size()] = item;
        }
        ++kept;
    }
    count = kept;
}
} // namespace generator_pool_detail

using generator_pool_detail::AlignedBuffer;
using generator_pool_detail::inlineEnqueue;
using generator_pool_detail::removeQueued;
using generator_pool_detail::Slot;
using generator_pool_detail::SpawnCommand;

struct GeneratorPool::Impl final
{
    const Program &program;
    std::uint8_t containerId[16];
    std::size_t maxActive, budget, committed, workBytes, defaultBytes, slotCost,
        maxCaptureLanes;
    std::vector<std::unique_ptr<Slot>> slots;
    std::vector<GeneratorEvent> events;
    std::vector<GeneratorSignalEvent> signalEvents;
    std::vector<std::array<std::uint8_t, 16>> signalOutputs;
    std::vector<SpawnCommand> commands;
    std::vector<GeneratorCapture> commandCaptures;
    std::size_t eventHead = 0, eventCount = 0, signalEventHead = 0,
                signalEventCount = 0;
    std::size_t signalOutputCount = 0, active = 0;
    std::size_t commandHead = 0, commandCount = 0;
    GeneratorInstanceId nextId = 1;
    GeneratorLifecycle lifecycle{};
    bool lifecycleConfigured = false;
    bool executing = false;
    bool rejectEnqueues = false;
    mutable std::mutex mutex;

    Impl(const Program &p, const std::uint8_t *id, std::size_t maximum,
         std::size_t limit, std::size_t capacity, std::size_t base,
         std::size_t work, std::size_t defaults, std::size_t cost,
         std::size_t maxLanes)
        : program(p), maxActive(maximum), budget(limit), committed(base),
          workBytes(work), defaultBytes(defaults), slotCost(cost),
          maxCaptureLanes(maxLanes), events(capacity), signalEvents(capacity),
          signalOutputs(p.nodeCount()), commands(capacity),
          commandCaptures(capacity * maxLanes)
    {
        std::memcpy(containerId, id, 16);
        slots.reserve(maximum);
    }
    auto find(GeneratorInstanceId id) noexcept -> Slot *
    {
        if (!id)
        {
            return nullptr;
        }
        for (auto &slot : slots)
        {
            if (slot->id == id)
            {
                return slot.get();
            }
        }
        return nullptr;
    }
    void emit(GeneratorEventKind kind, const Slot &slot) noexcept
    {
        auto &event = events[(eventHead + eventCount) % events.size()];
        event = {};
        event.kind = kind;
        std::memcpy(event.containerId, containerId, 16);
        event.instanceId = slot.id;
        event.startTime = slot.start;
        event.seed = slot.seed;
        ++eventCount;
    }
    static auto observeSignal(void *context,
                              const std::uint8_t nodeId[16]) noexcept -> Result
    {
        auto slot = static_cast<Slot *>(context);
        auto impl = static_cast<Impl *>(slot->observerOwner);
        if (!impl)
        {
            return Result::InvalidHandle;
        }
        auto lock = std::lock_guard<std::mutex>(impl->mutex);
        for (auto index = std::size_t{}; index < impl->signalOutputCount;
             ++index)
        {
            if (std::memcmp(impl->signalOutputs[index].data(), nodeId, 16) != 0)
            {
                continue;
            }
            if (impl->signalEventCount == impl->signalEvents.size())
            {
                return Result::QueueRejected;
            }
            auto &event = impl->signalEvents[(impl->signalEventHead +
                                              impl->signalEventCount) %
                                             impl->signalEvents.size()];
            event = {};
            std::memcpy(event.containerId, impl->containerId, 16);
            event.instanceId = slot->id;
            std::memcpy(event.nodeId, nodeId, 16);
            event.time = slot->signalEffectTime;
            if (!std::isfinite(event.time))
            {
                return Result::InvalidArgument;
            }
            event.seed = slot->seed;
            ++impl->signalEventCount;
            break;
        }
        return Result::Ok;
    }
    auto publishDestroy(Slot &slot) noexcept -> Result
    {
        if (slot.destroyPublished)
        {
            return Result::NotReady;
        }
        if (eventCount == events.size())
        {
            return Result::QueueRejected;
        }
        emit(GeneratorEventKind::Destroy, slot);
        slot.destroyPublished = true;
        return Result::Ok;
    }
};

} // namespace liberafx
