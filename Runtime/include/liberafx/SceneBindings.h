#pragma once

#include <algorithm>
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

inline auto sampleSpriteRect(std::uint32_t frameIndex, float u, float v, float width, float height,
                             AtlasFrame& out) noexcept -> bool
{
    if (!std::isfinite(u) || !std::isfinite(v) || !std::isfinite(width) || !std::isfinite(height) ||
        u < 0 || v < 0 || width <= 0 || height <= 0 ||
        static_cast<double>(u) + width > 1.000001 || static_cast<double>(v) + height > 1.000001)
        return false;
    out.frameIndex = frameIndex;
    out.uvRect[0] = u;
    out.uvRect[1] = v;
    out.uvRect[2] = width;
    out.uvRect[3] = height;
    return true;
}

inline auto sampleSpriteSequence(double frameInput, double elapsedSeconds, double fps,
                                 const double* durationFrames, std::uint32_t frameCount,
                                 std::uint32_t startFrame, std::uint32_t endFrame,
                                 bool loop, bool playing, std::uint32_t& outFrame) noexcept -> bool
{
    if (!durationFrames || frameCount == 0 || frameCount > 4096 ||
        startFrame > endFrame || endFrame >= frameCount ||
        !std::isfinite(fps) || fps <= 0 ||
        !std::isfinite(frameInput) ||
        (playing && (!std::isfinite(elapsedSeconds) || elapsedSeconds < 0)))
        return false;
    auto total = 0.0;
    for (auto index = startFrame; index <= endFrame; ++index)
    {
        auto duration = durationFrames[index];
        if (!std::isfinite(duration) || duration <= 0 || duration > 1'000'000) return false;
        total += duration;
    }
    if (!std::isfinite(total) || total <= 0) return false;
    if (frameInput >= 0)
    {
        auto requested = std::floor(frameInput);
        auto index = 0.0;
        auto length = static_cast<double>(endFrame - startFrame + 1);
        if (loop)
        {
            auto relative = std::fmod(requested - startFrame, length);
            if (relative < 0) relative += length;
            index = startFrame + relative;
        }
        else
            index = std::clamp(requested, static_cast<double>(startFrame), static_cast<double>(endFrame));
        outFrame = static_cast<std::uint32_t>(index);
        return true;
    }
    auto ticks = playing ? elapsedSeconds * fps : 0.0;
    if (!std::isfinite(ticks)) return false;
    if (loop) ticks = std::fmod(ticks, total);
    else ticks = std::min(ticks, std::nextafter(total, 0.0));
    for (auto index = startFrame; index <= endFrame; ++index)
    {
        if (ticks < durationFrames[index])
        {
            outFrame = index;
            return true;
        }
        ticks -= durationFrames[index];
    }
    outFrame = endFrame;
    return true;
}

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
    return sampleSpriteRect(index, static_cast<float>(index % columns) / columns,
        static_cast<float>(index / columns) / rows, 1.0f / columns, 1.0f / rows, out);
}

struct SpritePose final {
    double x = 0, y = 0, rotationDegrees = 0, scaleX = 1, scaleY = 1;
};

struct SpritePoseKey final {
    double frame = 0;
    SpritePose pose{};
};

// Draws from a single composition use the same elapsed time and clip duration.
// The final cell and pose are held until the shared clock resets.
template<class Key, class ReadKey>
inline auto sampleSpriteComposition(double elapsedSeconds, double fps, double durationFrames, bool loop,
    const double* durations, std::uint32_t frameCount, const Key* keys, std::uint32_t keyCount,
    std::uint32_t& outFrame, SpritePose& outPose, ReadKey readKey) noexcept -> bool
{
    if (!std::isfinite(elapsedSeconds) || elapsedSeconds < 0 ||
        !std::isfinite(fps) || fps <= 0 || fps > 10000 ||
        !std::isfinite(durationFrames) || durationFrames < 1 || durationFrames > 4'096'000'000.0 ||
        !durations || frameCount < 1 || frameCount > 4096 ||
        (keyCount != 0 && !keys) || keyCount > 256)
        return false;
    auto ticks = elapsedSeconds * fps;
    if (!std::isfinite(ticks)) return false;
    ticks = loop ? std::fmod(ticks, durationFrames) : std::min(ticks, durationFrames);
    auto frame = frameCount - 1;
    auto total = 0.0;
    for (auto index = 0u; index < frameCount; ++index)
    {
        const auto duration = durations[index];
        if (!std::isfinite(duration) || duration <= 0 || duration > 1'000'000) return false;
        const auto previous = total;
        total += duration;
        if (previous <= ticks && ticks < total) frame = index;
    }
    if (total > durationFrames) return false;
    auto previous = -1.0;
    auto lastPose = SpritePose{};
    const auto bounded = [](double value) { return std::isfinite(value) && std::abs(value) <= 1'000'000; };
    const auto validScale = [](double value) { return std::isfinite(value) && value != 0 && std::abs(value) <= 10'000; };
    auto pose = SpritePose{};
    for (auto index = 0u; index < keyCount; ++index)
    {
        const auto key = readKey(keys, index);
        if (!std::isfinite(key.frame) || key.frame < 0 || key.frame <= previous || key.frame > durationFrames ||
            !bounded(key.pose.x) || !bounded(key.pose.y) || !bounded(key.pose.rotationDegrees) ||
            !validScale(key.pose.scaleX) || !validScale(key.pose.scaleY)) return false;
        if (index == 0 || ticks >= key.frame) pose = key.pose;
        else if (ticks > previous)
        {
            const auto& left = lastPose;
            const auto t = (ticks - previous) / (key.frame - previous);
            const auto mix = [t](double a, double b) { return a + (b - a) * t; };
            pose = {mix(left.x, key.pose.x), mix(left.y, key.pose.y),
                mix(left.rotationDegrees, key.pose.rotationDegrees),
                mix(left.scaleX, key.pose.scaleX), mix(left.scaleY, key.pose.scaleY)};
        }
        previous = key.frame;
        lastPose = key.pose;
    }
    outFrame = frame;
    outPose = pose;
    return true;
}

inline auto sampleSpriteComposition(double elapsedSeconds, double fps, double durationFrames, bool loop,
    const double* durations, std::uint32_t frameCount, const SpritePoseKey* keys, std::uint32_t keyCount,
    std::uint32_t& outFrame, SpritePose& outPose) noexcept -> bool
{
    return sampleSpriteComposition(elapsedSeconds, fps, durationFrames, loop, durations, frameCount, keys,
        keyCount, outFrame, outPose, [](const SpritePoseKey* input, std::uint32_t i) { return input[i]; });
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
