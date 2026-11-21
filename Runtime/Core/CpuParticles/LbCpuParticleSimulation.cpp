#include "LbCpuParticlesInternal.h"

namespace liberafx::cpu_particle_detail {
auto enqueueInline(void*, Job job, void* context) noexcept -> bool
{
    job(context);
    return true;
}
auto System::publish(const Io& io, const double* values, double time) noexcept -> Result
{
    if (pendingCount == pending.size() || eventCount == events.size())
    {
        return Result::QueueRejected;
    }
    auto event = CpuParticleEvent{};
    std::copy(id.begin(), id.end(), event.systemId);
    std::copy(io.node.begin(), io.node.end(), event.nodeId);
    event.particleId = current->draw.id;
    event.kind = io.kind == 3 ? 2u : 1u;
    event.width = io.width;
    event.time = time;
    if (values)
    {
        std::copy_n(values, io.width, event.values);
    }
    pending[pendingCount++] = {event, io.root};
    events[(eventHead + eventCount++) % events.size()] = event;
    return Result::Ok;
}
auto System::collect(Slot& slot, std::uint32_t stageIndex, double time) noexcept -> Result
{
    for (auto& io : stages[stageIndex].io)
    {
        if (io.kind != 1)
        {
            continue;
        }
        auto values = std::array<double, 4>{};
        auto width = std::uint8_t{};
        auto result = slot.stages[stageIndex].valueComponents(io.node.data(), values.data(), width);
        if (result != Result::Ok || width != io.width)
        {
            return result == Result::Ok ? Result::InvalidBinary : result;
        }
        result = publish(io, values.data(), time);
        if (result != Result::Ok)
        {
            return result;
        }
    }
    return Result::Ok;
}
auto System::run(Slot& slot, std::uint32_t stageIndex, double time, const HostQualitySnapshot& quality) noexcept -> Result
{
    current = &slot;
    auto& instance = slot.stages[stageIndex];
    auto& stage = stages[stageIndex];
    auto result = instance.setHostQuality(quality);
    if (result == Result::Ok)
    {
        result = instance.setTimeContext(time, frameDelta, realTime, std::max(0.0, realTime - slot.realStart), realDelta);
    }
    for (auto& io : stage.io)
    {
        if (result != Result::Ok)
        {
            return result;
        }
        if (io.kind == 0 && nonzero(io.root))
        {
            for (auto lane = 0u; lane < io.width && result == Result::Ok; ++lane)
            {
                result = instance.setUnconnectedParameter(io.node.data(), 0, static_cast<std::uint8_t>(lane), io.sampled[lane]);
            }
        }
    }
    if (result == Result::Ok)
    {
        result = instance.updateAsync(slot.age, enqueueInline, nullptr);
    }
    if (result == Result::Ok)
    {
        result = instance.result();
    }
    if (result == Result::Ok)
    {
        result = instance.dispatchGeneratedSignals(10000);
    }
    for (auto index = std::size_t{}; index < incomingCount && result == Result::Ok; ++index)
    {
        if (incoming[index].stage == stageIndex)
        {
            result = instance.dispatchSignal(incoming[index].node.data(), slot.age, 10000);
        }
    }
    if (result == Result::Ok && nonzero(stage.entry))
    {
        result = instance.dispatchSignal(stage.entry.data(), slot.age, 10000);
    }
    if (result == Result::Ok)
    {
        result = collect(slot, stageIndex, time);
    }
    return result;
}
auto System::tick(Instance& root, double time, bool emitting, const HostQualitySnapshot& quality) noexcept -> Result
{
    if (failure != Result::Ok)
    {
        return failure;
    }
    if (!std::isfinite(time) || time < 0 || time < lastTime)
    {
        return Result::InvalidArgument;
    }
    if (time == lastTime)
    {
        return Result::Ok;
    }
    try {
        const auto status = refreshEmitter(time);
        if (status != Result::Ok) return status;
    } catch (const std::bad_alloc&) { return Result::InsufficientWork; }
    stepDelta = lastTime < 0 ? 0 : time - lastTime;
    lastTime = time;
    for (auto& stage : stages)
    {
        for (auto& io : stage.io)
        {
            if (io.kind != 0 || !nonzero(io.root))
            {
                continue;
            }
            auto values = std::array<double, 4>{};
            auto width = std::uint8_t{};
            auto result = root.valueComponents(io.root.data(), values.data(), width);
            if (result != Result::Ok || io.component + io.width > width)
            {
                return result == Result::Ok ? Result::InvalidBinary : result;
            }
            std::copy_n(values.data() + io.component, io.width, io.sampled.data());
        }
    }
    // Existing particles update exactly once. Stable slot order is not a reduction.
    for (auto index = 0u; index < capacity; ++index)
    {
        auto& slot = slots[index];
        if (!slot.active)
        {
            continue;
        }
        slot.age += stepDelta;
        if (slot.age >= slot.life)
        {
            slot.active = false;
            --live;
            continue;
        }
        auto result = run(slot, 1, time, quality);
        if (result != Result::Ok)
        {
            return result;
        }
        if (slot.age >= slot.life)
        {
            slot.active = false;
            --live;
        }
    }
    auto requested = double{};
    if (emitting)
    {
        if (nonzero(emissionSource))
        {
            auto values = std::array<double, 4>{};
            auto width = std::uint8_t{};
            auto result = root.valueComponents(emissionSource.data(), values.data(), width);
            if (result != Result::Ok || emissionComponent >= width)
            {
                return result == Result::Ok ? Result::InvalidBinary : result;
            }
            auto total = values[emissionComponent];
            if (total < lastTotal || total > 4294967295.0 || std::floor(total) != total)
            {
                return Result::InvalidArgument;
            }
            requested = total - lastTotal;
            lastTotal = total;
        }
        else
        {
            remainder += rate * stepDelta;
            requested = std::floor(remainder);
            remainder -= requested;
        }
        requested += burst;
        burst = 0;
    }
    if (!std::isfinite(requested) || requested > capacity - live ||
        requested > 4294967295.0 - static_cast<double>(nextId) + 1)
    {
        return Result::InsufficientWork;
    }
    auto remaining = static_cast<std::uint32_t>(requested);
    for (auto index = 0u; index < capacity && remaining != 0; ++index)
    {
        auto& slot = slots[index];
        if (slot.active)
        {
            continue;
        }
        slot.draw = {};
        slot.draw.id = nextId++;
        slot.velocity = {};
        slot.age = 0;
        slot.realStart = realTime;
        slot.life = 2;
        for (auto stageIndex = 0u; stageIndex < 2; ++stageIndex)
        {
            auto& stage = stages[stageIndex];
            auto work = static_cast<std::uint8_t*>(stage.work.data) + index * stage.workStride;
            auto defaults = stage.defaults.data ? static_cast<double*>(stage.defaults.data) + index * stage.defaultStride : nullptr;
            auto result = slot.stages[stageIndex].initialize(stage.program, reinterpret_cast<double*>(work),
                stage.workStride / sizeof(double), defaults, stage.defaultStride);
            if (result == Result::Ok)
            {
                result = slot.stages[stageIndex].setSignalObserver(observe, this);
            }
            if (result != Result::Ok)
            {
                return result;
            }
        }
        auto savedDelta = stepDelta;
        stepDelta = 0;
        auto result = run(slot, 0, time, quality);
        if (result == Result::Ok && slot.life > 0)
        {
            result = run(slot, 1, time, quality);
        }
        stepDelta = savedDelta;
        if (result != Result::Ok)
        {
            return result;
        }
        slot.active = slot.life > 0;
        live += slot.active ? 1 : 0;
        --remaining;
    }
    current = nullptr;
    incomingCount = 0;
    auto activeIndex = std::size_t{};
    for (auto index = 0u; index < capacity; ++index)
    {
        if (slots[index].active)
        {
            activeIndices[activeIndex++] = index;
        }
    }
    return Result::Ok;
}
}
