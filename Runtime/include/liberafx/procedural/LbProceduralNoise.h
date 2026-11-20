#pragma once

#include <cmath>
#include <cstdint>

namespace liberafx::procedural_noise {

// Periodic integer lattice. The same integer hash and interpolation are used
// by the trusted texture shaders; sampling does not depend on engine RNG state.
inline auto hash(std::int32_t x, std::int32_t y, std::int32_t z, std::uint32_t seed) -> std::uint32_t
{
    auto value = (static_cast<std::uint32_t>(x) & 4095u) * 0x8da6b343u ^
        (static_cast<std::uint32_t>(y) & 4095u) * 0xd8163841u ^
        (static_cast<std::uint32_t>(z) & 4095u) * 0xcb1ab31fu ^ seed * 0x9e3779b9u;
    value ^= value >> 16;
    value *= 0x7feb352du;
    value ^= value >> 15;
    value *= 0x846ca68bu;
    return value ^ (value >> 16);
}

inline auto unit(std::uint32_t value) -> float
{
    return static_cast<float>(value >> 8) * (1.0f / 16777215.0f);
}
inline auto fade(float t) -> float { return t * t * t * (t * (t * 6 - 15) + 10); }
inline auto mix(float a, float b, float t) -> float { return a + (b - a) * t; }
inline auto periodic(float coordinate) -> float
{
    auto result = std::fmod(coordinate, 4096.0f);
    return result < 0 ? result + 4096.0f : result;
}

inline auto value3(float x, float y, float z, std::uint32_t seed) -> float
{
    x = periodic(x);
    y = periodic(y);
    z = periodic(z);
    const auto ix = static_cast<std::int32_t>(std::floor(x));
    const auto iy = static_cast<std::int32_t>(std::floor(y));
    const auto iz = static_cast<std::int32_t>(std::floor(z));
    const auto fx = fade(x - static_cast<float>(ix));
    const auto fy = fade(y - static_cast<float>(iy));
    const auto fz = fade(z - static_cast<float>(iz));
    const auto sample = [&](std::int32_t dx, std::int32_t dy, std::int32_t dz)
    {
        return unit(hash(ix + dx, iy + dy, iz + dz, seed));
    };
    return mix(mix(mix(sample(0, 0, 0), sample(1, 0, 0), fx),
                   mix(sample(0, 1, 0), sample(1, 1, 0), fx), fy),
               mix(mix(sample(0, 0, 1), sample(1, 0, 1), fx),
                   mix(sample(0, 1, 1), sample(1, 1, 1), fx), fy), fz);
}

inline auto value2(float x, float y, std::uint32_t seed) -> float
{
    x = periodic(x);
    y = periodic(y);
    const auto ix = static_cast<std::int32_t>(std::floor(x));
    const auto iy = static_cast<std::int32_t>(std::floor(y));
    return mix(mix(unit(hash(ix, iy, 0, seed)), unit(hash(ix + 1, iy, 0, seed)), fade(x - ix)),
        mix(unit(hash(ix, iy + 1, 0, seed)), unit(hash(ix + 1, iy + 1, 0, seed)), fade(x - ix)), fade(y - iy));
}

inline auto gradient2(float x, float y, std::uint32_t seed) -> float
{
    x = periodic(x);
    y = periodic(y);
    const auto ix = static_cast<std::int32_t>(std::floor(x));
    const auto iy = static_cast<std::int32_t>(std::floor(y));
    const auto fx = x - static_cast<float>(ix), fy = y - static_cast<float>(iy);
    const auto gradient = [&](std::int32_t dx, std::int32_t dy)
    {
        constexpr float diagonal = .7071067811865475f;
        constexpr float gradients[8][2] = {
            {1, 0}, {-1, 0}, {0, 1}, {0, -1},
            {diagonal, diagonal}, {-diagonal, diagonal}, {diagonal, -diagonal}, {-diagonal, -diagonal}
        };
        const auto index = hash(ix + dx, iy + dy, 0, seed) & 7u;
        return gradients[index][0] * (fx - static_cast<float>(dx)) +
            gradients[index][1] * (fy - static_cast<float>(dy));
    };
    const auto result = mix(mix(gradient(0, 0), gradient(1, 0), fade(fx)),
        mix(gradient(0, 1), gradient(1, 1), fade(fx)), fade(fy));
    return std::fmax(0.0f, std::fmin(1.0f, result * .7071067811865475f + .5f));
}

inline auto cellular2(float x, float y, std::uint32_t seed, bool edge) -> float
{
    x = periodic(x);
    y = periodic(y);
    const auto ix = static_cast<std::int32_t>(std::floor(x));
    const auto iy = static_cast<std::int32_t>(std::floor(y));
    auto nearest = 16.0f, second = 16.0f;
    for (auto dy = -1; dy <= 1; ++dy)
    {
        for (auto dx = -1; dx <= 1; ++dx)
        {
            const auto hx = unit(hash(ix + dx, iy + dy, 0, seed));
            const auto hy = unit(hash(ix + dx, iy + dy, 0, seed ^ 0x68bc21ebu));
            const auto px = static_cast<float>(ix + dx) + hx - x;
            const auto py = static_cast<float>(iy + dy) + hy - y;
            const auto distance = px * px + py * py;
            if (distance < nearest)
            {
                second = nearest;
                nearest = distance;
            }
            else if (distance < second)
            {
                second = distance;
            }
        }
    }
    const auto result = edge ? std::sqrt(second) - std::sqrt(nearest) : std::sqrt(nearest);
    return std::fmax(0.0f, std::fmin(1.0f, result));
}

inline auto fractal3(float x, float y, float z, std::uint32_t seed,
    std::uint32_t octaves, float roughness) -> float
{
    auto sum = 0.0f, weight = 1.0f, total = 0.0f;
    for (auto octave = 0u; octave < octaves; ++octave)
    {
        sum += value3(x, y, z, seed + octave * 0x9e3779b9u) * weight;
        total += weight;
        weight *= roughness;
        if (weight == 0)
        {
            break;
        }
        x *= 2;
        y *= 2;
        z *= 2;
    }
    return total > 0 ? sum / total : .5f;
}

}
