#pragma once
#include "liberafx/LbVfxRuntime.h"
#include <algorithm>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <vector>

namespace liberafx
{
struct ParticleSpriteSample final
{
    double uv[4]{0, 0, 1, 1};
    double x = 0, y = 0, rotation = 0, scaleX = 1, scaleY = 1;
    std::uint32_t cell = 0;
};
// Immutable after configure: no JSON/engine objects and no per-particle allocations.
// CPU keeps double precision; GPU adapters upload the same float4 record layout.
class LIBRA_RUNTIME_CORE_API ParticleSpriteAnimation final
{
    std::vector<double> values_;
    std::size_t parts_ = 0;
    static bool integer(double value, std::size_t limit) noexcept
    {
        return value >= 0 && value <= static_cast<double>(limit) && std::floor(value) == value;
    }

  public:
    std::size_t partCount() const noexcept
    {
        return parts_;
    }
    const std::vector<double> &values() const noexcept
    {
        return values_;
    }
    bool textureMatches(std::size_t width, std::size_t height) const noexcept
    {
        for (auto part = std::size_t{}; part < parts_; ++part)
        {
            if (values_[part * 12 + 6] != width || values_[part * 12 + 7] != height)
            {
                return false;
            }
        }
        return true;
    }
    bool gpuCompatible() const noexcept
    {
        for (auto part = std::size_t{}; part < parts_; ++part)
        {
            const auto *h = values_.data() + part * 12;
            for (auto channel = 0; channel < 2; ++channel)
            {
                const auto offset = static_cast<std::size_t>(h[channel == 0 ? 2 : 4]) * 4;
                const auto count = static_cast<std::size_t>(h[channel == 0 ? 3 : 5]);
                auto previous = channel == 0 ? 0.0f : -1.0f;
                for (auto i = std::size_t{}; i < count; ++i)
                {
                    const auto current = static_cast<float>(values_[offset + i * 8]);
                    if (current <= previous)
                    {
                        return false;
                    }
                    previous = current;
                }
            }
        }
        return true;
    }
    Result configure(const double *values, std::size_t count, std::size_t parts) noexcept;
    Result sample(std::size_t part, double age, ParticleSpriteSample &out) const noexcept
    {
        out = {};
        if (part >= parts_ || !std::isfinite(age) || age < 0)
        {
            return Result::InvalidArgument;
        }
        const auto *h = values_.data() + part * 12;
        auto ticks = age * h[0];
        if (!std::isfinite(ticks))
        {
            return Result::NonFinite;
        }
        ticks = h[8] != 0 ? std::fmod(ticks, h[1]) : std::min(ticks, h[1]);
        const auto frames = static_cast<std::size_t>(h[3]), offset = static_cast<std::size_t>(h[2]) * 4;
        auto low = std::size_t{}, high = frames - 1;
        while (low < high)
        {
            const auto middle = (low + high) / 2;
            if (ticks < values_[offset + middle * 8])
            {
                high = middle;
            }
            else
            {
                low = middle + 1;
            }
        }
        const auto *f = values_.data() + offset + low * 8;
        out.uv[0] = f[1];
        out.uv[1] = f[2];
        out.uv[2] = f[3];
        out.uv[3] = f[4];
        out.cell = static_cast<std::uint32_t>(f[7]);
        if (h[9] == 0)
        {
            return Result::Ok;
        }
        out.scaleX = f[3];
        out.scaleY = f[4];
        const auto keys = static_cast<std::size_t>(h[5]), keyOffset = static_cast<std::size_t>(h[4]) * 4;
        if (!keys)
        {
            return Result::Ok;
        }
        low = 0;
        high = keys - 1;
        while (low < high)
        {
            const auto middle = (low + high) / 2;
            if (ticks <= values_[keyOffset + middle * 8])
            {
                high = middle;
            }
            else
            {
                low = middle + 1;
            }
        }
        const auto *right = values_.data() + keyOffset + low * 8;
        const auto *left = low ? right - 8 : right;
        const auto t = right[0] > left[0] ? std::clamp((ticks - left[0]) / (right[0] - left[0]), 0.0, 1.0) : 0.0;
        const auto mix = [&](std::size_t i) { return left[i] + (right[i] - left[i]) * t; };
        out.x = mix(1);
        out.y = mix(2);
        out.rotation = mix(3);
        out.scaleX *= mix(4);
        out.scaleY *= mix(5);
        return Result::Ok;
    }
};
} // namespace liberafx
