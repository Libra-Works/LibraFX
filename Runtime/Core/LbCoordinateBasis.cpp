#include "liberafx/LbCoordinateBasis.h"
#include <algorithm>
#include <cmath>

namespace liberafx {
auto validCoordinateBasis(const CoordinateBasis& basis) noexcept -> bool {
    if (!std::isfinite(basis.unitsPerMetre) || basis.unitsPerMetre < 1e-9 || basis.unitsPerMetre > 1e9) return false;
    auto used = 0u;
    for (const auto axis : basis.axes) {
        if (axis < -3 || axis > 3 || axis == 0) return false;
        const auto bit = 1u << (std::abs(axis) - 1);
        if (used & bit) return false;
        used |= bit;
    }
    return used == 7;
}

auto convertCoordinates(const CoordinateBasis& basis, SpatialValueKind kind,
    bool toHost, const double* source, std::size_t width, double* out) noexcept -> Result {
    if (!validCoordinateBasis(basis) || !source || !out || static_cast<unsigned>(kind) > 6 ||
        width != (kind == SpatialValueKind::Transform || kind == SpatialValueKind::CanonicalDrawTransform ? 16u : 3u))
        return Result::InvalidArgument;
    if (!std::all_of(source, source + width, [](double value) { return std::isfinite(value); })) return Result::NonFinite;
    auto result = std::array<double, 16>{};
    const auto units = basis.unitsPerMetre;
    if (width == 3) {
        auto determinant = 1;
        for (auto i = 0u; i < 3; ++i) {
            if (basis.axes[i] < 0) determinant = -determinant;
            for (auto j = i + 1; j < 3; ++j)
                if (std::abs(basis.axes[i]) > std::abs(basis.axes[j])) determinant = -determinant;
        }
        const auto physical = kind == SpatialValueKind::Position || kind == SpatialValueKind::Vector;
        const auto scale = physical ? (toHost ? units : 1 / units) : 1;
        const auto handedness = kind == SpatialValueKind::AngularVector ? determinant : 1;
        for (auto i = 0u; i < 3; ++i) {
            const auto axis = static_cast<unsigned>(std::abs(basis.axes[i]) - 1);
            const auto sign = basis.axes[i] < 0 ? -1.0 : 1.0;
            if (toHost) result[axis] = source[i] * sign * scale * handedness;
            else result[i] = source[axis] * sign * scale * handedness;
        }
    } else {
        if (std::abs(source[12]) > 1e-9 || std::abs(source[13]) > 1e-9 ||
            std::abs(source[14]) > 1e-9 || std::abs(source[15] - 1) > 1e-9) return Result::InvalidArgument;
        auto forward = std::array<double, 16>{}, inverse = std::array<double, 16>{};
        forward[15] = inverse[15] = 1;
        for (auto i = 0u; i < 3; ++i) {
            const auto axis = static_cast<unsigned>(std::abs(basis.axes[i]) - 1);
            const auto sign = basis.axes[i] < 0 ? -1.0 : 1.0;
            forward[axis * 4 + i] = sign * units;
            inverse[i * 4 + axis] = sign / units;
        }
        const auto& left = toHost ? forward : inverse;
        auto intermediate = std::array<double, 16>{};
        for (auto row = 0u; row < 4; ++row)
            for (auto column = 0u; column < 4; ++column)
                for (auto lane = 0u; lane < 4; ++lane)
                    intermediate[row * 4 + column] += left[row * 4 + lane] * source[lane * 4 + column];
        if (kind == SpatialValueKind::CanonicalDrawTransform) result = intermediate;
        else {
            const auto& right = toHost ? inverse : forward;
            for (auto row = 0u; row < 4; ++row)
                for (auto column = 0u; column < 4; ++column)
                    for (auto lane = 0u; lane < 4; ++lane)
                        result[row * 4 + column] += intermediate[row * 4 + lane] * right[lane * 4 + column];
        }
    }
    if (!std::all_of(result.begin(), result.begin() + width, [](double value) { return std::isfinite(value); })) return Result::NonFinite;
    std::copy_n(result.begin(), width, out);
    return Result::Ok;
}
}
