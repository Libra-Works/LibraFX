#include "liberafx/math/LbRandomMath.h"
#include <algorithm>
#include <cmath>

namespace liberafx::math
{
auto mixRandomWord(std::uint32_t value) noexcept -> std::uint32_t
{
    value ^= value >> 16;
    value *= 0x7feb352du;
    value ^= value >> 15;
    value *= 0x846ca68bu;
    value ^= value >> 16;
    return value;
}

auto randomWord(std::uint32_t seed, std::uint32_t sampleIndex, std::uint32_t stream) noexcept -> std::uint32_t
{
    return mixRandomWord(seed ^ mixRandomWord(sampleIndex + 0x9e3779b9u) ^ mixRandomWord(stream + 0x85ebca6bu));
}

auto randomUnit(std::uint32_t seed, std::uint32_t sampleIndex, std::uint32_t stream) noexcept -> double
{
    return static_cast<double>(randomWord(seed, sampleIndex, stream)) / 4294967296.0;
}

auto randomInteger(std::uint32_t seed, std::uint32_t sampleIndex, std::int32_t low, std::int32_t high) noexcept -> std::int32_t
{
    if (low > high)
    {
        std::swap(low, high);
    }
    auto count = static_cast<std::uint64_t>(static_cast<std::int64_t>(high) - low) + 1;
    auto threshold = (std::uint64_t{1} << 32) % count;
    auto stream = std::uint32_t{};
    auto word = randomWord(seed, sampleIndex, stream);
    while (word < threshold)
    {
        ++stream;
        word = randomWord(seed, sampleIndex, stream);
    }
    return static_cast<std::int32_t>(static_cast<std::int64_t>(low) + static_cast<std::int64_t>(word % count));
}

auto randomSphereDirection(std::uint32_t seed, std::uint32_t sampleIndex) noexcept -> LbVector
{
    auto z = 2 * randomUnit(seed, sampleIndex, 0) - 1;
    auto angle = 6.283185307179586476925286766559 * randomUnit(seed, sampleIndex, 1);
    auto radius = std::sqrt(std::max(0.0, 1 - z * z));
    return {radius * std::cos(angle), radius * std::sin(angle), z};
}

auto randomSpherePoint(std::uint32_t seed, std::uint32_t sampleIndex) noexcept -> LbVector
{
    auto direction = randomSphereDirection(seed, sampleIndex);
    auto radius = std::cbrt(randomUnit(seed, sampleIndex, 2));
    for (auto& component : direction)
    {
        component *= radius;
    }
    return direction;
}
}
