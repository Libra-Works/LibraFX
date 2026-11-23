#pragma once

#include "liberafx/LbFlexiblePlaneDraw.h"
#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>
#include <functional>
#include <limits>
#include <map>
#include <string>
#include <utility>
#include <vector>

namespace liberafx::flexible_adapter {

using Id = std::array<std::uint8_t, 16>;

struct Input {
    std::string id;
    Id source{};
    std::array<double, 4> defaults{};
    std::uint32_t width = 0, component = 0;
    bool connected = false;
};

struct Definition {
    Id source{};
    std::vector<Input> inputs;
    std::vector<LibraFlexiblePoint> points;
    std::uint32_t capacity = 0;
    double maxAge = 0;
    LibraFlexiblePlaneOptions options{};
    bool history = false;
};

// The supplied read callback executes on the owning engine thread.
using Read = std::function<LibraResult(const Id&, double*, std::uint8_t&)>;

inline auto finiteFloat(double value) -> bool
{
    return std::isfinite(value) && std::abs(value) <= std::numeric_limits<float>::max();
}

inline auto input(const Definition& definition, const std::string& id) -> const Input*
{
    const auto found = std::find_if(definition.inputs.begin(), definition.inputs.end(),
        [&id](const Input& item) { return item.id == id; });
    return found == definition.inputs.end() ? nullptr : &*found;
}

inline auto validate(const Definition& definition) -> bool
{
    const auto has = [&definition](const std::string& id, std::uint32_t width) {
        const auto* item = input(definition, id);
        return item && item->width == width;
    };
    if (std::all_of(definition.source.begin(), definition.source.end(), [](auto value) { return value == 0; }) ||
        !has("width", 1) || !has("tint", 4) || definition.options.facing > 1 ||
        definition.options.uvMode > 1 || !finiteFloat(definition.options.repeatLength) ||
        definition.options.repeatLength <= 0 || !finiteFloat(definition.options.upX) ||
        !finiteFloat(definition.options.upY) || !finiteFloat(definition.options.upZ))
    {
        return false;
    }
    for (auto index = std::size_t{}; index < definition.inputs.size(); ++index)
    {
        const auto& item = definition.inputs[index];
        if (item.id.empty() || item.width == 0 || item.width > 4 ||
            item.component + item.width > 4 ||
            (item.connected && std::all_of(item.source.begin(), item.source.end(),
                [](auto value) { return value == 0; })) ||
            std::count_if(definition.inputs.begin(), definition.inputs.end(),
                [&item](const Input& other) { return item.id == other.id; }) != 1)
        {
            return false;
        }
        for (auto lane = 0u; lane < item.width; ++lane)
        {
            if (!finiteFloat(item.defaults[lane]))
            {
                return false;
            }
        }
    }
    if (definition.inputs.size() > 256)
    {
        return false;
    }
    if (definition.history)
    {
        return definition.points.empty() && definition.capacity >= 2 && definition.capacity <= 4096 &&
            finiteFloat(definition.maxAge) && definition.maxAge > 0 && definition.inputs.size() >= 5 &&
            has("position", 3) && has("pointWidth", 1) && has("pointColor", 4) &&
            input(definition, "width")->defaults[0] >= 0 &&
            input(definition, "pointWidth")->defaults[0] >= 0;
    }
    if (definition.points.size() < 2 || definition.points.size() > 64 ||
        definition.capacity != 0 || definition.maxAge != 0 ||
        definition.inputs.size() < 2 + std::min<std::size_t>(4, definition.points.size()) ||
        input(definition, "width")->defaults[0] < 0)
    {
        return false;
    }
    for (auto index = std::size_t{}; index < std::min<std::size_t>(4, definition.points.size()); ++index)
    {
        if (!has("position" + std::to_string(index), 3))
        {
            return false;
        }
    }
    for (auto index = std::size_t{4}; index < definition.points.size(); ++index)
    {
        if (const auto* extra = input(definition, "position" + std::to_string(index)); extra && extra->width != 3)
        {
            return false;
        }
    }
    for (const auto& p : definition.points)
    {
        if (!finiteFloat(p.x) || !finiteFloat(p.y) || !finiteFloat(p.z) ||
            !finiteFloat(p.width) || p.width < 0 || !finiteFloat(p.r) ||
            !finiteFloat(p.g) || !finiteFloat(p.b) || !finiteFloat(p.a))
        {
            return false;
        }
    }
    return true;
}

inline auto readInput(const Definition& definition, const char* name, const Read& read,
                      std::array<double, 4>& output) -> LibraResult
{
    const auto* item = input(definition, name);
    if (!item)
    {
        return LIBRA_INVALID_BINARY;
    }
    output = item->defaults;
    if (item->connected)
    {
        auto values = std::array<double, 4>{};
        auto width = std::uint8_t{};
        if (!read)
        {
            return LIBRA_INVALID_ARGUMENT;
        }
        const auto result = read(item->source, values.data(), width);
        if (result != LIBRA_OK)
        {
            return result;
        }
        if (width > 4 || item->component + item->width > width)
        {
            return LIBRA_INVALID_BINARY;
        }
        output = {};
        for (auto lane = 0u; lane < item->width; ++lane)
        {
            output[lane] = values[item->component + lane];
        }
    }
    for (auto lane = 0u; lane < item->width; ++lane)
    {
        if (!finiteFloat(output[lane]))
        {
            return LIBRA_NON_FINITE;
        }
    }
    return LIBRA_OK;
}

inline auto sameInput(const Input& a, const Input& b) -> bool
{
    return a.id == b.id && a.source == b.source && a.defaults == b.defaults &&
        a.width == b.width && a.component == b.component && a.connected == b.connected;
}

class HistoryScope final {
    struct Entry {
        LibraFlexibleHistory history{};
        std::vector<LibraFlexibleTimedPoint> storage;
        std::array<Input, 3> inputs;
        double maxAge = 0;
        double sampleTime = 0;
        LibraFlexiblePoint sample{};
        bool sampled = false;
    };
    std::map<Id, Entry> entries_;
public:
    void reset() { entries_.clear(); }
    // Must be called exactly once before processing all draws in one scope update.
    void beginUpdate()
    {
        for (auto& pair : entries_)
        {
            pair.second.sampled = false;
        }
    }
    auto sample(const Definition& definition, double time, const LibraFlexiblePoint& point,
                std::vector<LibraFlexiblePoint>& output, std::uint32_t& count) -> LibraResult
    {
        auto found = entries_.find(definition.source);
        if (found == entries_.end())
        {
            auto entry = Entry{};
            entry.storage.resize(definition.capacity);
            entry.maxAge = definition.maxAge;
            entry.inputs = {*input(definition, "position"), *input(definition, "pointWidth"),
                *input(definition, "pointColor")};
            found = entries_.emplace(definition.source, std::move(entry)).first;
        }
        auto& entry = found->second;
        if (entry.storage.size() != definition.capacity || entry.maxAge != definition.maxAge ||
            !sameInput(entry.inputs[0], *input(definition, "position")) ||
            !sameInput(entry.inputs[1], *input(definition, "pointWidth")) ||
            !sameInput(entry.inputs[2], *input(definition, "pointColor")))
        {
            return LIBRA_INVALID_BINARY;
        }
        if (entry.sampled)
        {
            const auto& prior = entry.sample;
            if (entry.sampleTime != time || prior.x != point.x || prior.y != point.y ||
                prior.z != point.z || prior.width != point.width || prior.r != point.r ||
                prior.g != point.g || prior.b != point.b || prior.a != point.a)
            {
                return LIBRA_INVALID_ARGUMENT;
            }
        }
        else
        {
            const auto result = libra_flexible_history_update(&entry.history, entry.storage.data(),
                static_cast<std::uint32_t>(entry.storage.size()), time, entry.maxAge, &point);
            if (result != LIBRA_OK)
            {
                return result;
            }
            entry.sampled = true;
            entry.sampleTime = time;
            entry.sample = point;
        }
        return libra_flexible_history_copy(&entry.history, entry.storage.data(),
            static_cast<std::uint32_t>(entry.storage.size()), output.data(),
            static_cast<std::uint32_t>(output.size()), &count);
    }
};

class Geometry final {
    std::vector<LibraFlexiblePoint> points_;
    std::vector<LibraSpriteVertex> vertices_;
    std::vector<std::uint32_t> indices_;
    LibraFlexiblePlaneCounts counts_{};
public:
    explicit Geometry(const Definition& definition)
    {
        const auto capacity = definition.history ? definition.capacity : definition.points.size();
        points_.resize(capacity);
        vertices_.resize(capacity * 2);
        indices_.resize((capacity - 1) * 6);
    }
    auto vertices() const -> const std::vector<LibraSpriteVertex>& { return vertices_; }
    auto indices() const -> const std::vector<std::uint32_t>& { return indices_; }
    auto counts() const -> LibraFlexiblePlaneCounts { return counts_; }

    auto update(const Definition& definition, HistoryScope& history, const Read& read, double time,
                float cameraX, float cameraY, float cameraZ) -> LibraResult
    {
        if (!std::isfinite(time) || !finiteFloat(cameraX) || !finiteFloat(cameraY) || !finiteFloat(cameraZ))
        {
            return LIBRA_INVALID_ARGUMENT;
        }
        auto position = std::array<double, 4>{}, color = std::array<double, 4>{}, width = std::array<double, 4>{};
        auto count = std::uint32_t{};
        auto result = LibraResult{LIBRA_OK};
        if (definition.history)
        {
            result = readInput(definition, "position", read, position);
            if (result == LIBRA_OK)
            {
                result = readInput(definition, "pointWidth", read, width);
            }
            if (result == LIBRA_OK)
            {
                result = readInput(definition, "pointColor", read, color);
            }
            if (result != LIBRA_OK || width[0] < 0)
            {
                return result == LIBRA_OK ? LIBRA_INVALID_ARGUMENT : result;
            }
            auto point = LibraFlexiblePoint{static_cast<float>(position[0]), static_cast<float>(position[1]),
                static_cast<float>(position[2]), static_cast<float>(width[0]), static_cast<float>(color[0]),
                static_cast<float>(color[1]), static_cast<float>(color[2]), static_cast<float>(color[3])};
            result = history.sample(definition, time, point, points_, count);
            if (result != LIBRA_OK)
            {
                return result;
            }
        }
        else
        {
            std::copy(definition.points.begin(), definition.points.end(), points_.begin());
            count = static_cast<std::uint32_t>(definition.points.size());
            for (auto index = 0u; index < count; ++index)
            {
                const auto name = "position" + std::to_string(index);
                if (index >= 4u && !input(definition, name))
                {
                    continue;
                }
                result = readInput(definition, name.c_str(), read, position);
                if (result != LIBRA_OK)
                {
                    return result;
                }
                points_[index].x = static_cast<float>(position[0]);
                points_[index].y = static_cast<float>(position[1]);
                points_[index].z = static_cast<float>(position[2]);
            }
        }
        result = readInput(definition, "width", read, width);
        if (result == LIBRA_OK)
        {
            result = readInput(definition, "tint", read, color);
        }
        if (result != LIBRA_OK || width[0] < 0)
        {
            return result == LIBRA_OK ? LIBRA_INVALID_ARGUMENT : result;
        }
        for (auto index = 0u; index < count; ++index)
        {
            auto& p = points_[index];
            const auto values = std::array<double, 5>{p.width * width[0], p.r * color[0],
                p.g * color[1], p.b * color[2], p.a * color[3]};
            if (!std::all_of(values.begin(), values.end(), finiteFloat))
            {
                return LIBRA_ARITHMETIC_OVERFLOW;
            }
            p.width = static_cast<float>(values[0]);
            p.r = static_cast<float>(values[1]);
            p.g = static_cast<float>(values[2]);
            p.b = static_cast<float>(values[3]);
            p.a = static_cast<float>(values[4]);
        }
        auto options = definition.options;
        options.cameraX = cameraX;
        options.cameraY = cameraY;
        options.cameraZ = cameraZ;
        return libra_flexible_plane_generate(points_.data(), count, &options, vertices_.data(),
            static_cast<std::uint32_t>(vertices_.size()), indices_.data(),
            static_cast<std::uint32_t>(indices_.size()), &counts_);
    }
};
}
