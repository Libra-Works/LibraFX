#pragma once

#include "liberafx/Lifetime.h"

namespace liberafx {

// Owner-thread lifecycle for one initialized effect Instance. The host supplies
// scaled, paused elapsed seconds and owns the instance and its work memory.
// Restart requires a freshly initialized instance and a new lifecycle.
class LIBRA_RUNTIME_CORE_API EffectLifecycle final {
public:
    explicit EffectLifecycle(Instance& instance) noexcept : instance_(instance) {}
    EffectLifecycle(const EffectLifecycle&) = delete;
    EffectLifecycle& operator=(const EffectLifecycle&) = delete;

    // OnStart, if present, runs after the first evaluation and before natural
    // expiry is reported. A failed evaluation or signal is never retried.
    Result start(const Lifetime& lifetime, const std::uint8_t* onStartNodeId,
                 std::size_t instructionBudget, LifetimeSample& out) noexcept;
    // Natural expiry is reported once, after evaluation at the exact boundary;
    // dispatching an author-visible Destroy signal remains the host's responsibility.
    Result advance(double elapsed, LifetimeSample& out) noexcept;
    // Manual stop never reports natural expiry.
    Result stop() noexcept;

private:
    enum class Phase : std::uint8_t { Ready, Running, Busy, Expired, Stopped, Failed };
    Instance& instance_;
    Lifetime lifetime_{};
    LifetimeSample last_{};
    Result startFailure_ = Result::Ok;
    Phase phase_ = Phase::Ready;
};

}
