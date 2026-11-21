#include "LbGeneratorPoolInternal.h"

namespace liberafx
{

auto GeneratorPool::configureLifecycle(
    const GeneratorLifecycle &lifecycle) noexcept -> Result
{
    auto nonzero = [](const std::uint8_t *id) noexcept
    {
        for (auto i = 0u; i < 16; ++i)
        {
            if (id[i])
            {
                return true;
            }
        }
        return false;
    };
    if ((lifecycle.hasOnStart &&
         (!nonzero(lifecycle.onStartNodeId) || !lifecycle.instructionBudget ||
          lifecycle.instructionBudget > 10000)) ||
        (lifecycle.hasLife && !nonzero(lifecycle.lifeNodeId)))
    {
        return Result::InvalidArgument;
    }
    auto lock = std::lock_guard<std::mutex>(impl_->mutex);
    if (impl_->executing || impl_->active || impl_->commandCount ||
        impl_->eventCount || impl_->signalEventCount)
    {
        return Result::Busy;
    }
    impl_->lifecycle = lifecycle;
    impl_->lifecycleConfigured = true;
    return Result::Ok;
}

auto GeneratorPool::start(GeneratorInstanceId id) noexcept -> Result
{
    auto lock = std::unique_lock<std::mutex>(impl_->mutex);
    if (impl_->executing)
    {
        return Result::Busy;
    }
    if (!impl_->lifecycleConfigured)
    {
        return Result::InvalidArgument;
    }
    auto slot = impl_->find(id);
    if (!slot)
    {
        return Result::InvalidHandle;
    }
    if (slot->expiring)
    {
        return impl_->publishDestroy(*slot);
    }
    if (slot->started)
    {
        return slot->startResult;
    }
    auto signal = SignalHandle{};
    auto life = ValueHandle{};
    auto status = Result::Ok;
    if (impl_->lifecycle.hasOnStart)
    {
        status =
            slot->instance->bindSignal(impl_->lifecycle.onStartNodeId, signal);
        if (status != Result::Ok)
        {
            return status;
        }
    }
    if (impl_->lifecycle.hasLife)
    {
        status = slot->instance->bindValue(impl_->lifecycle.lifeNodeId, life);
        if (status != Result::Ok)
        {
            return status;
        }
        if (life.componentCount != 1)
        {
            return Result::InvalidValueType;
        }
    }
    impl_->executing = true;
    slot->signalTime = 0;
    slot->signalEffectTime = slot->start;
    lock.unlock();
    status = slot->instance->updateAsync(0, inlineEnqueue, nullptr);
    auto evaluated = status == Result::Ok;
    if (status == Result::Ok)
    {
        status = slot->instance->result();
    }
    auto duration = std::array<double, 4>{};
    if (status == Result::Ok && impl_->lifecycle.hasLife)
    {
        status = slot->instance->value(life, duration.data());
        if (status == Result::Ok &&
            (!std::isfinite(duration[0]) || duration[0] < 0))
        {
            status = Result::InvalidValueType;
        }
    }
    if (status == Result::Ok)
    {
        status = slot->instance->dispatchGeneratedSignals(
            impl_->lifecycle.instructionBudget
                ? impl_->lifecycle.instructionBudget
                : 10000);
    }
    // Once signal traversal starts it is never replayed, even if a callback
    // fails.
    auto dispatched = status == Result::Ok;
    if (dispatched && impl_->lifecycle.hasOnStart)
    {
        status = slot->instance->dispatchSignal(
            signal, 0, impl_->lifecycle.instructionBudget);
    }
    lock.lock();
    impl_->executing = false;
    if (evaluated)
    {
        slot->started = true;
        slot->startResult = status;
        if (dispatched && impl_->lifecycle.hasLife)
        {
            (void)Lifetime::finite(duration[0], slot->lifetime);
        }
    }
    if (status != Result::Ok)
    {
        return status;
    }
    slot->sampleReady = true;
    slot->sampledAge = 0;
    auto forceScope = std::uint8_t{};
    status = slot->instance->pendingForceRequest(forceScope);
    if (status != Result::Ok || forceScope != 0)
    {
        return status;
    }
    if (impl_->lifecycle.hasLife && duration[0] == 0)
    {
        slot->expired = true;
        auto due = double{};
        auto hasSignal = false;
        status = slot->instance->nextDeferredSignal(due, hasSignal);
        if (status != Result::Ok || hasSignal)
        {
            return status;
        }
        slot->expiring = true;
        return impl_->publishDestroy(*slot);
    }
    return Result::Ok;
}

auto GeneratorPool::finalize(GeneratorInstanceId id) noexcept -> Result
{
    auto lock = std::unique_lock<std::mutex>(impl_->mutex);
    if (impl_->executing)
    {
        return Result::Busy;
    }
    auto slot = impl_->find(id);
    if (!slot)
    {
        return Result::InvalidHandle;
    }
    if (!slot->expiring || !slot->destroyDelivered)
    {
        return Result::NotReady;
    }
    if (impl_->eventCount == impl_->events.size())
    {
        return Result::QueueRejected;
    }
    impl_->emit(GeneratorEventKind::Stop, *slot);
    slot->id = 0;
    slot->expiring = false;
    --impl_->active;
    impl_->executing = true;
    impl_->rejectEnqueues = true;
    lock.unlock();
    slot->instance.reset();
    lock.lock();
    impl_->rejectEnqueues = false;
    impl_->executing = false;
    return Result::Ok;
}

auto GeneratorPool::pendingForceRequest(GeneratorInstanceId id,
                                        std::uint8_t &scope) const noexcept
    -> Result
{
    auto lock = std::lock_guard<std::mutex>(impl_->mutex);
    if (impl_->executing)
    {
        return Result::Busy;
    }
    auto slot = impl_->find(id);
    if (!slot)
    {
        return Result::InvalidHandle;
    }
    return slot->instance->pendingForceRequest(scope);
}

auto GeneratorPool::setLive(GeneratorInstanceId id,
                            const GeneratorCapture *values,
                            std::size_t count) noexcept -> Result
{
    if (count > 256 || (count && !values))
    {
        return Result::InvalidArgument;
    }
    auto lock = std::lock_guard<std::mutex>(impl_->mutex);
    if (impl_->executing)
    {
        return Result::Busy;
    }
    auto slot = impl_->find(id);
    if (!slot)
    {
        return Result::InvalidHandle;
    }
    if (slot->expiring)
    {
        return Result::Busy;
    }
    auto handles = std::array<ParameterHandle, 256>{};
    auto previous = std::array<double, 256>{};
    for (auto i = std::size_t{}; i < count; ++i)
    {
        if (!std::isfinite(values[i].value))
        {
            return Result::NonFinite;
        }
        auto result =
            slot->instance->bindParameter(values[i].nodeId, values[i].inputSlot,
                                          values[i].component, handles[i]);
        if (result != Result::Ok)
        {
            return result;
        }
        result = slot->instance->parameter(handles[i], previous[i]);
        if (result != Result::Ok)
        {
            return result;
        }
        result = slot->instance->setUnconnectedParameter(
            values[i].nodeId, values[i].inputSlot, values[i].component,
            previous[i]);
        if (result != Result::Ok)
        {
            return result;
        }
    }
    for (auto i = std::size_t{}; i < count; ++i)
    {
        auto result = slot->instance->setParameter(handles[i], values[i].value);
        if (result != Result::Ok)
        {
            while (i)
            {
                --i;
                (void)slot->instance->setParameter(handles[i], previous[i]);
            }
            return result;
        }
    }
    return Result::Ok;
}

auto GeneratorPool::setCaptured(GeneratorInstanceId id,
                                const GeneratorCapture *values,
                                std::size_t count) noexcept -> Result
{
    if (count > 256 || (count && !values))
    {
        return Result::InvalidArgument;
    }
    auto lock = std::lock_guard<std::mutex>(impl_->mutex);
    if (impl_->executing)
    {
        return Result::Busy;
    }
    auto slot = impl_->find(id);
    if (!slot)
    {
        return Result::InvalidHandle;
    }
    if (slot->started || slot->sampleReady || slot->expiring)
    {
        return Result::Busy;
    }
    auto handles = std::array<ParameterHandle, 256>{};
    auto previous = std::array<double, 256>{};
    for (auto i = std::size_t{}; i < count; ++i)
    {
        if (!std::isfinite(values[i].value))
        {
            return Result::NonFinite;
        }
        auto result =
            slot->instance->bindParameter(values[i].nodeId, values[i].inputSlot,
                                          values[i].component, handles[i]);
        if (result != Result::Ok)
        {
            return result;
        }
        result = slot->instance->parameter(handles[i], previous[i]);
        if (result != Result::Ok)
        {
            return result;
        }
        result = slot->instance->setUnconnectedParameter(
            values[i].nodeId, values[i].inputSlot, values[i].component,
            previous[i]);
        if (result != Result::Ok)
        {
            return result;
        }
    }
    for (auto i = std::size_t{}; i < count; ++i)
    {
        auto result = slot->instance->setParameter(handles[i], values[i].value);
        if (result != Result::Ok)
        {
            while (i)
            {
                --i;
                (void)slot->instance->setParameter(handles[i], previous[i]);
            }
            return result;
        }
    }
    return Result::Ok;
}

auto GeneratorPool::setSignalOutputs(const std::uint8_t *nodeIds,
                                     std::size_t count) noexcept -> Result
{
    if (count && !nodeIds)
    {
        return Result::InvalidArgument;
    }
    auto lock = std::lock_guard<std::mutex>(impl_->mutex);
    if (count > impl_->signalOutputs.size())
    {
        return Result::InvalidArgument;
    }
    if (impl_->executing || impl_->active || impl_->commandCount ||
        impl_->eventCount || impl_->signalEventCount)
    {
        return Result::Busy;
    }
    for (auto index = std::size_t{}; index < count; ++index)
    {
        auto id = nodeIds + index * 16;
        for (auto previous = std::size_t{}; previous < index; ++previous)
        {
            if (std::memcmp(impl_->signalOutputs[previous].data(), id, 16) == 0)
            {
                return Result::InvalidArgument;
            }
        }
    }
    for (auto index = std::size_t{}; index < count; ++index)
    {
        std::memcpy(impl_->signalOutputs[index].data(), nodeIds + index * 16,
                    16);
    }
    impl_->signalOutputCount = count;
    return Result::Ok;
}

auto GeneratorPool::dispatchSignal(const std::uint8_t nodeId[16], double time,
                                   std::size_t instructionBudget) noexcept
    -> Result
{
    if (!nodeId || !std::isfinite(time) || !instructionBudget ||
        instructionBudget > 10000)
    {
        return Result::InvalidArgument;
    }
    auto lock = std::unique_lock<std::mutex>(impl_->mutex);
    if (impl_->executing)
    {
        return Result::Busy;
    }
    impl_->executing = true;
    auto result = Result::Ok;
    for (auto &slot : impl_->slots)
    {
        if (!slot->id || !slot->started || !slot->sampleReady ||
            slot->expiring || time < slot->start)
        {
            continue;
        }
        slot->signalTime = time - slot->start;
        slot->signalEffectTime = time;
        lock.unlock();
        auto current = slot->instance->dispatchSignal(nodeId, slot->signalTime,
                                                      instructionBudget);
        lock.lock();
        if (result == Result::Ok)
        {
            result = current;
        }
    }
    impl_->executing = false;
    return result;
}

auto GeneratorPool::dispatchSignal(GeneratorInstanceId id,
                                   const std::uint8_t nodeId[16], double time,
                                   std::size_t instructionBudget) noexcept
    -> Result
{
    if (!nodeId || !std::isfinite(time) || !instructionBudget ||
        instructionBudget > 10000)
    {
        return Result::InvalidArgument;
    }
    auto lock = std::unique_lock<std::mutex>(impl_->mutex);
    if (impl_->executing)
    {
        return Result::Busy;
    }
    auto slot = impl_->find(id);
    if (!slot)
    {
        return Result::InvalidHandle;
    }
    if (!slot->started || !slot->sampleReady || slot->expiring)
    {
        return Result::NotReady;
    }
    if (slot->startResult != Result::Ok)
    {
        return slot->startResult;
    }
    if (time != slot->start + slot->sampledAge)
    {
        return Result::InvalidArgument;
    }
    slot->signalTime = slot->sampledAge;
    slot->signalEffectTime = time;
    impl_->executing = true;
    lock.unlock();
    auto result = slot->instance->dispatchSignal(nodeId, slot->signalTime,
                                                 instructionBudget);
    lock.lock();
    impl_->executing = false;
    return result;
}

auto GeneratorPool::dispatchSignalOwned(const std::uint8_t nodeId[16], double time,
                                       std::size_t instructionBudget,
                                       std::uint64_t ownerTag) noexcept -> Result
{
    if (!nodeId || !std::isfinite(time) || !instructionBudget ||
        instructionBudget > 10000)
    {
        return Result::InvalidArgument;
    }
    auto lock = std::unique_lock<std::mutex>(impl_->mutex);
    if (impl_->executing)
    {
        return Result::Busy;
    }
    impl_->executing = true;
    auto result = Result::Ok;
    for (auto& slot : impl_->slots)
    {
        if (!slot->id || slot->ownerTag != ownerTag || !slot->started ||
            !slot->sampleReady || slot->expiring || time < slot->start)
        {
            continue;
        }
        slot->signalTime = time - slot->start;
        slot->signalEffectTime = time;
        lock.unlock();
        auto current = slot->instance->dispatchSignal(nodeId, slot->signalTime,
                                                      instructionBudget);
        lock.lock();
        if (result == Result::Ok)
        {
            result = current;
        }
    }
    impl_->executing = false;
    return result;
}

auto GeneratorPool::setHostQuality(GeneratorInstanceId id,
                                   const HostQualitySnapshot &snapshot) noexcept
    -> Result
{
    auto lock = std::lock_guard<std::mutex>(impl_->mutex);
    if (impl_->executing)
    {
        return Result::Busy;
    }
    auto slot = impl_->find(id);
    if (slot == nullptr)
    {
        return Result::InvalidHandle;
    }
    return slot->instance->setHostQuality(snapshot);
}

auto GeneratorPool::setTimeContext(GeneratorInstanceId id, double effectTime,
                                   double frameDelta) noexcept -> Result
{
    return setTimeContext(id, effectTime, frameDelta, effectTime, effectTime, frameDelta);
}
auto GeneratorPool::setTimeContext(GeneratorInstanceId id, double effectTime,
    double frameDelta, double realEffectTime, double realInstanceAge, double realFrameDelta) noexcept -> Result
{
    auto lock = std::lock_guard<std::mutex>(impl_->mutex);
    if (impl_->executing)
    {
        return Result::Busy;
    }
    auto slot = impl_->find(id);
    if (!slot)
    {
        return Result::InvalidHandle;
    }
    if (slot->expiring)
    {
        return Result::NotReady;
    }
    return slot->instance->setTimeContext(effectTime, frameDelta, realEffectTime, realInstanceAge, realFrameDelta);
}

auto GeneratorPool::withInstance(GeneratorInstanceId id,
                                 GeneratorInstanceAccess access,
                                 void *context) noexcept -> Result
{
    if (!access)
    {
        return Result::InvalidArgument;
    }
    auto lock = std::unique_lock<std::mutex>(impl_->mutex);
    if (impl_->executing)
    {
        return Result::Busy;
    }
    auto slot = impl_->find(id);
    if (!slot)
    {
        return Result::InvalidHandle;
    }
    if (!slot->instance)
    {
        return Result::NotReady;
    }
    impl_->executing = true;
    lock.unlock();
    auto result = access(context, *slot->instance);
    lock.lock();
    impl_->executing = false;
    return result;
}

auto GeneratorPool::evaluate(GeneratorInstanceId id, double time) noexcept
    -> Result
{
    if (!std::isfinite(time))
    {
        return Result::InvalidArgument;
    }
    auto lock = std::unique_lock<std::mutex>(impl_->mutex);
    if (impl_->executing)
    {
        return Result::Busy;
    }
    auto slot = impl_->find(id);
    if (!slot)
    {
        return Result::InvalidHandle;
    }
    if (slot->expiring)
    {
        return impl_->publishDestroy(*slot);
    }
    if (!slot->started)
    {
        return Result::NotReady;
    }
    if (slot->startResult != Result::Ok)
    {
        return slot->startResult;
    }
    auto elapsed = time - slot->start;
    if (impl_->lifecycle.enableDeferredSignals && slot->sampleReady &&
        time < slot->start + slot->sampledAge)
    {
        return Result::InvalidArgument;
    }
    if (slot->sampleReady && time == slot->start + slot->sampledAge)
    {
        elapsed = slot->sampledAge;
    }
    auto sample = LifetimeSample{};
    if (slot->lifetime.sample(elapsed, sample) != Result::Ok)
    {
        return Result::InvalidArgument;
    }
    auto age = slot->expired ? elapsed : sample.age;
    auto due = double{};
    auto hasSignal = false;
    auto pendingResult = slot->instance->nextDeferredSignal(due, hasSignal);
    if (pendingResult != Result::Ok)
    {
        return pendingResult;
    }
    if (hasSignal)
    {
        if (time == slot->start + due && (slot->expired || due <= sample.age))
        {
            age = due;
        }
        if (age > due)
        {
            return Result::NotReady;
        }
    }
    // Life is captured once by start, never inferred from Busy/FCurve or
    // refreshed by Live inputs. Clamp an overshooting frame to the expiry
    // boundary so Destroy consumers receive the actual final value.
    impl_->executing = true;
    slot->signalTime = age;
    slot->signalEffectTime = slot->start + age;
    lock.unlock();
    auto result = slot->instance->updateAsync(age, inlineEnqueue, nullptr);
    if (result == Result::Ok)
    {
        result = slot->instance->result();
    }
    if (result == Result::Ok)
    {
        result = slot->instance->dispatchGeneratedSignals(
            impl_->lifecycle.instructionBudget
                ? impl_->lifecycle.instructionBudget
                : 10000);
    }
    lock.lock();
    impl_->executing = false;
    if (result == Result::Ok)
    {
        slot->sampledAge = age;
        slot->sampleReady = true;
    }
    auto forceScope = std::uint8_t{};
    if (result == Result::Ok)
    {
        result = slot->instance->pendingForceRequest(forceScope);
    }
    if (result != Result::Ok || forceScope != 0)
    {
        return result;
    }
    if (result == Result::Ok && impl_->lifecycleConfigured &&
        impl_->lifecycle.hasLife && sample.expired)
    {
        slot->expired = true;
        if (!hasSignal)
        {
            slot->expiring = true;
            return impl_->publishDestroy(*slot);
        }
    }
    return result;
}

auto GeneratorPool::nextDeferredSignal(GeneratorInstanceId id, double &time,
                                       bool &hasSignal) noexcept -> Result
{
    auto lock = std::lock_guard<std::mutex>(impl_->mutex);
    if (impl_->executing)
    {
        return Result::Busy;
    }
    auto slot = impl_->find(id);
    if (!slot)
    {
        return Result::InvalidHandle;
    }
    auto due = double{};
    auto result = slot->instance->nextDeferredSignal(due, hasSignal);
    if (result == Result::Ok && hasSignal)
    {
        time = slot->start + due;
        if (!std::isfinite(time))
        {
            return Result::InvalidArgument;
        }
    }
    return result;
}

auto GeneratorPool::dispatchDeferredSignal(
    GeneratorInstanceId id, double time, std::size_t instructionBudget) noexcept
    -> Result
{
    if (!std::isfinite(time) || !instructionBudget || instructionBudget > 10000)
    {
        return Result::InvalidArgument;
    }
    auto lock = std::unique_lock<std::mutex>(impl_->mutex);
    if (impl_->executing)
    {
        return Result::Busy;
    }
    auto slot = impl_->find(id);
    if (!slot)
    {
        return Result::InvalidHandle;
    }
    if (!slot->started || !slot->sampleReady || slot->expiring)
    {
        return Result::NotReady;
    }
    if (slot->startResult != Result::Ok)
    {
        return slot->startResult;
    }
    auto due = double{};
    auto hasSignal = false;
    auto result = slot->instance->nextDeferredSignal(due, hasSignal);
    if (result != Result::Ok)
    {
        return result;
    }
    if (!hasSignal || slot->start + due != time || due != slot->sampledAge)
    {
        return Result::NotReady;
    }
    impl_->executing = true;
    slot->signalTime = due;
    slot->signalEffectTime = time;
    lock.unlock();
    result = slot->instance->dispatchDeferredSignal(due, instructionBudget);
    lock.lock();
    impl_->executing = false;
    if (result != Result::Ok)
    {
        slot->startResult = result;
        return result;
    }
    result = slot->instance->nextDeferredSignal(due, hasSignal);
    if (result == Result::Ok && slot->expired && !hasSignal)
    {
        slot->expiring = true;
        return impl_->publishDestroy(*slot);
    }
    return result;
}

auto GeneratorPool::elapsed(GeneratorInstanceId id, double &out) noexcept
    -> Result
{
    auto lock = std::lock_guard<std::mutex>(impl_->mutex);
    if (impl_->executing)
    {
        return Result::Busy;
    }
    auto slot = impl_->find(id);
    if (!slot)
    {
        return Result::InvalidHandle;
    }
    if (!slot->sampleReady)
    {
        return Result::NotReady;
    }
    out = slot->sampledAge;
    return Result::Ok;
}

auto GeneratorPool::ownerDestroy(GeneratorInstanceId id,
                                 const std::uint8_t nodeId[16], double time,
                                 std::size_t instructionBudget) noexcept
    -> Result
{
    if (!nodeId || !std::isfinite(time) || !instructionBudget ||
        instructionBudget > 10000)
    {
        return Result::InvalidArgument;
    }
    auto lock = std::unique_lock<std::mutex>(impl_->mutex);
    if (impl_->executing)
    {
        return Result::Busy;
    }
    auto slot = impl_->find(id);
    if (!slot)
    {
        return Result::InvalidHandle;
    }
    if (slot->ownerDestroySent)
    {
        return slot->ownerDestroyResult;
    }
    if (!slot->started || !slot->sampleReady || slot->expiring)
    {
        return Result::NotReady;
    }
    if (slot->startResult != Result::Ok)
    {
        return slot->startResult;
    }
    if (time != slot->start + slot->sampledAge)
    {
        return Result::InvalidArgument;
    }
    auto signal = SignalHandle{};
    auto result = slot->instance->bindSignal(nodeId, signal);
    if (result != Result::Ok)
    {
        return result;
    }
    slot->ownerDestroySent = true;
    impl_->executing = true;
    slot->signalTime = slot->sampledAge;
    slot->signalEffectTime = time;
    lock.unlock();
    result = slot->instance->dispatchSignal(signal, slot->sampledAge,
                                            instructionBudget);
    lock.lock();
    slot->ownerDestroyResult = result;
    impl_->executing = false;
    return result;
}

auto GeneratorPool::lifetime(GeneratorInstanceId id,
                             LifetimeSample &out) noexcept -> Result
{
    auto lock = std::lock_guard<std::mutex>(impl_->mutex);
    if (impl_->executing)
    {
        return Result::Busy;
    }
    auto slot = impl_->find(id);
    if (!slot)
    {
        return Result::InvalidHandle;
    }
    if (!slot->sampleReady)
    {
        return Result::NotReady;
    }
    return slot->lifetime.sample(slot->sampledAge, out);
}

auto GeneratorPool::setDebugHooks(GeneratorInstanceId id,
                                  const ExecutionDebugHooks &hooks) noexcept
    -> Result
{
    auto lock = std::lock_guard<std::mutex>(impl_->mutex);
    if (impl_->executing)
    {
        return Result::Busy;
    }
    auto slot = impl_->find(id);
    if (!slot ||
        (hooks.beforeNode && (slot->expired || slot->destroyPublished)))
    {
        return Result::InvalidHandle;
    }
    return slot->instance->setDebugHooks(hooks);
}

auto GeneratorPool::value(GeneratorInstanceId id, const std::uint8_t nodeId[16],
                          double out[4], std::uint8_t &width) noexcept -> Result
{
    if (!nodeId || !out)
    {
        return Result::InvalidArgument;
    }
    auto lock = std::lock_guard<std::mutex>(impl_->mutex);
    if (impl_->executing)
    {
        return Result::Busy;
    }
    auto slot = impl_->find(id);
    return slot ? slot->instance->valueComponents(nodeId, out, width)
                : Result::InvalidHandle;
}

} // namespace liberafx
