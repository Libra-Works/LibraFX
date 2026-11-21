#pragma once
#include <algorithm>
#include <cmath>
#include <cstdint>

namespace liberafx {
struct ParticleAtlas final {
    bool enabled = false, loop = true, overLife = false;
    std::uint32_t columns = 1, rows = 1, startFrame = 0;
    double fps = 0;
    auto valid() const noexcept -> bool {
        return columns >= 1 && columns <= 64 && rows >= 1 && rows <= 64 &&
            columns * rows <= 256 && startFrame < columns * rows && std::isfinite(fps) && fps >= 0 && fps <= 1000;
    }
    auto frame(double age, double life) const noexcept -> std::uint32_t {
        if (!enabled || !valid() || !std::isfinite(age) || !std::isfinite(life)) return 0;
        const auto count = columns * rows;
        age = std::max(0.0,age);
        if (overLife) {
            const auto progress = life > 0 ? std::clamp(age/life,0.0,1.0) : 0.0;
            return std::min(count-1, startFrame + static_cast<std::uint32_t>(progress * (count-startFrame)));
        }
        if (fps == 0) return startFrame;
        if (loop) return (startFrame + static_cast<std::uint32_t>(std::floor(std::fmod(age, count/fps)*fps))) % count;
        return age >= (count-1-startFrame)/fps ? count-1 : startFrame + static_cast<std::uint32_t>(std::floor(age*fps));
    }
};
}
