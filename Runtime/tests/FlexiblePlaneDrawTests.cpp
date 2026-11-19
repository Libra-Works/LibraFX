#include "liberafx/LbFlexiblePlaneDraw.h"
#ifdef NDEBUG
#undef NDEBUG
#endif
#include <array>
#include <cassert>
#include <cmath>
#include <cstring>
#include <limits>

int main()
{
    auto points = std::array<LibraFlexiblePoint, 4>{{
        {0, 0, 0, 2, 1, 0, 0, 1},
        {1, 0, 0, 4, 0, 1, 0, 1},
        {1, 0, 0, 9, 1, 1, 1, 1},
        {3, 0, 0, 6, 0, 0, 1, 1}
    }};
    auto options = LibraFlexiblePlaneOptions{0, 0, 0, 1, 0, 0, 0, 5, 1};
    auto vertices = std::array<LibraSpriteVertex, 6>{};
    auto indices = std::array<uint32_t, 12>{};
    auto counts = LibraFlexiblePlaneCounts{};
    vertices[0].x = 123;
    indices[0] = 123;
    assert(libra_flexible_plane_generate(points.data(), 4, &options, vertices.data(), 5,
        indices.data(), 12, &counts) == LIBRA_INSUFFICIENT_WORK);
    assert(counts.vertices == 6 && counts.indices == 12 && vertices[0].x == 123 && indices[0] == 123);
    assert(libra_flexible_plane_generate(points.data(), 4, &options, vertices.data(), 6,
        indices.data(), 12, &counts) == LIBRA_OK);
    assert(vertices[0].y == 0 && vertices[0].z == -1 && vertices[1].z == 1);
    assert(std::abs(vertices[2].u - 1.0f / 3.0f) < 1e-6f && vertices[4].u == 1);
    assert(vertices[2].g == 1 && vertices[4].b == 1);
    auto expected = std::array<uint32_t, 12>{0, 1, 2, 2, 1, 3, 2, 3, 4, 4, 3, 5};
    assert(indices == expected);
    options.uvMode = 1;
    options.repeatLength = 2;
    assert(libra_flexible_plane_generate(points.data(), 4, &options, vertices.data(), 6,
        indices.data(), 12, &counts) == LIBRA_OK);
    assert(vertices[2].u == 0.5f && vertices[4].u == 1.5f);
    options.facing = 1;
    options.cameraZ = 10;
    assert(libra_flexible_plane_generate(points.data(), 4, &options, vertices.data(), 6,
        indices.data(), 12, &counts) == LIBRA_OK);
    assert(std::abs(vertices[0].y) == 1);
    options.cameraZ = 0; // camera coincides with first point: deterministic fallback
    assert(libra_flexible_plane_generate(points.data(), 4, &options, vertices.data(), 6,
        indices.data(), 12, &counts) == LIBRA_OK);
    points[1].x = std::numeric_limits<float>::quiet_NaN();
    vertices[0].x = 123;
    assert(libra_flexible_plane_generate(points.data(), 4, &options, vertices.data(), 6,
        indices.data(), 12, &counts) == LIBRA_INVALID_ARGUMENT && vertices[0].x == 123);
    points[1].x = 1;
    points[0].width = std::numeric_limits<float>::max();
    points[0].x = std::numeric_limits<float>::max();
    options.facing = 0;
    options.upX = 0;
    options.upY = 1;
    options.upZ = 0;
    options.repeatLength = std::numeric_limits<float>::min();
    assert(libra_flexible_plane_generate(points.data(), 4, &options, vertices.data(), 6,
        indices.data(), 12, &counts) == LIBRA_ARITHMETIC_OVERFLOW && vertices[0].x == 123);
    points[0] = {0, 0, 0, 2, 1, 0, 0, 1};
    points[1] = points[0];
    points[2] = points[0];
    points[3] = points[0];
    assert(libra_flexible_plane_generate(points.data(), 4, &options, nullptr, 0,
        nullptr, 0, &counts) == LIBRA_OK && counts.vertices == 0);

    auto state = LibraFlexibleHistory{};
    auto storage = std::array<LibraFlexibleTimedPoint, 3>{};
    auto output = std::array<LibraFlexiblePoint, 3>{};
    auto count = uint32_t{};
    auto point = LibraFlexiblePoint{0, 0, 0, 1, 1, 1, 1, 1};
    for (auto i = 0u; i < 4; ++i)
    {
        point.x = static_cast<float>(i);
        assert(libra_flexible_history_update(&state, storage.data(), 3, i, 10, &point) == LIBRA_OK);
    }
    assert(state.count == 3 && storage[0].point.x == 1);
    point.x = 9;
    assert(libra_flexible_history_update(&state, storage.data(), 3, 3, 10, &point) == LIBRA_OK);
    assert(state.count == 3 && storage[2].point.x == 9);
    output[0].x = 123;
    assert(libra_flexible_history_copy(&state, storage.data(), 3, output.data(), 2, &count)
        == LIBRA_INSUFFICIENT_WORK && count == 3 && output[0].x == 123);
    assert(libra_flexible_history_update(&state, storage.data(), 3, 4, 0.5, &point) == LIBRA_OK);
    assert(state.count == 1 && storage[0].time == 4);
    assert(libra_flexible_history_update(&state, storage.data(), 3, 1, 1, &point) == LIBRA_OK);
    assert(state.count == 1 && storage[0].time == 1);
    storage[0].time = std::numeric_limits<double>::quiet_NaN();
    assert(libra_flexible_history_update(&state, storage.data(), 3, 2, 1, &point)
        == LIBRA_INVALID_ARGUMENT && state.count == 1);
    storage[0].time = 1;
    auto other = LibraFlexibleHistory{};
    auto otherStorage = std::array<LibraFlexibleTimedPoint, 3>{};
    assert(libra_flexible_history_update(&other, otherStorage.data(), 3, 5, 1, &point) == LIBRA_OK);
    libra_flexible_history_reset(&state);
    assert(state.count == 0 && other.count == 1 && otherStorage[0].time == 5);
    assert(libra_flexible_history_copy(&state, storage.data(), 3, output.data(), 3, &count)
        == LIBRA_OK && count == 0);
}
