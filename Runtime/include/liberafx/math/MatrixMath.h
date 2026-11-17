#pragma once

#include <array>
#include <cstdint>

namespace liberafx::math {

// Matrices are row-major; vectors are column vectors and transforms compose T * R * S.
using Matrix4 = std::array<double, 16>;
using Vector3 = std::array<double, 3>;
enum class TransformOrder : std::uint8_t { TRS = 0, TSR = 1, RTS = 2, RST = 3, STR = 4, SRT = 5 };

// rotationDegrees is (RollX, PitchY, YawZ). The convention matches the Unreal renderer:
// Rz(yaw) * Ry(-pitch) * Rx(-roll).
auto makeTransform(const Vector3& position, const Vector3& rotationDegrees,
                  const Vector3& scale) noexcept -> Matrix4;

auto makeOrderedTransform(const Vector3& position, const Vector3& rotationDegrees,
                          const Vector3& scale, TransformOrder order) noexcept -> Matrix4;

// Returns false for singular, relatively near-singular, non-finite, or unrepresentable
// results. On failure, inverse is all zeroes.
auto invertMatrix(const Matrix4& matrix, Matrix4& inverse) noexcept -> bool;

// Decomposes affine TRS matrices only. On failure, all output vectors are zeroed.
auto decomposeTransform(const Matrix4& matrix, Vector3& position, Vector3& rotationDegrees,
                        Vector3& scale) noexcept -> bool;

}
