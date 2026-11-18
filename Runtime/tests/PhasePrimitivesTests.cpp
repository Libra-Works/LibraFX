#include "liberafx/PhasePrimitives.h"
#ifdef NDEBUG
#undef NDEBUG
#endif
#include <cassert>
#include <limits>

namespace {
using liberafx::Result;

void composableSingleInstance()
{
    auto phase = liberafx::PhaseClock{};
    auto loop = liberafx::LoopClock{};
    auto arm = liberafx::FutureBoundaryArm{};
    auto requests = liberafx::EndRequestState{};
    auto phaseSample = liberafx::PhaseSample{};
    auto loopSample = liberafx::LoopSample{};
    // User phases: 10=Start, 20=Loop, 30=End; no built-in policy.
    assert(phase.reset(0, 10) == Result::Ok);
    assert(phase.advance(1, phaseSample) == Result::Ok && phaseSample.phaseTime == 1);
    assert(phase.enter(1, 20) == Result::Ok);
    assert(loop.reset(1, 2, false, 0) == Result::Ok);
    assert(loop.advance(5, loopSample) == Result::Ok && loopSample.boundaryCount == 2);
    assert(requests.notify() == Result::Ok && requests.notify() == Result::Ok);
    assert(requests.arrivals == 2); // Notification does not select End.
    assert(phase.sample().phase == 20);
    arm.arm(loopSample); // Request at a boundary must NOT consume this boundary.
    assert(!arm.consume(loopSample));
    assert(loop.advance(6, loopSample) == Result::Ok && !arm.consume(loopSample));
    assert(loop.advance(7, loopSample) == Result::Ok && arm.consume(loopSample));
    assert(!arm.consume(loopSample)); // exactly once
    assert(phase.enter(7, 30) == Result::Ok);
    assert(phase.advance(8, phaseSample) == Result::Ok);
    assert(phaseSample.overallAge == 8 && phaseSample.phaseTime == 1);
    // The effect still exists. End completion, natural end or force is a caller
    // decision; neither this clock nor its curve/phase length supplies Lifetime.
    assert(phase.advance(800, phaseSample) == Result::Ok && phaseSample.phase == 30);

    // Alternative user wiring: the very same notification selects End now.
    assert(phase.reset(0, 10) == Result::Ok);
    assert(phase.enter(1, 20) == Result::Ok);
    assert(requests.notify() == Result::Ok);
    assert(phase.enter(1.25, 30) == Result::Ok);
    assert(phase.sample().overallAge == 1.25 && phase.sample().phaseTime == 0);
}

void loopsAndReset()
{
    auto loop = liberafx::LoopClock{};
    auto sample = liberafx::LoopSample{};
    assert(loop.advance(0, sample) == Result::NotReady);
    assert(loop.reset(10, 0.5, true, 1000000) == Result::Ok);
    assert(loop.advance(400010.25, sample) == Result::Ok);
    assert(!sample.complete && sample.loopIndex == 800000 &&
        sample.boundariesCrossed == 800000 && sample.loopTime == 0.25);
    assert(loop.advance(600010, sample) == Result::Ok);
    assert(sample.complete && sample.loopIndex == 1000000 &&
        sample.boundariesCrossed == 200000 && sample.loopTime == 0.5);
    assert(loop.advance(900010, sample) == Result::Ok && sample.boundariesCrossed == 0);

    assert(loop.reset(0, 1, false, 0) == Result::Ok);
    assert(loop.advance(2, sample) == Result::Ok);
    auto arm = liberafx::FutureBoundaryArm{};
    arm.arm(sample);
    assert(loop.reset(0, 1, false, 0) == Result::Ok); // seek/reset invalidates old arm
    assert(loop.advance(100, sample) == Result::Ok && !arm.consume(sample) && !arm.armed());
    auto unchanged = sample;
    assert(loop.advance(99, sample) == Result::InvalidArgument);
    assert(sample.overallAge == unchanged.overallAge);
    assert(loop.advance(100, sample) == Result::Ok && sample.boundariesCrossed == 0);
}

void zeroAndOverflow()
{
    auto loop = liberafx::LoopClock{};
    auto sample = liberafx::LoopSample{};
    assert(loop.reset(3, 5, true, 0) == Result::Ok);
    assert(loop.advance(3, sample) == Result::Ok && sample.complete &&
        sample.loopIndex == 0 && sample.boundariesCrossed == 0);
    auto arm = liberafx::FutureBoundaryArm{};
    arm.arm(sample);
    assert(!arm.armed()); // no future boundary exists
    assert(loop.reset(3, 0, true, 4) == Result::Ok);
    assert(loop.advance(3, sample) == Result::Ok && sample.complete &&
        sample.boundariesCrossed == 4 && sample.loopTime == 0);
    assert(loop.advance(4, sample) == Result::Ok && sample.boundariesCrossed == 0);
    assert(loop.reset(0, 0, false, 0) == Result::InvalidArgument);
    assert(loop.reset(0, 1, false, 0) == Result::Ok);
    assert(loop.advance(1e30, sample) == Result::CounterOverflow);
    assert(loop.advance(2, sample) == Result::Ok && sample.boundariesCrossed == 2);
    assert(loop.reset(0, std::numeric_limits<double>::infinity(), true, 1) == Result::InvalidArgument);
    auto requests = liberafx::EndRequestState{};
    requests.arrivals = std::numeric_limits<std::uint64_t>::max();
    assert(requests.notify() == Result::CounterOverflow);
}
}

int main()
{
    composableSingleInstance();
    loopsAndReset();
    zeroAndOverflow();
}
