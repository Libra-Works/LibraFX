#include "liberafx/EffectLifecycle.h"
#include "liberafx/NamedIo.h"
#include <array>

namespace liberafx {
namespace {
auto inlineJob(void*, Job job, void* context) noexcept -> bool
{
    job(context);
    return true;
}
}

EffectLifecycle::~EffectLifecycle()
{
    if (ownsDeferredStorage_) {
        (void)instance_->clearDeferredSignals();
        (void)instance_->setDeferredSignalStorage(nullptr, 0);
    }
}

auto EffectLifecycle::start(const Lifetime& lifetime, const std::uint8_t* onStartNodeId,
                            std::size_t instructionBudget, LifetimeSample& out) noexcept -> Result
{
    return startInternal(lifetime, nullptr, onStartNodeId, instructionBudget, out);
}

auto EffectLifecycle::startCaptured(const std::uint8_t lifeNodeId[16], const std::uint8_t* onStartNodeId,
                                    std::size_t instructionBudget, LifetimeSample& out) noexcept -> Result
{
    if (!lifeNodeId) return Result::InvalidArgument;
    return startInternal(Lifetime::infinite(), lifeNodeId, onStartNodeId, instructionBudget, out);
}

auto EffectLifecycle::startInternal(Lifetime lifetime, const std::uint8_t* lifeNodeId,
                                    const std::uint8_t* onStartNodeId,
                                    std::size_t instructionBudget, LifetimeSample& out) noexcept -> Result
{
    if (phase_ == Phase::Busy) return Result::Busy;
    if (phase_ == Phase::Failed) return startFailure_;
    if (phase_ != Phase::Ready) return Result::NotReady;
    auto signal = SignalHandle{};
    if (onStartNodeId) {
        if (!instructionBudget) return Result::InvalidArgument;
        auto result = instance_->bindSignal(onStartNodeId, signal);
        if (result != Result::Ok) return result;
    }
    auto life = ValueHandle{};
    if (lifeNodeId) {
        auto result = instance_->bindValue(lifeNodeId, life);
        if (result != Result::Ok) return result;
        if (life.componentCount != 1) return Result::InvalidValueType;
    }
    auto storageResult = instance_->setDeferredSignalStorage(deferredStorage_.data(), deferredStorage_.size());
    if (storageResult != Result::Ok) return storageResult;
    ownsDeferredStorage_ = true;
    instructionBudget_ = instructionBudget ? instructionBudget : 10000;
    phase_ = Phase::Busy;
    auto result = Result::Ok;
    if (io_) {
        result = lifeNodeId ? io_->startCaptured(0, onStartNodeId, instructionBudget, lifeNodeId, lifetime) :
                               io_->start(0, onStartNodeId, instructionBudget);
        if (result != Result::Ok) {
            startFailure_ = result;
            phase_ = Phase::Failed;
            return result;
        }
    } else {
        result = instance_->updateAsync(0, inlineJob, nullptr);
        if (result != Result::Ok) {
            phase_ = Phase::Ready;
            return result;
        }
        result = instance_->result();
        if (result != Result::Ok) {
            startFailure_ = result;
            phase_ = Phase::Failed;
            return result;
        }
        if (lifeNodeId) {
            auto value = std::array<double, 4>{};
            result = instance_->value(life, value.data());
            if (result == Result::Ok) result = Lifetime::finite(value[0], lifetime);
            if (result != Result::Ok) {
                startFailure_ = result;
                phase_ = Phase::Failed;
                return result;
            }
        }
        result = instance_->dispatchGeneratedSignals(instructionBudget_);
        if (result != Result::Ok) {
            startFailure_ = result;
            phase_ = Phase::Failed;
            return result;
        }
        if (onStartNodeId) {
            result = instance_->dispatchSignal(signal, 0, instructionBudget);
            if (result != Result::Ok) {
                startFailure_ = result;
                phase_ = Phase::Failed;
                return result;
            }
        }
    }
    auto sample = LifetimeSample{};
    result = lifetime.sample(0, sample);
    if (result != Result::Ok) {
        startFailure_ = result;
        phase_ = Phase::Failed;
        return result;
    }
    lifetime_ = lifetime;
    last_ = sample;
    lastElapsed_ = sample.age;
    phase_ = sample.expired ? Phase::Expired : Phase::Running;
    out = sample;
    return Result::Ok;
}

auto EffectLifecycle::advance(double elapsed, LifetimeSample& out) noexcept -> Result
{
    if (phase_ == Phase::Busy) return Result::Busy;
    if (phase_ != Phase::Running) return Result::NotReady;
    auto sample = LifetimeSample{};
    auto result = lifetime_.sample(elapsed, sample);
    if (result != Result::Ok || elapsed < last_.age) return Result::InvalidArgument;
    auto next = ScheduledEvent{};
    if (scheduled_.peek(next) && sample.age > next.time) return Result::NotReady;
    auto deferredTime = double{};
    auto hasDeferred = false;
    result = instance_->nextDeferredSignal(deferredTime, hasDeferred);
    if (result != Result::Ok) return result;
    if (hasDeferred && sample.age > deferredTime) return Result::NotReady;
    if (elapsed == last_.age) {
        out = last_;
        return Result::Ok;
    }
    phase_ = Phase::Busy;
    if (io_) {
        result = io_->update(sample.age);
    } else {
        result = instance_->updateAsync(sample.age, inlineJob, nullptr);
        if (result != Result::Ok) {
            phase_ = Phase::Running;
            return result;
        }
        result = instance_->result();
        if (result == Result::Ok) result = instance_->dispatchGeneratedSignals(instructionBudget_);
    }
    if (result != Result::Ok) {
        startFailure_ = result;
        phase_ = Phase::Failed;
        return result;
    }
    last_ = sample;
    lastElapsed_ = sample.age;
    phase_ = sample.expired ? Phase::Expired : Phase::Running;
    out = sample;
    return Result::Ok;
}

auto EffectLifecycle::drain(double elapsed) noexcept -> Result
{
    if (phase_ == Phase::Busy) return Result::Busy;
    if (phase_ != Phase::Expired && phase_ != Phase::Draining) return Result::NotReady;
    if (!std::isfinite(elapsed) || elapsed < lastElapsed_) return Result::InvalidArgument;
    auto next = ScheduledEvent{};
    if (scheduled_.peek(next) && elapsed > next.time) return Result::NotReady;
    auto deferredTime = double{};
    auto hasDeferred = false;
    auto deferredResult = instance_->nextDeferredSignal(deferredTime, hasDeferred);
    if (deferredResult != Result::Ok) return deferredResult;
    if (hasDeferred && elapsed > deferredTime) return Result::NotReady;
    if (elapsed == lastElapsed_) {
        phase_ = Phase::Draining;
        return Result::Ok;
    }
    phase_ = Phase::Busy;
    auto result = io_ ? io_->update(elapsed) : instance_->updateAsync(elapsed, inlineJob, nullptr);
    if (!io_ && result == Result::Ok) result = instance_->result();
    if (!io_ && result == Result::Ok) result = instance_->dispatchGeneratedSignals(instructionBudget_);
    if (result != Result::Ok) {
        startFailure_ = result;
        phase_ = Phase::Failed;
        return result;
    }
    lastElapsed_ = elapsed;
    phase_ = Phase::Draining;
    return Result::Ok;
}

auto EffectLifecycle::schedule(double time, std::uint64_t token) noexcept -> Result
{
    if (phase_ == Phase::Busy) return Result::Busy;
    if (phase_ != Phase::Running && phase_ != Phase::Expired && phase_ != Phase::Draining)
        return Result::NotReady;
    if (!std::isfinite(time) || time < lastElapsed_) return Result::InvalidArgument;
    return scheduled_.schedule(time, token);
}

auto EffectLifecycle::cancelScheduled(std::uint64_t token) noexcept -> Result
{
    if (!token) return Result::InvalidArgument;
    if (phase_ == Phase::Busy) return Result::Busy;
    scheduled_.cancel(token);
    return Result::Ok;
}

auto EffectLifecycle::nextScheduled(ScheduledEvent& out, bool& hasEvent) const noexcept -> Result
{
    if (phase_ == Phase::Busy) return Result::Busy;
    if (phase_ != Phase::Running && phase_ != Phase::Expired && phase_ != Phase::Draining)
        return Result::NotReady;
    hasEvent = scheduled_.peek(out);
    return Result::Ok;
}

auto EffectLifecycle::takeScheduled(ScheduledEvent& out, bool& hasEvent) noexcept -> Result
{
    if (phase_ == Phase::Busy) return Result::Busy;
    if (phase_ != Phase::Running && phase_ != Phase::Expired && phase_ != Phase::Draining)
        return Result::NotReady;
    hasEvent = false;
    auto next = ScheduledEvent{};
    if (!scheduled_.peek(next) || next.time > lastElapsed_) return Result::Ok;
    hasEvent = scheduled_.pop(out);
    return Result::Ok;
}

auto EffectLifecycle::nextDeferredSignal(double& time, bool& hasSignal) const noexcept -> Result
{
    if (phase_ == Phase::Busy) return Result::Busy;
    if (phase_ != Phase::Running && phase_ != Phase::Expired && phase_ != Phase::Draining)
        return Result::NotReady;
    return instance_->nextDeferredSignal(time, hasSignal);
}

auto EffectLifecycle::dispatchDeferredSignal(double time, std::size_t instructionBudget) noexcept -> Result
{
    if (phase_ == Phase::Busy) return Result::Busy;
    if (phase_ != Phase::Running && phase_ != Phase::Expired && phase_ != Phase::Draining)
        return Result::NotReady;
    if (!instructionBudget || instructionBudget > 10000 || time != lastElapsed_)
        return Result::InvalidArgument;
    auto previous = phase_;
    phase_ = Phase::Busy;
    auto result = io_ ? io_->fireDeferredSignal(time, instructionBudget) :
                        instance_->dispatchDeferredSignal(time, instructionBudget);
    if (result == Result::NotReady) {
        phase_ = previous;
        return result;
    }
    if (result != Result::Ok) {
        startFailure_ = result;
        phase_ = Phase::Failed;
        return result;
    }
    phase_ = previous;
    return Result::Ok;
}

auto EffectLifecycle::requestNaturalEnd() noexcept -> Result
{
    if (phase_ == Phase::Busy) return Result::Busy;
    if (phase_ == Phase::Expired || phase_ == Phase::Draining) return Result::Ok;
    if (phase_ != Phase::Running) return Result::NotReady;
    phase_ = Phase::Draining;
    return Result::Ok;
}

auto EffectLifecycle::destroy(const std::uint8_t nodeId[16], std::size_t instructionBudget) noexcept -> Result
{
    if (!nodeId || !instructionBudget || instructionBudget > 10000) return Result::InvalidArgument;
    if (phase_ == Phase::Busy) return Result::Busy;
    if (destroySent_) return destroyResult_;
    if ((phase_ != Phase::Expired && phase_ != Phase::Draining) || scheduled_.size())
        return Result::NotReady;
    auto deferredTime = double{};
    auto hasDeferred = false;
    auto pendingResult = instance_->nextDeferredSignal(deferredTime, hasDeferred);
    if (pendingResult != Result::Ok) return pendingResult;
    if (hasDeferred) return Result::NotReady;
    auto signal = SignalHandle{};
    auto result = instance_->bindSignal(nodeId, signal);
    if (result != Result::Ok) return result;
    destroySent_ = true;
    phase_ = Phase::Busy;
    result = io_ ? io_->fireLifecycleSignal(signal, lastElapsed_, instructionBudget) :
                   instance_->dispatchSignal(signal, lastElapsed_, instructionBudget);
    destroyResult_ = result;
    if (result != Result::Ok) {
        startFailure_ = result;
        phase_ = Phase::Failed;
        return result;
    }
    phase_ = Phase::Draining;
    return Result::Ok;
}

auto EffectLifecycle::finish() noexcept -> Result
{
    if (phase_ == Phase::Busy) return Result::Busy;
    if (phase_ != Phase::Expired && phase_ != Phase::Draining) return Result::NotReady;
    if (scheduled_.size()) return Result::NotReady;
    auto deferredTime = double{};
    auto hasDeferred = false;
    auto result = instance_->nextDeferredSignal(deferredTime, hasDeferred);
    if (result != Result::Ok) return result;
    if (hasDeferred) return Result::NotReady;
    phase_ = Phase::Finished;
    return Result::Ok;
}

auto EffectLifecycle::stop() noexcept -> Result
{
    if (phase_ == Phase::Busy) return Result::Busy;
    if (phase_ != Phase::Running && phase_ != Phase::Expired &&
        phase_ != Phase::Draining && phase_ != Phase::Failed) return Result::NotReady;
    scheduled_.clear();
    auto result = instance_->clearDeferredSignals();
    if (result != Result::Ok) return result;
    phase_ = Phase::Stopped;
    return Result::Ok;
}

auto EffectLifecycle::validateSignalTime(double elapsed) const noexcept -> Result
{
    if (phase_ == Phase::Busy) return Result::Busy;
    if (phase_ != Phase::Running && phase_ != Phase::Draining) return Result::NotReady;
    return std::isfinite(elapsed) && elapsed == lastElapsed_ ? Result::Ok : Result::InvalidArgument;
}

}
