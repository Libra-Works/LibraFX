#pragma once

#include "liberafx/Lifetime.h"
#include "liberafx/ScheduledEventQueue.h"
#include <array>

namespace liberafx {

class NamedIo;

// Owner-thread lifecycle for one initialized effect Instance. The host supplies
// scaled, paused elapsed seconds and owns the instance and its work memory.
// The instance must outlive the lifecycle and must not be reinitialized while
// the lifecycle exists.
// Restart requires a freshly initialized instance and a new lifecycle.
class LIBRA_RUNTIME_CORE_API EffectLifecycle final {
public:
    explicit EffectLifecycle(Instance& instance) noexcept : instance_(&instance) {}
    // The fresh facade must wrap this instance and outlive the lifecycle.
    EffectLifecycle(Instance& instance, NamedIo& io) noexcept : instance_(&instance), io_(&io) {}
    ~EffectLifecycle();
    EffectLifecycle(const EffectLifecycle&) = delete;
    EffectLifecycle& operator=(const EffectLifecycle&) = delete;

    // OnStart, if present, runs after the first evaluation and before natural
    // expiry is reported. A failed evaluation or signal is never retried.
    Result start(const Lifetime& lifetime, const std::uint8_t* onStartNodeId,
                 std::size_t instructionBudget, LifetimeSample& out) noexcept;
    // Capture one finite duration from the initial evaluation before OnStart.
    Result startCaptured(const std::uint8_t lifeNodeId[16], const std::uint8_t* onStartNodeId,
                         std::size_t instructionBudget, LifetimeSample& out) noexcept;
    // The lifetime deadline is reported once after the exact boundary update.
    // It is not final destruction while children/continuations are pending.
    Result advance(double elapsed, LifetimeSample& out) noexcept;
    // After the deadline, an owner with pending children/continuations keeps
    // updating the parent until it can finish. No expiry is reported again.
    Result drain(double elapsed) noexcept;
    // The host schedules opaque continuations and delivers them after updating
    // to their timestamp. Pending continuations prevent natural finish.
    Result schedule(double time, std::uint64_t token) noexcept;
    // Owner-thread scoped cancellation, without running a continuation.
    Result cancelScheduled(std::uint64_t token) noexcept;
    Result nextScheduled(ScheduledEvent& out, bool& hasEvent) const noexcept;
    Result takeScheduled(ScheduledEvent& out, bool& hasEvent) noexcept;
    // Signal continuations use this effect's local clock and retain the effect
    // after its deadline. The owner evaluates at the due time before dispatch.
    Result nextDeferredSignal(double& time, bool& hasSignal) const noexcept;
    Result dispatchDeferredSignal(double time, std::size_t instructionBudget) noexcept;
    // The host may end an infinite owner explicitly after it has decided that
    // no new input will be accepted. Pending work still drains before Destroy.
    Result requestNaturalEnd() noexcept;
    // Send the natural root Destroy signal once after owned work is quiescent.
    // Further work created by the signal must drain before finish.
    Result destroy(const std::uint8_t nodeId[16], std::size_t instructionBudget) noexcept;
    Result finish() noexcept;
    // Manual stop never reports natural expiry.
    Result stop() noexcept;
    // Signal dispatch uses the last evaluated age. Hosts must reject external
    // inputs after natural end while allowing internal routes to drain.
    Result validateSignalTime(double elapsed) const noexcept;

private:
    enum class Phase : std::uint8_t { Ready, Running, Busy, Expired, Draining, Finished, Stopped, Failed };
    Result startInternal(Lifetime lifetime, const std::uint8_t* lifeNodeId,
                         const std::uint8_t* onStartNodeId,
                         std::size_t instructionBudget, LifetimeSample& out) noexcept;
    Instance* instance_ = nullptr;
    NamedIo* io_ = nullptr;
    Lifetime lifetime_{};
    LifetimeSample last_{};
    double lastElapsed_ = 0;
    std::size_t instructionBudget_ = 10000;
    Result startFailure_ = Result::Ok;
    Phase phase_ = Phase::Ready;
    bool destroySent_ = false;
    bool ownsDeferredStorage_ = false;
    Result destroyResult_ = Result::Ok;
    std::array<ScheduledEvent, 128> scheduledStorage_{};
    ScheduledEventQueue scheduled_{scheduledStorage_.data(), scheduledStorage_.size()};
    std::array<DeferredSignal, 128> deferredStorage_{};
};

}
