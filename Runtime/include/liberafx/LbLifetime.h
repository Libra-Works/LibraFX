#pragma once

#include "liberafx/LbVfxRuntime.h"
#include <algorithm>
#include <cmath>
#include <limits>

namespace liberafx {

struct LifetimeSample final {
    double age = 0;
    double remaining = 0;
    double normalizedAge = 0;
    bool expired = false;
};

// Immutable duration policy. The owner performs the final update at sample.age
// before publishing natural expiry; stopping or resetting never publishes it.
class Lifetime final {
public:
    static auto infinite() noexcept -> Lifetime { return {}; }

    static auto finite(double seconds, Lifetime& out) noexcept -> Result
    {
        if (!std::isfinite(seconds) || seconds < 0) return Result::InvalidArgument;
        out.duration_ = seconds;
        return Result::Ok;
    }

    auto sample(double elapsed, LifetimeSample& out) const noexcept -> Result
    {
        if (!std::isfinite(elapsed) || elapsed < 0) return Result::InvalidArgument;
        auto value = LifetimeSample{};
        if (std::isinf(duration_)) {
            value.age = elapsed;
            value.remaining = std::numeric_limits<double>::infinity();
        } else {
            value.age = std::min(elapsed, duration_);
            value.remaining = duration_ - value.age;
            value.expired = elapsed >= duration_;
            value.normalizedAge = duration_ == 0 ? 1 : value.age / duration_;
        }
        out = value;
        return Result::Ok;
    }

private:
    double duration_ = std::numeric_limits<double>::infinity();
};

}
