#pragma once

#include "liberafx/LbVfxRuntime.h"

namespace liberafx {

// All state belongs to the caller, not to the Program. No allocation, lifetime
// inference, automatic phase transition, signal dispatch or destruction occurs.
struct PhaseSample final {
    std::uint32_t phase = 0;
    std::uint64_t revision = 0;
    double overallAge = 0;
    double phaseTime = 0;
};

struct LIBRA_RUNTIME_CORE_API PhaseClock final {
    // Reset/seek is explicit and invalidates boundary arms via a new revision.
    Result reset(double overallAge, std::uint32_t initialPhase) noexcept;
    Result advance(double overallAge, PhaseSample& out) noexcept;
    // Entering even the same phase restarts its clock. State/random streams and
    // children are NOT reset. The caller chooses which phase graph to dispatch.
    Result enter(double overallAge, std::uint32_t phase) noexcept;
    PhaseSample sample() const noexcept;

private:
    double age_ = 0;
    double phaseStart_ = 0;
    std::uint64_t revision_ = 0;
    std::uint32_t phase_ = 0;
};

struct LoopSample final {
    std::uint64_t revision = 0;
    // Half-open iterations [start,end); at completion index == iteration count.
    std::uint64_t loopIndex = 0;
    std::uint64_t boundaryCount = 0;
    std::uint64_t boundariesCrossed = 0;
    double overallAge = 0;
    double phaseTime = 0;
    double loopTime = 0;
    bool complete = false;
};

struct LIBRA_RUNTIME_CORE_API LoopClock final {
    // count is ignored unless bounded. Zero count completes immediately.
    // Bounded zero-length loops cross all count boundaries at reset, once.
    // Unbounded zero-length loops are rejected (infinitely many events).
    Result reset(double overallAge, double length, bool bounded,
                 std::uint64_t count) noexcept;
    // O(1), including long jumps; boundaries are a count, not allocated events.
    // No implicit repeat dispatch: the caller explicitly handles this count.
    // All failures leave the clock and output unchanged.
    Result advance(double overallAge, LoopSample& out) noexcept;

private:
    double start_ = 0;
    double age_ = 0;
    double length_ = 0;
    std::uint64_t revision_ = 0;
    std::uint64_t count_ = 0;
    std::uint64_t boundaries_ = 0;
    bool bounded_ = false;
};

struct LIBRA_RUNTIME_CORE_API FutureBoundaryArm final {
    // First advance the clock to the request's age, then arm. A boundary at or
    // before this sample is never accepted, including repeated same-tick calls.
    void arm(const LoopSample& requestSample) noexcept;
    void clear() noexcept;
    // A reset invalidates the arm. Multiple arrivals are NOT time-spanning AND.
    bool consume(const LoopSample& sample) noexcept;
    bool armed() const noexcept { return armed_; }

private:
    std::uint64_t revision_ = 0;
    std::uint64_t after_ = 0;
    bool armed_ = false;
};

// Optional caller-owned notification counter; EndRequest has no fixed policy.
// Ignore it, act immediately, or arm a FutureBoundaryArm using the current clock.
struct LIBRA_RUNTIME_CORE_API EndRequestState final {
    std::uint64_t arrivals = 0;
    Result notify() noexcept;
};

}
