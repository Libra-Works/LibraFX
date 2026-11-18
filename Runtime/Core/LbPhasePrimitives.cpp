#include "liberafx/LbPhasePrimitives.h"
#include <cmath>
#include <limits>

namespace liberafx {
namespace {
auto validAge(double age) noexcept -> bool { return std::isfinite(age) && age >= 0; }
auto maxRevision() noexcept -> std::uint64_t
{
    return std::numeric_limits<std::uint64_t>::max();
}
}

auto PhaseClock::reset(double age, std::uint32_t phase) noexcept -> Result
{
    if (!validAge(age)) return Result::InvalidArgument;
    if (revision_ == maxRevision()) return Result::CounterOverflow;
    age_ = phaseStart_ = age;
    phase_ = phase;
    ++revision_;
    return Result::Ok;
}

auto PhaseClock::advance(double age, PhaseSample& out) noexcept -> Result
{
    if (!revision_) return Result::NotReady;
    if (!validAge(age) || age < age_) return Result::InvalidArgument;
    age_ = age;
    out = sample();
    return Result::Ok;
}

auto PhaseClock::enter(double age, std::uint32_t phase) noexcept -> Result
{
    if (!revision_) return Result::NotReady;
    if (!validAge(age) || age < age_) return Result::InvalidArgument;
    return reset(age, phase);
}

auto PhaseClock::sample() const noexcept -> PhaseSample
{
    return {phase_, revision_, age_, age_ - phaseStart_};
}

auto LoopClock::reset(double age, double length, bool bounded, std::uint64_t count) noexcept -> Result
{
    if (!validAge(age) || !validAge(length) || (!bounded && length == 0))
        return Result::InvalidArgument;
    if (bounded && (!std::isfinite(length * static_cast<double>(count)) ||
        !std::isfinite(age + length * static_cast<double>(count))))
        return Result::InvalidArgument;
    if (revision_ == maxRevision()) return Result::CounterOverflow;
    start_ = age_ = age;
    length_ = length;
    bounded_ = bounded;
    count_ = count;
    boundaries_ = 0;
    ++revision_;
    return Result::Ok;
}

auto LoopClock::advance(double age, LoopSample& out) noexcept -> Result
{
    if (!revision_) return Result::NotReady;
    if (!validAge(age) || age < age_) return Result::InvalidArgument;
    auto sample = LoopSample{};
    sample.revision = revision_;
    sample.overallAge = age;
    sample.phaseTime = age - start_;
    auto duration = bounded_ ? length_ * static_cast<double>(count_) : 0;
    sample.complete = bounded_ && sample.phaseTime >= duration;
    if (sample.complete) {
        sample.boundaryCount = count_;
        sample.loopTime = count_ ? length_ : 0;
    } else {
        auto quotient = std::floor(static_cast<long double>(sample.phaseTime) / length_);
        // Comparing to 2^64, not double(UINT64_MAX), avoids an undefined cast
        // on implementations where long double has only double precision.
        if (quotient >= std::ldexp(static_cast<long double>(1), 64))
            return Result::CounterOverflow;
        sample.boundaryCount = static_cast<std::uint64_t>(quotient);
        // The final boundary is governed by the explicit duration comparison
        // above, not a division rounded up next to that endpoint.
        if (bounded_ && sample.boundaryCount >= count_) sample.boundaryCount = count_ - 1;
        sample.loopTime = std::fmod(sample.phaseTime, length_);
    }
    sample.loopIndex = sample.boundaryCount;
    sample.boundariesCrossed = sample.boundaryCount - boundaries_;
    age_ = age;
    boundaries_ = sample.boundaryCount;
    out = sample;
    return Result::Ok;
}

void FutureBoundaryArm::arm(const LoopSample& sample) noexcept
{
    revision_ = sample.revision;
    after_ = sample.boundaryCount;
    armed_ = revision_ != 0 && !sample.complete;
}

void FutureBoundaryArm::clear() noexcept { armed_ = false; }

auto FutureBoundaryArm::consume(const LoopSample& sample) noexcept -> bool
{
    if (!armed_) return false;
    if (sample.revision != revision_) {
        clear();
        return false;
    }
    if (sample.boundaryCount <= after_) return false;
    clear();
    return true;
}

auto EndRequestState::notify() noexcept -> Result
{
    if (arrivals == maxRevision()) return Result::CounterOverflow;
    ++arrivals;
    return Result::Ok;
}
}
