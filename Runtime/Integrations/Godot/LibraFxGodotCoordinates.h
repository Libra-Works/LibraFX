#pragma once
#include "liberafx/LbCoordinateBasis.h"
#include <godot_cpp/variant/transform3d.hpp>
#include <cmath>
#include <limits>

namespace liberafx::godot_adapter {
inline auto effectToHost(const CoordinateBasis& coordinates, const godot::Transform3D& source,
    godot::Transform3D& output) -> Result {
    auto lanes = std::array<double, 16>{};
    for (auto row = 0u; row < 3; ++row) {
        for (auto column = 0u; column < 3; ++column) lanes[row * 4 + column] = source.basis[row][column];
        lanes[row * 4 + 3] = source.origin[row];
    }
    lanes[15] = 1;
    const auto result = convertCoordinates(coordinates, SpatialValueKind::CanonicalDrawTransform, true,
        lanes.data(), lanes.size(), lanes.data());
    if (result != Result::Ok) return result;
    for (const auto value : lanes)
        if (std::abs(value) > std::numeric_limits<godot::real_t>::max()) return Result::NonFinite;
    output = godot::Transform3D(godot::Basis(
        godot::Vector3(lanes[0], lanes[4], lanes[8]),
        godot::Vector3(lanes[1], lanes[5], lanes[9]),
        godot::Vector3(lanes[2], lanes[6], lanes[10])), godot::Vector3(lanes[3], lanes[7], lanes[11]));
    return Result::Ok;
}
}
