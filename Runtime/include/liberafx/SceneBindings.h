#pragma once

#include <array>
#include <cmath>
#include <cstdint>
#include <limits>
#include "liberafx/platform/RenderQueue.h"

namespace liberafx {

struct AtlasFrame final {
    std::uint32_t frameIndex = 0;
    float uvRect[4]{0, 0, 1, 1};
};

// Frame zero is the top-left cell. Buffer allocation and UV orientation conversion
// remain adapter-owned; a static quad can animate entirely through shader uniforms.
inline auto sampleSpriteAtlas(double frame, std::uint32_t columns, std::uint32_t rows,
                              AtlasFrame& out) noexcept -> bool
{
    auto count = std::uint64_t{columns} * rows;
    if (!std::isfinite(frame) || columns == 0 || rows == 0 ||
        count > std::numeric_limits<std::uint32_t>::max()) return false;
    auto wrapped = std::fmod(std::floor(frame), static_cast<double>(count));
    if (wrapped < 0) wrapped += static_cast<double>(count);
    auto index = static_cast<std::uint32_t>(wrapped);
    out.frameIndex = index;
    out.uvRect[0] = static_cast<float>(index % columns) / columns;
    out.uvRect[1] = static_cast<float>(index / columns) / rows;
    out.uvRect[2] = 1.0f / columns;
    out.uvRect[3] = 1.0f / rows;
    return true;
}

inline auto makeSpriteQuad(const AtlasFrame& frame, float width, float height,
                           RenderVertex (&vertices)[4]) noexcept -> bool
{
    if (!std::isfinite(width) || !std::isfinite(height) || width <= 0 || height <= 0) return false;
    for (auto value : frame.uvRect)
        if (!std::isfinite(value) || value < 0 || value > 1) return false;
    if (frame.uvRect[0] + frame.uvRect[2] > 1.000001f ||
        frame.uvRect[1] + frame.uvRect[3] > 1.000001f) return false;
    constexpr auto coordinates = std::array{std::array{-0.5f, 0.5f}, std::array{0.5f, 0.5f},
        std::array{0.5f, -0.5f}, std::array{-0.5f, -0.5f}};
    constexpr auto uv = std::array{std::array{0.0f, 0.0f}, std::array{1.0f, 0.0f},
        std::array{1.0f, 1.0f}, std::array{0.0f, 1.0f}};
    for (auto index = 0u; index < 4; ++index)
    {
        vertices[index] = {};
        vertices[index].position[0] = coordinates[index][0] * width;
        vertices[index].position[1] = coordinates[index][1] * height;
        vertices[index].uv[0] = frame.uvRect[0] + uv[index][0] * frame.uvRect[2];
        vertices[index].uv[1] = frame.uvRect[1] + uv[index][1] * frame.uvRect[3];
    }
    return true;
}

}
