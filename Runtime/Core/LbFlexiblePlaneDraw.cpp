#include "liberafx/LbFlexiblePlaneDraw.h"
#include <cmath>
#include <cfloat>
#include <cstring>

namespace liberafx::flexible_plane {

struct Vec3 { double x, y, z; };
auto subtract(Vec3 a, Vec3 b) -> Vec3 { return {a.x - b.x, a.y - b.y, a.z - b.z}; }
auto cross(Vec3 a, Vec3 b) -> Vec3
{
    return {a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x};
}
auto length(Vec3 a) -> double { return std::sqrt(a.x * a.x + a.y * a.y + a.z * a.z); }
auto position(const LibraFlexiblePoint& p) -> Vec3 { return {p.x, p.y, p.z}; }
auto normalized(Vec3 value) -> Vec3
{
    auto magnitude = length(value);
    if (magnitude <= 1e-12)
    {
        return {};
    }
    return {value.x / magnitude, value.y / magnitude, value.z / magnitude};
}
auto finitePoint(const LibraFlexiblePoint& p) -> bool
{
    return std::isfinite(p.x) && std::isfinite(p.y) && std::isfinite(p.z) &&
        std::isfinite(p.width) && p.width >= 0 && std::isfinite(p.r) &&
        std::isfinite(p.g) && std::isfinite(p.b) && std::isfinite(p.a);
}
auto distinct(const LibraFlexiblePoint& a, const LibraFlexiblePoint& b) -> bool
{
    return length(subtract(position(a), position(b))) > 1e-6;
}
auto nextPoint(const LibraFlexiblePoint* points, uint32_t count, uint32_t current) -> uint32_t
{
    auto next = current + 1;
    while (next < count && !distinct(points[current], points[next]))
    {
        ++next;
    }
    return next;
}
auto validFloat(double value) -> bool
{
    return std::isfinite(value) && std::abs(value) <= FLT_MAX;
}
auto validHistory(const LibraFlexibleHistory& history, const LibraFlexibleTimedPoint* storage) -> bool
{
    for (auto i = 0u; i < history.count; ++i)
    {
        if (!std::isfinite(storage[i].time) || !finitePoint(storage[i].point) ||
            (i && storage[i].time <= storage[i - 1].time))
        {
            return false;
        }
    }
    return history.count == 0 || storage[history.count - 1].time == history.lastTime;
}

auto generatePass(const LibraFlexiblePoint* points, uint32_t pointCount,
                  const LibraFlexiblePlaneOptions& options, double total,
                  LibraSpriteVertex* vertices, uint32_t* indices) -> bool
{
    auto previous = 0u;
    auto current = 0u;
    auto next = nextPoint(points, pointCount, current);
    auto distance = 0.0;
    auto vertexIndex = 0u;
    while (true)
    {
        auto p = position(points[current]);
        auto direction = next < pointCount ? subtract(position(points[next]), p)
                                           : subtract(p, position(points[previous]));
        if (current != 0 && next < pointCount)
        {
            auto smooth = subtract(position(points[next]), position(points[previous]));
            if (length(smooth) > 1e-12)
            {
                direction = smooth;
            }
        }
        auto tangent = normalized(direction);
        auto reference = options.facing == 0 ? Vec3{options.upX, options.upY, options.upZ}
            : subtract(Vec3{options.cameraX, options.cameraY, options.cameraZ}, p);
        auto side = normalized(cross(tangent, reference));
        if (length(side) <= 1e-12)
        {
            auto axis = std::abs(tangent.x) <= std::abs(tangent.y) &&
                std::abs(tangent.x) <= std::abs(tangent.z) ? Vec3{1, 0, 0}
                : std::abs(tangent.y) <= std::abs(tangent.z) ? Vec3{0, 1, 0} : Vec3{0, 0, 1};
            side = normalized(cross(tangent, axis));
        }
        auto half = static_cast<double>(points[current].width) * 0.5;
        auto u = options.uvMode == 0 ? distance / total : distance / options.repeatLength;
        auto left = Vec3{p.x - side.x * half, p.y - side.y * half, p.z - side.z * half};
        auto right = Vec3{p.x + side.x * half, p.y + side.y * half, p.z + side.z * half};
        if (!validFloat(u) || !validFloat(left.x) || !validFloat(left.y) || !validFloat(left.z) ||
            !validFloat(right.x) || !validFloat(right.y) || !validFloat(right.z))
        {
            return false;
        }
        if (vertices)
        {
            const auto& c = points[current];
            vertices[vertexIndex] = {static_cast<float>(left.x), static_cast<float>(left.y),
                static_cast<float>(left.z), static_cast<float>(u), 0, c.r, c.g, c.b, c.a};
            vertices[vertexIndex + 1] = {static_cast<float>(right.x), static_cast<float>(right.y),
                static_cast<float>(right.z), static_cast<float>(u), 1, c.r, c.g, c.b, c.a};
            if (vertexIndex)
            {
                auto index = (vertexIndex / 2 - 1) * 6;
                indices[index] = vertexIndex - 2;
                indices[index + 1] = vertexIndex - 1;
                indices[index + 2] = vertexIndex;
                indices[index + 3] = vertexIndex;
                indices[index + 4] = vertexIndex - 1;
                indices[index + 5] = vertexIndex + 1;
            }
        }
        vertexIndex += 2;
        if (next == pointCount)
        {
            break;
        }
        distance += length(subtract(position(points[next]), p));
        previous = current;
        current = next;
        next = nextPoint(points, pointCount, current);
    }
    return true;
}
}

using namespace liberafx::flexible_plane;

LibraResult LIBRA_INTEROP_CALL libra_flexible_plane_generate(
    const LibraFlexiblePoint* points, uint32_t pointCount, const LibraFlexiblePlaneOptions* options,
    LibraSpriteVertex* vertices, uint32_t vertexCapacity, uint32_t* indices,
    uint32_t indexCapacity, LibraFlexiblePlaneCounts* counts)
{
    if (!counts)
    {
        return LIBRA_INVALID_ARGUMENT;
    }
    *counts = {};
    if (!options || pointCount > LIBRA_FLEXIBLE_MAX_POINTS || (pointCount && !points) ||
        options->facing > 1 || options->uvMode > 1 || !std::isfinite(options->upX) ||
        !std::isfinite(options->upY) || !std::isfinite(options->upZ) ||
        !std::isfinite(options->cameraX) || !std::isfinite(options->cameraY) ||
        !std::isfinite(options->cameraZ) || !std::isfinite(options->repeatLength) ||
        (options->uvMode == 1 && options->repeatLength <= 0))
    {
        return LIBRA_INVALID_ARGUMENT;
    }
    for (auto i = 0u; i < pointCount; ++i)
    {
        if (!finitePoint(points[i]))
        {
            return LIBRA_INVALID_ARGUMENT;
        }
    }
    if (pointCount < 2)
    {
        return LIBRA_OK;
    }
    auto count = 1u;
    auto total = 0.0;
    auto current = 0u;
    while (true)
    {
        auto next = nextPoint(points, pointCount, current);
        if (next == pointCount)
        {
            break;
        }
        total += length(subtract(position(points[next]), position(points[current])));
        ++count;
        current = next;
    }
    if (count < 2)
    {
        return LIBRA_OK;
    }
    if (!generatePass(points, pointCount, *options, total, nullptr, nullptr))
    {
        return LIBRA_ARITHMETIC_OVERFLOW;
    }
    counts->vertices = count * 2;
    counts->indices = (count - 1) * 6;
    if (vertexCapacity < counts->vertices || indexCapacity < counts->indices ||
        !vertices || !indices)
    {
        return LIBRA_INSUFFICIENT_WORK;
    }
    generatePass(points, pointCount, *options, total, vertices, indices);
    return LIBRA_OK;
}

void LIBRA_INTEROP_CALL libra_flexible_history_reset(LibraFlexibleHistory* history)
{
    if (history)
    {
        *history = {};
    }
}

LibraResult LIBRA_INTEROP_CALL libra_flexible_history_update(
    LibraFlexibleHistory* history, LibraFlexibleTimedPoint* storage, uint32_t capacity,
    double time, double maxAge, const LibraFlexiblePoint* point)
{
    if (!history || !storage || !point || capacity == 0 || capacity > LIBRA_FLEXIBLE_MAX_POINTS ||
        history->count > capacity || history->reserved != 0 || !std::isfinite(time) ||
        !std::isfinite(maxAge) || maxAge <= 0 || !finitePoint(*point) ||
        (history->count && (!std::isfinite(history->lastTime) || !validHistory(*history, storage))))
    {
        return LIBRA_INVALID_ARGUMENT;
    }
    auto sample = *point;
    if (history->count && time < history->lastTime)
    {
        history->count = 0;
    }
    auto removed = 0u;
    while (removed < history->count && time - storage[removed].time > maxAge)
    {
        ++removed;
    }
    if (removed)
    {
        history->count -= removed;
        std::memmove(storage, storage + removed, history->count * sizeof(*storage));
    }
    if (history->count && time == history->lastTime)
    {
        storage[history->count - 1] = {time, sample};
    }
    else
    {
        if (history->count == capacity)
        {
            --history->count;
            std::memmove(storage, storage + 1, history->count * sizeof(*storage));
        }
        storage[history->count++] = {time, sample};
    }
    history->lastTime = time;
    return LIBRA_OK;
}

LibraResult LIBRA_INTEROP_CALL libra_flexible_history_copy(
    const LibraFlexibleHistory* history, const LibraFlexibleTimedPoint* storage,
    uint32_t capacity, LibraFlexiblePoint* points, uint32_t pointCapacity, uint32_t* count)
{
    if (!history || !count || history->count > capacity || capacity > LIBRA_FLEXIBLE_MAX_POINTS ||
        history->reserved != 0 || (history->count && (!storage || !validHistory(*history, storage))))
    {
        return LIBRA_INVALID_ARGUMENT;
    }
    *count = history->count;
    if (pointCapacity < history->count || (history->count && !points))
    {
        return LIBRA_INSUFFICIENT_WORK;
    }
    for (auto i = 0u; i < history->count; ++i)
    {
        points[i] = storage[i].point;
    }
    return LIBRA_OK;
}
