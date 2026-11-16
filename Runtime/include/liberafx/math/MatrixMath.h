#pragma once

#include <array>

namespace liberafx::math {

// Matrices are row-major; vectors are column vectors and transforms compose T * R * S.
using Matrix4 = std::array<double, 16>;
using Vector3 = std::array<double, 3>;

// rotationDegrees is (RollX, PitchY, YawZ). The convention matches the Unreal renderer:
// Rz(yaw) * Ry(-pitch) * Rx(-roll).
auto makeTransform(const Vector3& position, const Vector3& rotationDegrees,
                  const Vector3& scale) noexcept -> Matrix4;

// Returns false for singular, relatively near-singular, non-finite, or unrepresentable
// results. On failure, inverse is all zeroes.
auto invertMatrix(const Matrix4& matrix, Matrix4& inverse) noexcept -> bool;

// Decomposes affine TRS matrices only. On failure, all output vectors are zeroed.
auto decomposeTransform(const Matrix4& matrix, Vector3& position, Vector3& rotationDegrees,
                        Vector3& scale) noexcept -> bool;

}
