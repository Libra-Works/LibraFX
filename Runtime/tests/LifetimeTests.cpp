#include "liberafx/LbLifetime.h"
#include <cassert>
#include <cmath>
#include <limits>

int main()
{
    auto lifetime = liberafx::Lifetime::infinite();
    auto sample = liberafx::LifetimeSample{};
    assert(lifetime.sample(12, sample) == liberafx::Result::Ok);
    assert(sample.age == 12 && std::isinf(sample.remaining) &&
           sample.normalizedAge == 0 && !sample.expired);
    assert(liberafx::Lifetime::finite(4, lifetime) == liberafx::Result::Ok);
    assert(lifetime.sample(1, sample) == liberafx::Result::Ok);
    assert(sample.age == 1 && sample.remaining == 3 && sample.normalizedAge == 0.25 && !sample.expired);
    assert(lifetime.sample(7, sample) == liberafx::Result::Ok);
    assert(sample.age == 4 && sample.remaining == 0 && sample.normalizedAge == 1 && sample.expired);
    assert(liberafx::Lifetime::finite(0, lifetime) == liberafx::Result::Ok);
    assert(lifetime.sample(0, sample) == liberafx::Result::Ok);
    assert(sample.age == 0 && sample.remaining == 0 && sample.normalizedAge == 1 && sample.expired);
    assert(liberafx::Lifetime::finite(-1, lifetime) == liberafx::Result::InvalidArgument);
    assert(liberafx::Lifetime::finite(std::numeric_limits<double>::infinity(), lifetime) == liberafx::Result::InvalidArgument);
    assert(liberafx::Lifetime::finite(std::numeric_limits<double>::quiet_NaN(), lifetime) == liberafx::Result::InvalidArgument);
    assert(lifetime.sample(-1, sample) == liberafx::Result::InvalidArgument);
    assert(lifetime.sample(std::numeric_limits<double>::infinity(), sample) == liberafx::Result::InvalidArgument);
    assert(lifetime.sample(std::numeric_limits<double>::quiet_NaN(), sample) == liberafx::Result::InvalidArgument);
    assert(sample.age == 0 && sample.remaining == 0 && sample.normalizedAge == 1 && sample.expired);
}
