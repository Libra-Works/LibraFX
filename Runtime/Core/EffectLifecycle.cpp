#include "liberafx/EffectLifecycle.h"

namespace liberafx {
namespace {
auto inlineJob(void*, Job job, void* context) noexcept -> bool
{
    job(context);
    return true;
}
}

auto EffectLifecycle::start(const Lifetime& lifetime, const std::uint8_t* onStartNodeId,
                            std::size_t instructionBudget, LifetimeSample& out) noexcept -> Result
{
    if (phase_ == Phase::Busy) return Result::Busy;
    if (phase_ == Phase::Failed) return startFailure_;
    if (phase_ != Phase::Ready) return Result::NotReady;
    auto signal = SignalHandle{};
    if (onStartNodeId) {
        if (!instructionBudget) return Result::InvalidArgument;
        auto result = instance_.bindSignal(onStartNodeId, signal);
        if (result != Result::Ok) return result;
    }
    auto sample = LifetimeSample{};
    auto result = lifetime.sample(0, sample);
    if (result != Result::Ok) return result;
    phase_ = Phase::Busy;
    result = instance_.updateAsync(0, inlineJob, nullptr);
    if (result != Result::Ok) {
        phase_ = Phase::Ready;
        return result;
    }
    result = instance_.result();
    if (result != Result::Ok) {
        startFailure_ = result;
        phase_ = Phase::Failed;
        return result;
    }
    if (onStartNodeId) {
        result = instance_.dispatchSignal(signal, 0, instructionBudget);
        if (result != Result::Ok) {
            startFailure_ = result;
            phase_ = Phase::Failed;
            return result;
        }
    }
    lifetime_ = lifetime;
    last_ = sample;
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
    if (elapsed == last_.age) {
        out = last_;
        return Result::Ok;
    }
    phase_ = Phase::Busy;
    result = instance_.updateAsync(sample.age, inlineJob, nullptr);
    if (result != Result::Ok) {
        phase_ = Phase::Running;
        return result;
    }
    result = instance_.result();
    if (result != Result::Ok) {
        startFailure_ = result;
        phase_ = Phase::Failed;
        return result;
    }
    last_ = sample;
    phase_ = sample.expired ? Phase::Expired : Phase::Running;
    out = sample;
    return Result::Ok;
}

auto EffectLifecycle::stop() noexcept -> Result
{
    if (phase_ == Phase::Busy) return Result::Busy;
    if (phase_ != Phase::Running && phase_ != Phase::Failed) return Result::NotReady;
    phase_ = Phase::Stopped;
    return Result::Ok;
}

}
